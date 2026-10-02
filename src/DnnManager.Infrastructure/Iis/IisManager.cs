using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Web.Administration;

namespace DnnManager.Infrastructure.Iis;

public sealed class IisManager : IIisManager
{
    // App pool names Windows ships with - never delete their shared profiles even if a project
    // were (pathologically) named the same.
    private static readonly HashSet<string> ReservedAppPools = new(StringComparer.OrdinalIgnoreCase)
    {
        "DefaultAppPool", "Classic .NET AppPool", ".NET v2.0", ".NET v2.0 Classic", ".NET v4.5", ".NET v4.5 Classic"
    };

    // Windows' ERROR_SERVICE_DOES_NOT_EXIST.
    private const int ServiceDoesNotExist = 1060;
    // How long StartSite waits for an app pool that is still stopping.
    private static readonly TimeSpan PoolStopWait = TimeSpan.FromSeconds(20);

    private readonly ProcessRunner _proc;
    private readonly ILogger<IisManager> _log;

    public IisManager(ProcessRunner proc, ILogger<IisManager> log)
    {
        _proc = proc;
        _log = log;
    }

    public async Task<Result> ControlServerAsync(IisServerAction action, CancellationToken ct)
    {
        var iisreset = Path.Combine(Environment.SystemDirectory, "iisreset.exe");
        if (!File.Exists(iisreset)) return Result.Fail($"iisreset was not found at {iisreset}.");

        var r = await _proc.RunAsync(iisreset, new[] { "/" + action.ToString().ToLowerInvariant() }, ct);
        if (r.Success) return Result.Ok();

        var output = (r.StdErr.Length > 0 ? r.StdErr : r.StdOut).Trim();
        return Result.Fail($"iisreset failed (exit code {r.ExitCode}): {output}");
    }

    public IisServerState GetServerState()
    {
        try
        {
            using var service = new ServiceController("W3SVC");
            return service.Status switch
            {
                ServiceControllerStatus.Running => IisServerState.Running,
                ServiceControllerStatus.Stopped => IisServerState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => IisServerState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending or ServiceControllerStatus.Paused => IisServerState.Stopping,
                _ => IisServerState.Unknown
            };
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: ServiceDoesNotExist })
        {
            return IisServerState.NotInstalled;
        }
        catch (Exception ex)
        {
            // Asking failed - which isn't IIS being absent: the next read asks again.
            _log.LogWarning(ex, "Could not read the IIS service state");
            return IisServerState.Unknown;
        }
    }

    public Result CreateSite(string siteName, string physicalPath, string hostname, int port)
    {
        try
        {
            // Fully tear down any existing site/pool of this name first - stopping and waiting
            // for its worker to exit - so we never re-create on top of a running w3wp that still
            // holds the physical-path files (which would orphan it and race file access).
            RemoveSite(siteName);

            using var sm = new ServerManager();
            var pool = sm.ApplicationPools.Add(siteName);
            pool.ManagedRuntimeVersion = "v4.0";
            pool.ManagedPipelineMode = ManagedPipelineMode.Integrated;

            var site = sm.Sites.Add(siteName, "http", $"*:{port}:{hostname}", physicalPath);
            site.ApplicationDefaults.ApplicationPoolName = siteName;
            sm.CommitChanges();
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IIS CreateSite failed");
            return Result.Fail(ex.Message);
        }
    }

    public Result RemoveSite(string siteName)
    {
        try
        {
            // 1) Stop the site and pool, but don't remove them yet. Stopping a pool only
            //    *initiates* WAS shutdown; the w3wp.exe worker lingers (up to its shutdown
            //    time limit) and keeps file handles open on the site's physical path - the
            //    DNN assemblies in \bin, App_Data, logs. Removing the pool config here would
            //    orphan that still-running worker and let the caller's directory delete race
            //    it ("being used by another process").
            using (var sm = new ServerManager())
            {
                var site = sm.Sites[siteName];
                if (site is not null && site.State != ObjectState.Stopped)
                    try { site.Stop(); } catch { /* already stopping/stopped */ }

                var pool = sm.ApplicationPools[siteName];
                if (pool is not null && pool.State != ObjectState.Stopped)
                    try { pool.Stop(); } catch { /* already stopping/stopped */ }

                sm.CommitChanges();
            }

            // 2) Wait for the worker process to actually exit so it releases its file
            //    handles. Force-kills it if it won't stop gracefully in time.
            WaitForPoolToStop(siteName, TimeSpan.FromSeconds(30));

            // 3) Now it's safe to remove the (stopped) site and pool from config.
            using (var sm = new ServerManager())
            {
                var site = sm.Sites[siteName];
                if (site is not null) sm.Sites.Remove(site);
                var pool = sm.ApplicationPools[siteName];
                if (pool is not null) sm.ApplicationPools.Remove(pool);
                sm.CommitChanges();
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IIS RemoveSite failed");
            return Result.Fail(ex.Message);
        }
    }

    // Polls (with a fresh ServerManager each time so WAS state is re-read) until the pool is
    // Stopped with no live worker processes, i.e. its file handles are released. If the worker
    // hasn't exited by the deadline, kills it so the site's files can be deleted.
    private void WaitForPoolToStop(string poolName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var sm = new ServerManager();
                var pool = sm.ApplicationPools[poolName];
                if (pool is null) return; // already gone

                if (pool.State == ObjectState.Stopped)
                {
                    // -1 so a failed read does NOT look like "0 workers, safe to delete" - we
                    // only return once we've positively observed zero live workers.
                    var workers = -1;
                    try { workers = pool.WorkerProcesses.Count; } catch { /* re-read next poll */ }
                    if (workers == 0) return;
                }
            }
            catch { /* WAS state momentarily unavailable; retry */ }
            Thread.Sleep(250);
        }

        _log.LogWarning("App pool '{Pool}' did not stop within timeout; force-killing its worker(s).", poolName);
        KillWorkerProcesses(poolName);
    }

    private void KillWorkerProcesses(string poolName)
    {
        try
        {
            using var sm = new ServerManager();
            var pool = sm.ApplicationPools[poolName];
            if (pool is null) return;
            foreach (var wp in pool.WorkerProcesses)
            {
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(wp.ProcessId);
                    proc.Kill();
                    proc.WaitForExit(5000);
                    _log.LogWarning("Force-killed w3wp {Pid} for pool {Pool}", wp.ProcessId, poolName);
                }
                catch (Exception ex) { _log.LogWarning(ex, "Could not kill w3wp {Pid}", wp.ProcessId); }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "KillWorkerProcesses failed for {Pool}", poolName); }
    }

    public Result StartSite(string siteName)
    {
        try
        {
            // An app pool that is still stopping - its worker process is on its way out, as right after StopSite -
            // can't be started: IIS refuses. Wait for it to have stopped.
            var deadline = DateTime.UtcNow + PoolStopWait;
            while (PoolState(siteName) == ObjectState.Stopping)
            {
                if (DateTime.UtcNow >= deadline)
                    return Result.Fail("Its app pool is still stopping (the worker process hasn't ended yet) - try again in a moment.");
                Thread.Sleep(250);
            }

            using var sm = new ServerManager();
            var site = sm.Sites[siteName];
            if (site is null) return Result.Fail($"Site '{siteName}' not found");
            // A stopped pool (StopSite stops it too) would leave the started site answering 503.
            if (PoolOf(sm, site) is { } pool && pool.State == ObjectState.Stopped) pool.Start();
            if (site.State is not (ObjectState.Started or ObjectState.Starting)) site.Start();
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(ex.Message); }
    }

    /// <summary>The state of the site's app pool, read afresh; null when it has none or it can't be told.</summary>
    private static ObjectState? PoolState(string siteName)
    {
        using var sm = new ServerManager();
        if (sm.Sites[siteName] is not { } site || PoolOf(sm, site) is not { } pool) return null;
        try { return pool.State; } catch { return null; }
    }

    public Result StopSite(string siteName)
    {
        try
        {
            using var sm = new ServerManager();
            var site = sm.Sites[siteName];
            if (site is null) return Result.Fail($"Site '{siteName}' not found");
            if (site.State is not (ObjectState.Stopped or ObjectState.Stopping)) site.Stop();

            // Stopping the pool ends its worker process (and the memory it holds) - unless another site shares it.
            var pool = PoolOf(sm, site);
            var shared = pool is not null && sm.Sites.Any(s => s.Name != site.Name &&
                string.Equals(s.Applications["/"]?.ApplicationPoolName, pool.Name, StringComparison.OrdinalIgnoreCase));
            if (pool is not null && !shared && pool.State is not (ObjectState.Stopped or ObjectState.Stopping)) pool.Stop();
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(ex.Message); }
    }

    public Result RestartSite(string siteName)
    {
        try
        {
            using var sm = new ServerManager();
            var site = sm.Sites[siteName];
            if (site is null) return Result.Fail($"Site '{siteName}' not found");
            var pool = PoolOf(sm, site);
            if (pool is not null)
            {
                if (pool.State == ObjectState.Started) pool.Recycle();
                else if (pool.State == ObjectState.Stopped) pool.Start();
            }
            if (site.State is not (ObjectState.Started or ObjectState.Starting)) site.Start();
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(ex.Message); }
    }

    public Result RecycleAppPool(string siteName)
    {
        try
        {
            using var sm = new ServerManager();
            var site = sm.Sites[siteName];
            if (site is null) return Result.Fail($"Site '{siteName}' not found");
            if (PoolOf(sm, site) is { State: ObjectState.Started } pool) pool.Recycle();
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(ex.Message); }
    }

    public string? GetLogDirectory(string siteName)
    {
        try
        {
            using var sm = new ServerManager();
            if (sm.Sites[siteName] is not { } site) return null;
            // The site's own setting, else the default for all sites; IIS adds a folder per site, by its ID.
            var directory = site.LogFile.Directory;
            if (string.IsNullOrEmpty(directory)) directory = sm.SiteDefaults.LogFile.Directory;
            if (string.IsNullOrEmpty(directory)) directory = @"%SystemDrive%\inetpub\logs\LogFiles";
            return Path.Combine(Environment.ExpandEnvironmentVariables(directory), $"W3SVC{site.Id}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the log folder of IIS site {Site}", siteName);
            return null;
        }
    }

    public string AppPoolIdentity(string siteName)
    {
        try
        {
            using var sm = new ServerManager();
            var poolName = sm.Sites[siteName] is { } site && PoolOf(sm, site) is { } found ? found.Name : siteName;
            var pool = sm.ApplicationPools[poolName];
            return pool?.ProcessModel.IdentityType switch
            {
                ProcessModelIdentityType.SpecificUser => pool.ProcessModel.UserName,
                ProcessModelIdentityType.NetworkService => @"NT AUTHORITY\NETWORK SERVICE",
                ProcessModelIdentityType.LocalService => @"NT AUTHORITY\LOCAL SERVICE",
                ProcessModelIdentityType.LocalSystem => @"NT AUTHORITY\SYSTEM",
                _ => $@"IIS APPPOOL\{poolName}"
            };
        }
        catch (Exception ex)
        {
            // Every site DNN Manager creates runs as its own pool's identity.
            _log.LogWarning(ex, "Could not read the identity of IIS site {Site}", siteName);
            return $@"IIS APPPOOL\{siteName}";
        }
    }

    public Result EnableUserProfile(string siteName)
    {
        try
        {
            using var sm = new ServerManager();
            var pool = sm.Sites[siteName] is { } site ? PoolOf(sm, site) : sm.ApplicationPools[siteName];
            if (pool is null) return Result.Fail($"IIS site '{siteName}' has no app pool.");
            pool.ProcessModel.LoadUserProfile = true;
            // Points the identity's environment (LOCALAPPDATA…) at its profile - where LocalDB looks for its instance.
            pool.ProcessModel.SetAttributeValue("setProfileEnvironment", true);
            sm.CommitChanges();
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IIS EnableUserProfile failed");
            return Result.Fail(ex.Message);
        }
    }

    private static ApplicationPool? PoolOf(ServerManager sm, Site site) =>
        site.Applications["/"]?.ApplicationPoolName is { Length: > 0 } name ? sm.ApplicationPools[name] : null;

    public bool IsAvailable()
    {
        try
        {
            using var sm = new ServerManager();
            _ = sm.Sites.Count; // force applicationHost.config to load; throws if IIS isn't installed
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IIS is not available on this machine");
            return false;
        }
    }

    public IReadOnlyDictionary<string, string> GetSiteStates()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var sm = new ServerManager();
            foreach (var site in sm.Sites)
            {
                // A site whose state momentarily can't be read still exists - record it as unknown
                // rather than dropping it from the snapshot and reporting "no IIS site".
                string state;
                try { state = site.State.ToString(); } catch { state = "Unknown"; }
                map[site.Name] = state;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read IIS site states");
        }
        return map;
    }

    public IReadOnlyDictionary<string, IisSiteRuntime>? GetSiteRuntimes()
    {
        var map = new Dictionary<string, IisSiteRuntime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var sm = new ServerManager();
            // Several sites can share a pool - read each pool's workers and idle time-out once.
            var workers = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
            var idleTimeouts = new Dictionary<string, TimeSpan?>(StringComparer.OrdinalIgnoreCase);
            foreach (var site in sm.Sites)
            {
                // The state and the workers are asked of the running IIS. While it is stopped they read as stopped;
                // what it can't tell while it runs (a site it doesn't know yet) is unknown - the site still exists.
                string state;
                try { state = site.State.ToString(); } catch { state = "Unknown"; }
                var pool = PoolOf(sm, site);
                string? poolState = null;
                try { poolState = pool?.State.ToString(); } catch { /* unknown */ }
                IReadOnlyList<int> pids = [];
                TimeSpan? idleTimeout = null;
                if (pool is not null && !workers.TryGetValue(pool.Name, out pids!))
                {
                    try { pids = pool.WorkerProcesses.Select(w => w.ProcessId).ToList(); } catch { pids = []; }
                    workers[pool.Name] = pids;
                    // Configuration, not the running IIS - it reads the same while IIS is stopped.
                    try { idleTimeouts[pool.Name] = pool.ProcessModel.IdleTimeout; } catch { idleTimeouts[pool.Name] = null; }
                }
                if (pool is not null) idleTimeout = idleTimeouts.GetValueOrDefault(pool.Name);

                map[site.Name] = new IisSiteRuntime(site.Id, state, pool?.Name ?? "", poolState, pids,
                    site.Bindings.Select(ToBinding).ToList(), PhysicalPathOf(site)) { IdleTimeout = idleTimeout };
            }
        }
        catch (Exception ex)
        {
            // Not "no sites": the caller keeps what it knew.
            _log.LogWarning(ex, "Could not read the IIS sites");
            return null;
        }
        return map;
    }

    // IIS's "Web Service" performance counters have one instance per site, named like it.
    public IReadOnlyDictionary<string, long> GetRequestsServed()
    {
        var served = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string category = "Web Service";
            if (!PerformanceCounterCategory.Exists(category)) return served;
            // Every kind of request (GET, HEAD, POST…) - not "Total Get Requests" alone.
            if (new PerformanceCounterCategory(category).ReadCategory()["Total Method Requests"] is not { } requests) return served;
            foreach (System.Collections.DictionaryEntry entry in requests)
                served[(string)entry.Key] = ((InstanceData)entry.Value!).RawValue;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or FormatException)
        {
            _log.LogWarning(ex, "Could not read the IIS request counters");
        }
        return served;
    }

    public IReadOnlyDictionary<string, SiteTraffic> GetSiteTraffic()
    {
        var traffic = new Dictionary<string, SiteTraffic>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string category = "Web Service";
            if (!PerformanceCounterCategory.Exists(category)) return traffic;
            var data = new PerformanceCounterCategory(category).ReadCategory();
            var received = data["Total Bytes Received"];
            var sent = data["Total Bytes Sent"];
            if (received is null || sent is null) return traffic;
            foreach (System.Collections.DictionaryEntry entry in received)
            {
                var site = (string)entry.Key;
                if (sent[site] is { } sentSample)
                    traffic[site] = new SiteTraffic(((InstanceData)entry.Value!).RawValue, sentSample.RawValue);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or FormatException)
        {
            _log.LogWarning(ex, "Could not read the IIS site traffic counters");
        }
        return traffic;
    }

    private static IisBinding ToBinding(Binding binding)
    {
        var info = binding.BindingInformation ?? "";
        var parts = info.Split(':');
        var (port, host) = BindingParts(info);
        // A web binding is "address:port:host"; other protocols (net.tcp "808:*") have their own form.
        var address = parts.Length >= 3 ? string.Join(':', parts[..^2]) : "*";
        bool certificate;
        try { certificate = binding.Protocol == "https" && binding.CertificateHash is { Length: > 0 }; }
        catch { certificate = false; }
        return new IisBinding(binding.Protocol, address.Length == 0 ? "*" : address, port, host == "*" ? "" : host, certificate);
    }

    /// <summary>The folder the site's root application serves; empty when it has none.</summary>
    private static string PhysicalPathOf(Site site)
    {
        try { return Environment.ExpandEnvironmentVariables(site.Applications["/"]?.VirtualDirectories["/"]?.PhysicalPath ?? ""); }
        catch { return ""; }
    }

    // BindingInformation is "ip:port:host"; the IP can hold colons (IPv6), so read from the end.
    private static (int? Port, string Host) BindingParts(string bindingInformation)
    {
        var parts = bindingInformation.Split(':');
        var host = parts.Length >= 3 && parts[^1].Length > 0 ? parts[^1] : "*";
        return (parts.Length >= 2 && int.TryParse(parts[^2], out var port) ? port : null, host);
    }

    public IisSiteInfo? GetSiteInfo(string siteName)
    {
        try
        {
            using var sm = new ServerManager();
            var site = sm.Sites[siteName];
            if (site is null) return null;

            string state;
            try { state = site.State.ToString(); } catch { state = "Unknown"; }
            var root = site.Applications["/"];
            var physicalPath = Environment.ExpandEnvironmentVariables(root?.VirtualDirectories["/"]?.PhysicalPath ?? "");
            var bindings = site.Bindings.Select(b => BindingParts(b.BindingInformation) is { Port: { } port } parts
                ? $"{b.Protocol}://{parts.Host}:{port}"
                : $"{b.Protocol} {b.BindingInformation}").ToList();

            var poolName = root?.ApplicationPoolName ?? "";
            var pool = sm.ApplicationPools[poolName];
            string? poolState = null;
            try { poolState = pool?.State.ToString(); } catch { /* unknown */ }
            var clr = pool is null ? null : string.IsNullOrEmpty(pool.ManagedRuntimeVersion) ? "No Managed Code" : pool.ManagedRuntimeVersion;
            var identity = pool is null ? null
                : pool.ProcessModel.IdentityType == ProcessModelIdentityType.SpecificUser ? pool.ProcessModel.UserName
                : pool.ProcessModel.IdentityType.ToString();
            return new IisSiteInfo(state, physicalPath, bindings, poolName, poolState, clr,
                pool?.ManagedPipelineMode.ToString(), identity);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read IIS site {Site}", siteName);
            return null;
        }
    }

    public Result GrantPermissions(string path, IEnumerable<string> identities)
    {
        try
        {
            var di = new DirectoryInfo(path);
            var sec = di.GetAccessControl();
            foreach (var id in identities)
            {
                try
                {
                    var rule = new FileSystemAccessRule(
                        ResolveIdentity(id),
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow);
                    sec.AddAccessRule(rule);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Skip identity {Identity}", id);
                }
            }
            di.SetAccessControl(sec);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(ex.Message); }
    }

    public async Task<Result> RemoveAppPoolProfileAsync(string poolName, CancellationToken ct)
    {
        if (ReservedAppPools.Contains(poolName))
            return Result.Ok(); // never touch a built-in pool's shared profile

        try
        {
            var sid = AppPoolSid(poolName).Value;
            // Win32_UserProfile.Delete() removes BOTH the C:\Users\<pool> directory and the
            // ProfileList registry entry. Filter by the app-pool SID (not the folder name) so we
            // only ever delete this pool's own profile. No match => nothing to do.
            var script =
                $"$p = Get-CimInstance Win32_UserProfile -Filter \"SID='{sid}'\" -ErrorAction SilentlyContinue; " +
                "if ($p) { Remove-CimInstance -InputObject $p -ErrorAction Stop }";
            var r = await _proc.RunAsync("powershell.exe",
                new[] { "-NoProfile", "-NonInteractive", "-Command", script }, ct);
            if (!r.Success)
            {
                var err = r.StdErr.Length > 0 ? r.StdErr : r.StdOut;
                _log.LogWarning("Could not delete app-pool profile for {Pool}: {Error}", poolName, err);
                return Result.Fail(err);
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RemoveAppPoolProfile failed for {Pool}", poolName);
            return Result.Fail(ex.Message);
        }
    }

    // "IIS APPPOOL\<name>" virtual accounts often can't be name-translated right after the
    // pool is created. Compute the deterministic app-pool SID instead so the grant always works.
    private static IdentityReference ResolveIdentity(string id)
    {
        const string prefix = "IIS APPPOOL\\";
        if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return AppPoolSid(id[prefix.Length..]);
        return new NTAccount(id);
    }

    // IIS derives an application-pool SID as S-1-5-82-{five little-endian uint32s of
    // SHA1(lowercased pool name encoded as UTF-16LE)}.
    private static SecurityIdentifier AppPoolSid(string appPoolName)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(appPoolName.ToLowerInvariant());
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        var sb = new System.Text.StringBuilder("S-1-5-82");
        for (var i = 0; i < 5; i++)
            sb.Append('-').Append(BitConverter.ToUInt32(hash, i * 4));
        return new SecurityIdentifier(sb.ToString());
    }
}
