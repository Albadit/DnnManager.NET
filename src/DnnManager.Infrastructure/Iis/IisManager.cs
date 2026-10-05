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

public sealed class IisManager(ProcessRunner proc, ILogger<IisManager> log) : IIisManager
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

    private readonly ProcessRunner _proc = proc;
    private readonly ILogger<IisManager> _log = log;

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
            //    A pool named like the site that other sites use too is theirs as well: it is left running and in IIS
            //    (stopping or removing it would take those sites down).
            bool shared;
            using (var sm = new ServerManager())
            {
                shared = PoolUsedByOthers(sm, siteName);
                var site = sm.Sites[siteName];
                if (site is not null && site.State != ObjectState.Stopped)
                    try { site.Stop(); } catch { /* already stopping/stopped */ }

                var pool = shared ? null : sm.ApplicationPools[siteName];
                if (pool is not null && pool.State != ObjectState.Stopped)
                    try { pool.Stop(); } catch { /* already stopping/stopped */ }

                sm.CommitChanges();
            }

            // 2) Wait for the worker process to actually exit so it releases its file
            //    handles. Force-kills it if it won't stop gracefully in time.
            if (!shared) WaitForPoolToStop(siteName, TimeSpan.FromSeconds(30));

            // 3) Now it's safe to remove the (stopped) site and pool from config.
            using (var sm = new ServerManager())
            {
                var site = sm.Sites[siteName];
                if (site is not null) sm.Sites.Remove(site);
                var pool = shared ? null : sm.ApplicationPools[siteName];
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

    private static bool PoolExists(string poolName)
    {
        try
        {
            using var sm = new ServerManager();
            return sm.ApplicationPools[poolName] is not null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // IIS can't be read - the profile is looked at as before
        }
    }

    /// <summary>Whether an application of another site than <paramref name="siteName"/> runs in the pool of that name.</summary>
    private static bool PoolUsedByOthers(ServerManager sm, string siteName) =>
        sm.Sites.Where(s => !s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Applications)
            .Any(a => a.ApplicationPoolName.Equals(siteName, StringComparison.OrdinalIgnoreCase));

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

    public Result StopSiteAndWait(string siteName, TimeSpan timeout)
    {
        var stopped = StopSite(siteName);
        if (!stopped.Success) return stopped;
        string? poolName;
        try
        {
            using var sm = new ServerManager();
            if (sm.Sites[siteName] is not { } site) return Result.Fail($"Site '{siteName}' not found");
            poolName = PoolOf(sm, site)?.Name;
            // A pool another site shares keeps running (StopSite leaves it): its worker process isn't this site's to end.
            if (poolName is null || sm.Sites.Any(s => s.Name != site.Name &&
                    string.Equals(s.Applications["/"]?.ApplicationPoolName, poolName, StringComparison.OrdinalIgnoreCase))) return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(ex.Message); }

        // Stopping, IIS gives the worker process its shutdown time limit (90 seconds by default) to finish - and the
        // pool reads Stopped only once it has gone. Ended by force when it takes longer.
        WaitForPoolToStop(poolName, timeout);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var left = -1;
            try
            {
                using var sm = new ServerManager();
                left = sm.ApplicationPools[poolName]?.WorkerProcesses.Count ?? 0;
            }
            catch { /* re-read */ }
            if (left == 0) return Result.Ok();
            if (DateTime.UtcNow >= deadline)
                return Result.Fail($"The worker process of app pool '{poolName}' still runs - it couldn't be ended (DNN Manager may need to run as administrator).");
            Thread.Sleep(250);
        }
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

    public Result ReplaceHttpBindings(string siteName, IReadOnlyList<(string Host, int Port)> bindings)
    {
        try
        {
            using var sm = new ServerManager();
            if (sm.Sites[siteName] is not { } site) return Result.Fail($"Site '{siteName}' not found");
            foreach (var old in site.Bindings.Where(b => b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase)).ToList())
                site.Bindings.Remove(old);
            foreach (var (host, port) in bindings)
                site.Bindings.Add($"*:{port}:{host}", "http");
            sm.CommitChanges();
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IIS ReplaceHttpBindings failed");
            return Result.Fail(ex.Message);
        }
    }

    public Result SetPoolSettings(string siteName, IisPoolSettings settings)
    {
        try
        {
            using var sm = new ServerManager();
            if (sm.Sites[siteName] is not { } site || PoolOf(sm, site) is not { } pool) return Result.Fail($"IIS site '{siteName}' has no app pool.");
            if (sm.Sites.Any(s => s.Name != site.Name && s.Applications.Any(a => a.ApplicationPoolName.Equals(pool.Name, StringComparison.OrdinalIgnoreCase))))
                return Result.Fail($"The app pool '{pool.Name}' is used by other sites too - change it in IIS Manager.");

            pool.ManagedRuntimeVersion = settings.Runtime;
            pool.ManagedPipelineMode = Enum.Parse<ManagedPipelineMode>(settings.Pipeline, ignoreCase: true);
            pool.Enable32BitAppOnWin64 = settings.Enable32Bit;
            // A specific account keeps its user and password - they're changed in IIS Manager.
            if (Enum.TryParse<ProcessModelIdentityType>(settings.Identity, ignoreCase: true, out var identity) &&
                identity != ProcessModelIdentityType.SpecificUser)
                pool.ProcessModel.IdentityType = identity;
            pool.ProcessModel.IdleTimeout = settings.IdleTimeout;
            pool.SetAttributeValue("startMode", settings.StartMode.Equals("AlwaysRunning", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            sm.CommitChanges();
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IIS SetPoolSettings failed");
            return Result.Fail(ex.Message);
        }
    }

    public Result RenameSite(string siteName, string newName, string physicalPath)
    {
        try
        {
            // Stopped first, its worker gone: the pool can't be renamed while a worker runs as its identity, and the
            // caller may move the folder the worker holds files in.
            bool renamePool;
            using (var sm = new ServerManager())
            {
                if (sm.Sites[siteName] is not { } site) return Result.Fail($"Site '{siteName}' not found");
                if (sm.Sites[newName] is not null) return Result.Fail($"IIS already has a site named '{newName}'.");
                var pool = PoolOf(sm, site);
                renamePool = pool is not null && pool.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase) &&
                             !PoolUsedByOthers(sm, siteName) && sm.ApplicationPools[newName] is null;
                if (site.State != ObjectState.Stopped) try { site.Stop(); } catch { /* already stopping */ }
                if (renamePool && pool!.State != ObjectState.Stopped) try { pool.Stop(); } catch { /* already stopping */ }
                sm.CommitChanges();
            }
            if (renamePool) WaitForPoolToStop(siteName, TimeSpan.FromSeconds(30));

            using (var sm = new ServerManager())
            {
                var site = sm.Sites[siteName]!;
                site.Name = newName;
                if (renamePool)
                {
                    sm.ApplicationPools[siteName]!.Name = newName;
                    foreach (var app in site.Applications) app.ApplicationPoolName = newName;
                }
                if (site.Applications["/"]?.VirtualDirectories["/"] is { } root) root.PhysicalPath = physicalPath;
                sm.CommitChanges();
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IIS RenameSite failed");
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

    public IisSiteDetails? GetSiteDetails(string siteName)
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
            bool? preload = root is null ? null : Attribute<bool?>(root, "preloadEnabled");

            var bindings = site.Bindings.Select(b =>
            {
                var (port, host) = BindingParts(b.BindingInformation);
                var address = b.BindingInformation.Split(':')[0];
                var hash = b.CertificateHash is { Length: > 0 } bytes ? Convert.ToHexString(bytes) : null;
                var store = hash is null ? null : string.IsNullOrEmpty(b.CertificateStoreName) ? "My" : b.CertificateStoreName;
                var sslFlags = Attribute<object>(b, "sslFlags");
                var sni = sslFlags is not null && (Convert.ToInt32(sslFlags) & 1) == 1;
                return new IisBindingDetails(b.Protocol, address, port, host == "*" ? "" : host, b.BindingInformation, sni, hash, store,
                    hash is null ? null : Certificate(hash, store!));
            }).ToList();

            var poolName = root?.ApplicationPoolName ?? "";
            var pool = sm.ApplicationPools[poolName];
            return new IisSiteDetails(site.Id, site.Name, state, physicalPath, $"MACHINE/WEBROOT/APPHOST/{site.Name}", site.ServerAutoStart,
                preload, bindings, pool is null ? null : PoolDetails(pool));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the details of IIS site {Site}", siteName);
            return null;
        }
    }

    private static IisPoolDetails PoolDetails(ApplicationPool pool)
    {
        string? state = null;
        try { state = pool.State.ToString(); } catch { /* unknown */ }
        var model = pool.ProcessModel;
        var account = model.IdentityType switch
        {
            ProcessModelIdentityType.ApplicationPoolIdentity => $@"IIS APPPOOL\{pool.Name}",
            ProcessModelIdentityType.NetworkService => @"NT AUTHORITY\NETWORK SERVICE",
            ProcessModelIdentityType.LocalService => @"NT AUTHORITY\LOCAL SERVICE",
            ProcessModelIdentityType.LocalSystem => @"NT AUTHORITY\SYSTEM",
            _ => model.UserName
        };
        var recycling = pool.Recycling.PeriodicRestart;
        var workers = new List<int>();
        try { workers.AddRange(pool.WorkerProcesses.Select(w => w.ProcessId)); } catch { /* not running, or not readable */ }
        return new IisPoolDetails(
            pool.Name,
            state,
            string.IsNullOrEmpty(pool.ManagedRuntimeVersion) ? "No Managed Code" : pool.ManagedRuntimeVersion,
            pool.ManagedPipelineMode.ToString(),
            model.IdentityType.ToString(),
            account,
            Attribute<object>(pool, "startMode")?.ToString() switch { "1" => "AlwaysRunning", "0" => "OnDemand", { } other => other, null => "OnDemand" },
            model.IdleTimeout,
            Attribute<object>(model, "idleTimeoutAction")?.ToString() switch { "1" => "Suspend", "0" => "Terminate", { } other => other, null => null },
            recycling.Time,
            recycling.Schedule.Select(s => s.Time).ToList(),
            recycling.PrivateMemory,
            recycling.Memory,
            pool.QueueLength,
            pool.Enable32BitAppOnWin64,
            pool.Failure.RapidFailProtection,
            pool.Failure.RapidFailProtectionMaxCrashes,
            pool.Failure.RapidFailProtectionInterval,
            model.MaxProcesses,
            model.LoadUserProfile,
            workers);
    }

    /// <summary>An attribute IIS may not have (an older IIS, a module not installed): null then.</summary>
    private static T? Attribute<T>(ConfigurationElement element, string name)
    {
        try { return element.GetAttributeValue(name) is T value ? value : default; }
        catch { return default; }
    }

    /// <summary>The certificate with <paramref name="thumbprint"/> in the machine's <paramref name="store"/>; null when it isn't there.</summary>
    private static IisCertificate? Certificate(string thumbprint, string store)
    {
        try
        {
            using var x509 = new System.Security.Cryptography.X509Certificates.X509Store(store,
                System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            x509.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly | System.Security.Cryptography.X509Certificates.OpenFlags.OpenExistingOnly);
            var found = x509.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (found.Count == 0) return null;
            var cert = found[0];
            return new IisCertificate(cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
                string.IsNullOrEmpty(cert.FriendlyName) ? null : cert.FriendlyName,
                cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, true), cert.NotBefore, cert.NotAfter);
        }
        catch
        {
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
        // A pool still in IIS (another site uses it - see RemoveSite) still runs as that profile.
        if (PoolExists(poolName)) return Result.Ok();

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
