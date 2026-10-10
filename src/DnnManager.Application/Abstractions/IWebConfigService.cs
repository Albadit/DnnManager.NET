using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IWebConfigService
{
    Result<SiteSqlConnection> ReadSiteSqlServer(string webConfigPath);

    /// <summary>
    /// Rewrites both connectionStrings/add[@name='SiteSqlServer'] and
    /// appSettings/add[@key='SiteSqlServer'] to point at a new database.
    /// </summary>
    Result WriteSiteSqlServer(string webConfigPath, SiteSqlConnection newConnection);

    /// <summary>Points the site at <paramref name="connection"/>: SiteSqlServer as DNN reads it, for any kind of database.</summary>
    Result WriteDatabaseConnection(string webConfigPath, DatabaseConnection connection);

    /// <summary>
    /// The site's SiteSqlServer as a connection: integrated security or a SQL login, or a LocalDB file. A server isn't
    /// recognised as the local container here - <c>LocalSqlContainer.ConnectionOf</c> does that.
    /// </summary>
    Result<DatabaseConnection> ReadDatabaseConnection(string webConfigPath);

    /// <summary>
    /// SiteSqlServer's <c>localhost,&lt;port&gt;</c> as <c>127.0.0.1,&lt;port&gt;</c> - the rest of the connection string as
    /// it is. True when it was changed; false when it doesn't name localhost with a port.
    /// </summary>
    Result<bool> UseLoopbackAddress(string webConfigPath);

    /// <summary>
    /// Removes the IIS URL Rewrite section (system.webServer/rewrite). Those rules are
    /// production-only (HTTPS redirects, request blocking) and require the URL Rewrite module,
    /// which is usually absent locally - otherwise IIS returns HTTP 500.19. Safe no-op if absent.
    /// </summary>
    Result RemoveRewriteRules(string webConfigPath);

    /// <summary>
    /// Switches off (<c>enabled="false"</c>, with a comment) every enabled URL Rewrite rule that
    /// redirects to an <c>https://</c> address. A local site has no HTTPS binding, so such a rule sends
    /// every request to an address that doesn't answer. Also reports rules switched off by an earlier
    /// run (recognised by that comment), since those still have to go back on for production.
    /// Both lists are empty when there are none (or no web.config).
    /// </summary>
    Result<HttpsRedirectRules> DisableHttpsRedirectRules(string webConfigPath);

    /// <summary>A few settings worth knowing about a site's web.config, for the project details view.</summary>
    Result<WebConfigFacts> ReadFacts(string webConfigPath);

    /// <summary>
    /// Switches the HTTPS redirect rules <see cref="DisableHttpsRedirectRules"/> switched off back on, its comment removed -
    /// for a copy that goes to a server with HTTPS. Returns their names.
    /// </summary>
    Result<IReadOnlyList<string>> EnableHttpsRedirectRules(string webConfigPath);

    /// <summary>Sets SiteSqlServer (connectionStrings and appSettings) to <paramref name="connectionString"/> as it is.</summary>
    Result WriteConnectionString(string webConfigPath, string connectionString);

    /// <summary>Sets &lt;compilation debug&gt; - off for a live server.</summary>
    Result SetDebug(string webConfigPath, bool debug);
}
