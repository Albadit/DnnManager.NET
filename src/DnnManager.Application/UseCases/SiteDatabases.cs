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
        var sites = _iis.GetSiteRuntimes();
        if (sites is null) return null;
        foreach (var (name, site) in sites)
        {
            if (name.Equals(siteName, StringComparison.OrdinalIgnoreCase) || site.PhysicalPath.Length == 0) continue;
            var path = Path.Combine(site.PhysicalPath, "web.config");
            if (!File.Exists(path)) continue;
            var read = _webConfig.ReadDatabaseConnection(path);
            if (read is not { Success: true, Value: { Kind: not DatabaseKind.LocalDbFile } other }) continue;
            if (other.Database.Equals(database, StringComparison.OrdinalIgnoreCase) && address.SameServerAs(other.Server))
                return name;
        }
        return null;
    }
}
