using DnnManager.Application.Abstractions;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Which IIS sites use a database, as their web.config files have it - asked before a database is dropped, replaced or
/// taken over, so that one project never destroys, or quietly shares, another one's data.
/// </summary>
public sealed class SiteDatabases(IIisManager iis, IWebConfigService webConfig)
{
    private readonly IIisManager _iis = iis;
    private readonly IWebConfigService _webConfig = webConfig;

    /// <summary>
    /// The IIS site other than <paramref name="siteName"/> whose web.config names <paramref name="database"/> on
    /// <paramref name="server"/> (however it writes that server); null when none does, or IIS can't be read.
    /// </summary>
    public string? OtherSiteUsing(string siteName, string server, string database)
    {
        if (database.Length == 0) return null;
        var address = SqlServerAddress.Parse(server);
        return OtherSites(siteName).FirstOrDefault(s =>
            s.Connection.Database.Equals(database, StringComparison.OrdinalIgnoreCase) && address.SameServerAs(s.Connection.Server)).Site;
    }

    /// <summary>
    /// The IIS site other than <paramref name="siteName"/> whose web.config signs in to <paramref name="server"/> as
    /// <paramref name="login"/> - a project's own login, kept by a site renamed since; null when none does, or IIS can't
    /// be read.
    /// </summary>
    public string? OtherSiteSigningInAs(string siteName, string server, string login)
    {
        if (login.Length == 0) return null;
        var address = SqlServerAddress.Parse(server);
        return OtherSites(siteName).FirstOrDefault(s =>
            s.Connection.User.Equals(login, StringComparison.OrdinalIgnoreCase) && address.SameServerAs(s.Connection.Server)).Site;
    }

    /// <summary>
    /// The IIS sites whose web.config reaches <paramref name="server"/> as <c>localhost,&lt;port&gt;</c> - with the path of that
    /// web.config.
    /// </summary>
    public IReadOnlyList<(string Site, string WebConfig)> ReachingAsLocalhost(string server)
    {
        var address = SqlServerAddress.Parse(server);
        return OtherSites("")
            .Where(s => SqlServerAddress.Parse(s.Connection.Server) is { Port: not null } named &&
                        named.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && address.SameServerAs(named))
            .Select(s => (s.Site, s.WebConfig))
            .ToList();
    }

    /// <summary>The database connection of every IIS site but <paramref name="siteName"/>, as its web.config has it.</summary>
    private IEnumerable<(string Site, string WebConfig, DatabaseConnection Connection)> OtherSites(string siteName)
    {
        var sites = _iis.GetSiteRuntimes();
        if (sites is null) yield break;
        foreach (var (name, site) in sites)
        {
            if (name.Equals(siteName, StringComparison.OrdinalIgnoreCase) || site.PhysicalPath.Length == 0) continue;
            var path = Path.Combine(site.PhysicalPath, "web.config");
            if (!File.Exists(path)) continue;
            var read = _webConfig.ReadDatabaseConnection(path);
            if (read is { Success: true, Value: { Kind: not DatabaseKind.LocalDbFile } other }) yield return (name, path, other);
        }
    }
}
