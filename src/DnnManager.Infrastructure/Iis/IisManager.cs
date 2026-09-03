using System.Security.AccessControl;
using System.Security.Principal;
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

    private readonly ProcessRunner _proc;
    private readonly ILogger<IisManager> _log;

    public IisManager(ProcessRunner proc, ILogger<IisManager> log)
    {
        _proc = proc;
        _log = log;
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
            using var sm = new ServerManager();
            var site = sm.Sites[siteName];
            if (site is null) return Result.Fail($"Site '{siteName}' not found");
            site.Start();
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(ex.Message); }
    }

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
