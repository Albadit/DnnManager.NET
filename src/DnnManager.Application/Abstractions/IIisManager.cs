using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IIisManager
{
    Result CreateSite(string siteName, string physicalPath, string hostname, int port);
    Result RemoveSite(string siteName);

    /// <summary>Starts the site, and its app pool first when that is stopped.</summary>
    Result StartSite(string siteName);

    /// <summary>Stops the site, and its app pool when no other site uses it.</summary>
    Result StopSite(string siteName);

    /// <summary>
    /// Stops the site as <see cref="StopSite"/> does, and returns only once its worker process has ended - ended by force
    /// when it doesn't within <paramref name="timeout"/>. What the site held open is free then: DNN keeps its
    /// installBlocker.lock open until its process ends. Fails when the worker process still runs.
    /// </summary>
    Result StopSiteAndWait(string siteName, TimeSpan timeout) => StopSite(siteName);

    /// <summary>Recycles the site's app pool (a new worker process) and starts the site if it's stopped.</summary>
    Result RestartSite(string siteName);

    /// <summary>
    /// Recycles the site's app pool when it runs - what it holds in memory (ASP.NET's and DNN's caches) is gone; a
    /// stopped pool stays stopped. Ok when there is nothing to recycle.
    /// </summary>
    Result RecycleAppPool(string siteName);

    /// <summary>The folder IIS writes the site's request logs to (<c>…\W3SVC&lt;id&gt;</c>); null when there is no such site.</summary>
    string? GetLogDirectory(string siteName);

    /// <summary>
    /// The Windows account the site runs as - what it signs in to SQL Server with under Windows authentication, e.g.
    /// <c>IIS APPPOOL\mysite</c>.
    /// </summary>
    string AppPoolIdentity(string siteName);

    /// <summary>
    /// Loads the user profile of the site's app pool identity and gives it that profile's environment - what a LocalDB
    /// database needs: LocalDB keeps every Windows account's instance in its profile.
    /// </summary>
    Result EnableUserProfile(string siteName);

    /// <summary>True when IIS is installed and its configuration is reachable on this machine.
    /// Lets setup skip website creation gracefully instead of failing when IIS is absent.</summary>
    bool IsAvailable();

    /// <summary>The state of the IIS web service (W3SVC).</summary>
    IisServerState GetServerState();

    /// <summary>Starts, stops or restarts all IIS services (<c>iisreset /start</c>, <c>/stop</c> or <c>/restart</c>).</summary>
    Task<Result> ControlServerAsync(IisServerAction action, CancellationToken ct);

    /// <summary>
    /// One-shot snapshot of every IIS site: name -> state. Loading applicationHost.config is what a
    /// <c>ServerManager</c> actually costs, so callers that need the status of many sites take one
    /// snapshot rather than querying site by site. Empty when IIS is unavailable.
    /// </summary>
    IReadOnlyDictionary<string, string> GetSiteStates();

    /// <summary>
    /// Every site with what the Projects table shows live: its ID, state, bindings, folder, app pool and the pool's
    /// worker processes. Null when IIS's configuration couldn't be read - which is not the same as
    /// "there are no sites", so a caller can keep what it knew.
    /// </summary>
    IReadOnlyDictionary<string, IisSiteRuntime>? GetSiteRuntimes();

    /// <summary>
    /// Every site's HTTP traffic since IIS started, from IIS's own counters - all of them in one read. Empty when
    /// the counters aren't there or can't be read.
    /// </summary>
    IReadOnlyDictionary<string, SiteTraffic> GetSiteTraffic();

    /// <summary>
    /// How many requests each site has served since IIS started, from IIS's own counters - all of them in one read.
    /// Empty when the counters aren't there or can't be read.
    /// </summary>
    IReadOnlyDictionary<string, long> GetRequestsServed();

    /// <summary>The site in detail - its bindings with their certificates, its app pool's settings; null when IIS has no such site.</summary>
    IisSiteDetails? GetSiteDetails(string siteName) => null;

    /// <summary>
    /// Replaces the site's http bindings with <paramref name="bindings"/> (an empty host answers any); its https and other
    /// bindings stay as they are.
    /// </summary>
    Result ReplaceHttpBindings(string siteName, IReadOnlyList<(string Host, int Port)> bindings) => Result.Fail("Not supported here.");

    /// <summary>Changes the settings of the site's app pool - refused for a pool other sites use too.</summary>
    Result SetPoolSettings(string siteName, IisPoolSettings settings) => Result.Fail("Not supported here.");

    /// <summary>
    /// Renames the site to <paramref name="newName"/> - and its app pool, when it is named like the site and no other site
    /// uses it - and points its root at <paramref name="physicalPath"/>. The site and its pool are stopped first, and the
    /// worker process waited for; the caller starts it again.
    /// </summary>
    Result RenameSite(string siteName, string newName, string physicalPath) => Result.Fail("Not supported here.");

    /// <summary>
    /// Lets <paramref name="identities"/> into the site's folder <paramref name="path"/> and everything in it: its app
    /// pool may change it; IIS's shared groups (IIS_IUSRS, IUSR) may only read it.
    /// </summary>
    Result GrantPermissions(string path, IEnumerable<string> identities);

    /// <summary>
    /// Deletes the Windows user profile auto-created for the app pool's virtual identity
    /// (<c>IIS APPPOOL\&lt;poolName&gt;</c>) - i.e. the leftover <c>C:\Users\&lt;poolName&gt;</c> folder and
    /// its ProfileList registry entry. Matched strictly by the deterministic app-pool SID, so it
    /// only ever removes this pool's profile. Best-effort and idempotent: a no-op (still Ok) when no
    /// such profile exists. Call only after the pool's worker has exited (see <see cref="RemoveSite"/>).
    /// </summary>
    Task<Result> RemoveAppPoolProfileAsync(string poolName, CancellationToken ct);
}
