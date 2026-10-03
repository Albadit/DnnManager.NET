using System.Xml.Linq;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Infrastructure.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.WebConfigs;

public sealed class WebConfigService(ILogger<WebConfigService> log) : IWebConfigService
{
    private readonly ILogger<WebConfigService> _log = log;

    public Result<SiteSqlConnection> ReadSiteSqlServer(string webConfigPath)
    {
        try
        {
            if (!File.Exists(webConfigPath))
                return Result<SiteSqlConnection>.Fail($"web.config not found: {webConfigPath}");

            var doc = XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace);
            var add = FindConnectionStringElement(doc, webConfigPath, out var sourcePath);
            if (add is null)
                return Result<SiteSqlConnection>.Fail(
                    $"connectionStrings/add[@name='SiteSqlServer'] not found in {sourcePath}.");

            var raw = (string?)add.Attribute("connectionString");
            if (string.IsNullOrWhiteSpace(raw))
                return Result<SiteSqlConnection>.Fail($"SiteSqlServer connectionString attribute is empty in {sourcePath}.");

            // SqlConnectionStringBuilder accepts both 'Server'/'Data Source' and 'Database'/'Initial Catalog' aliases.
            SqlConnectionStringBuilder b;
            try { b = new SqlConnectionStringBuilder(raw); }
            catch (Exception ex)
            {
                // Not the value itself - it can hold a password.
                return Result<SiteSqlConnection>.Fail($"Failed to parse the SiteSqlServer connection string in {sourcePath}: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(b.DataSource) || string.IsNullOrWhiteSpace(b.InitialCatalog))
            {
                // Common DNN template case: AttachDBFilename / LocalDB / User Instance - there's no
                // real SQL Server database here so cloning isn't possible until the site is wired
                // up to a proper DB.
                if (!string.IsNullOrWhiteSpace(b.AttachDBFilename))
                {
                    return Result<SiteSqlConnection>.Fail(
                        $"This site uses LocalDB (AttachDBFilename='{b.AttachDBFilename}') and has no Initial Catalog. " +
                        $"There's no SQL Server database to back up - connect the project to a real DNN database first, then retry the clone. " +
                        $"(from {sourcePath})");
                }
                return Result<SiteSqlConnection>.Fail(
                    $"SiteSqlServer is missing Data Source or Initial Catalog. " +
                    $"DataSource='{b.DataSource}', InitialCatalog='{b.InitialCatalog}' (from {sourcePath}).");
            }

            return Result<SiteSqlConnection>.Ok(new SiteSqlConnection(
                b.DataSource,
                b.InitialCatalog,
                b.UserID ?? "",
                b.Password ?? ""));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read web.config");
            return Result<SiteSqlConnection>.Fail(ex.Message);
        }
    }

    public Result WriteSiteSqlServer(string webConfigPath, SiteSqlConnection newConnection) =>
        WriteConnectionString(webConfigPath, BuildConnectionString(newConnection));

    public Result WriteDatabaseConnection(string webConfigPath, DatabaseConnection connection) =>
        WriteConnectionString(webConfigPath, ConnectionStrings.ForSite(connection));

    public Result<DatabaseConnection> ReadDatabaseConnection(string webConfigPath)
    {
        try
        {
            if (!File.Exists(webConfigPath))
                return Result<DatabaseConnection>.Fail($"web.config not found: {webConfigPath}");
            var doc = XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace);
            var add = FindConnectionStringElement(doc, webConfigPath, out var sourcePath);
            var raw = (string?)add?.Attribute("connectionString");
            if (string.IsNullOrWhiteSpace(raw))
                return Result<DatabaseConnection>.Fail($"SiteSqlServer is missing or empty in {sourcePath}.");
            return ConnectionStrings.Parse(raw) is { } connection
                ? Result<DatabaseConnection>.Ok(connection)
                : Result<DatabaseConnection>.Fail($"The SiteSqlServer connection string in {sourcePath} can't be read.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read web.config");
            return Result<DatabaseConnection>.Fail(ex.Message);
        }
    }

    private Result WriteConnectionString(string webConfigPath, string connStr)
    {
        try
        {
            if (!File.Exists(webConfigPath))
                return Result.Fail($"web.config not found: {webConfigPath}");

            var doc = XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace);

            var (connAdd, connSourceFile, connDoc) = FindAndLoadSection(
                doc, webConfigPath, "connectionStrings", "add", "name", "SiteSqlServer");
            if (connAdd is null)
                return Result.Fail($"connectionStrings/add[@name='SiteSqlServer'] not found in {connSourceFile}.");
            connAdd.SetAttributeValue("connectionString", connStr);
            Save(connDoc!, connSourceFile);

            // DNN also reads from appSettings/add[@key='SiteSqlServer'] for the upgrade wizard.
            var (appAdd, appSourceFile, appDoc) = FindAndLoadSection(
                doc, webConfigPath, "appSettings", "add", "key", "SiteSqlServer");
            if (appAdd is not null)
            {
                appAdd.SetAttributeValue("value", connStr);
                Save(appDoc!, appSourceFile);
            }

            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to write web.config");
            return Result.Fail(ex.Message);
        }
    }

    public Result RemoveRewriteRules(string webConfigPath)
    {
        try
        {
            if (!File.Exists(webConfigPath))
                return Result.Fail($"web.config not found: {webConfigPath}");

            var doc = XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace);
            var rewrites = doc.Descendants("system.webServer")
                              .Elements("rewrite")
                              .ToList();
            if (rewrites.Count == 0) return Result.Ok();

            foreach (var r in rewrites) r.Remove();
            Save(doc, webConfigPath);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to remove rewrite rules from web.config");
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>Marks rules this app switched off, so they're easy to find before a production deploy.</summary>
    public const string DisabledRuleComment =
        " Disabled by DNN Manager for local development (no HTTPS locally). " +
        "Re-enable (remove enabled=\"false\") before deploying to production. ";

    public Result<HttpsRedirectRules> DisableHttpsRedirectRules(string webConfigPath)
    {
        try
        {
            if (!File.Exists(webConfigPath)) return Result<HttpsRedirectRules>.Ok(HttpsRedirectRules.None);

            var doc = XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace);
            var redirects = doc.Descendants("system.webServer")
                .Elements("rewrite").Elements("rules").Elements("rule")
                .Where(IsHttpsRedirect)
                .ToList();

            var switchedOff = new List<string>();
            var alreadyOff = new List<string>();
            foreach (var rule in redirects)
            {
                if (IsDisabled(rule))
                {
                    // Only ours: a rule that is off in the site's own config isn't this app's to report.
                    if (HasDisabledComment(rule)) alreadyOff.Add(NameOf(rule));
                    continue;
                }
                rule.SetAttributeValue("enabled", "false");
                rule.AddBeforeSelf(new XComment(DisabledRuleComment));
                // Keep the rule on its own line, indented like it was.
                if (rule.PreviousNode?.PreviousNode is XText indent) rule.AddBeforeSelf(new XText(indent.Value));
                switchedOff.Add(NameOf(rule));
            }
            if (switchedOff.Count > 0) Save(doc, webConfigPath);
            return Result<HttpsRedirectRules>.Ok(new HttpsRedirectRules(switchedOff, alreadyOff));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to disable HTTPS redirect rules in web.config");
            return Result<HttpsRedirectRules>.Fail(ex.Message);
        }
    }

    public Result<WebConfigFacts> ReadFacts(string webConfigPath)
    {
        try
        {
            var doc = XDocument.Load(webConfigPath);
            var web = doc.Root?.Element("system.web");
            var debug = (string?)web?.Element("compilation")?.Attribute("debug");
            var framework = (string?)web?.Element("httpRuntime")?.Attribute("targetFramework")
                            ?? (string?)web?.Element("compilation")?.Attribute("targetFramework");
            var disabled = doc.Descendants("system.webServer")
                .Elements("rewrite").Elements("rules").Elements("rule")
                .Where(r => IsDisabled(r) && HasDisabledComment(r))
                .Select(NameOf)
                .ToList();
            return Result<WebConfigFacts>.Ok(new WebConfigFacts(
                bool.TryParse(debug, out var d) ? d : null,
                framework,
                (string?)web?.Element("customErrors")?.Attribute("mode"),
                disabled));
        }
        catch (Exception ex)
        {
            return Result<WebConfigFacts>.Fail(ex.Message);
        }
    }

    // A rule that redirects to an https:// address (typically "HTTP to HTTPS redirect").
    private static bool IsHttpsRedirect(XElement rule)
    {
        var action = rule.Element("action");
        return action is not null
            && string.Equals((string?)action.Attribute("type"), "Redirect", StringComparison.OrdinalIgnoreCase)
            && ((string?)action.Attribute("url"))?.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsDisabled(XElement rule) =>
        string.Equals((string?)rule.Attribute("enabled"), "false", StringComparison.OrdinalIgnoreCase);

    // The nearest non-whitespace node before the rule is our "Disabled by DNN Manager" comment.
    private static bool HasDisabledComment(XElement rule)
    {
        var node = rule.PreviousNode;
        while (node is XText text && string.IsNullOrWhiteSpace(text.Value)) node = node.PreviousNode;
        return node is XComment comment && comment.Value.Contains("Disabled by DNN Manager", StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the file whole or not at all: to a file next to it first, which then takes its place - a crash or a full
    /// disk halfway leaves the old web.config, not half of one. Replacing keeps the file's permissions, which IIS reads it with.
    /// </summary>
    private static void Save(XDocument doc, string path)
    {
        var temp = path + ".dnnmanager.tmp";
        try
        {
            doc.Save(temp);
            if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string NameOf(XElement rule) => (string?)rule.Attribute("name") ?? "(unnamed)";

    // Looks up <connectionStrings>; if it uses configSource="…", loads the external file.
    private XElement? FindConnectionStringElement(XDocument doc, string webConfigPath, out string sourcePath)
    {
        var (element, file, _) = FindAndLoadSection(
            doc, webConfigPath, "connectionStrings", "add", "name", "SiteSqlServer");
        sourcePath = file;
        return element;
    }

    private (XElement? element, string sourceFile, XDocument? sourceDoc) FindAndLoadSection(
        XDocument doc, string webConfigPath,
        string sectionName, string childName, string keyAttr, string keyValue)
    {
        var section = doc.Descendants(sectionName).FirstOrDefault();
        if (section is null) return (null, webConfigPath, null);

        var configSource = (string?)section.Attribute("configSource");
        if (!string.IsNullOrWhiteSpace(configSource))
        {
            var dir = Path.GetDirectoryName(webConfigPath) ?? string.Empty;
            var external = Path.GetFullPath(Path.Combine(dir, configSource));
            // ASP.NET only takes a file in the site's own folder (or below) - nothing else is read or written.
            var inSite = external.StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
            if (!inSite || !File.Exists(external)) return (null, external, null);

            var extDoc = XDocument.Load(external, LoadOptions.PreserveWhitespace);
            var extRoot = extDoc.Root;
            if (extRoot is null) return (null, external, extDoc);

            var found = extRoot.Elements(childName)
                .FirstOrDefault(e => string.Equals((string?)e.Attribute(keyAttr), keyValue, StringComparison.OrdinalIgnoreCase));
            return (found, external, extDoc);
        }

        var inline = section.Elements(childName)
            .FirstOrDefault(e => string.Equals((string?)e.Attribute(keyAttr), keyValue, StringComparison.OrdinalIgnoreCase));
        return (inline, webConfigPath, doc);
    }

    // With the builder, a password with ; = or quotes in it is quoted instead of breaking the connection string.
    private static string BuildConnectionString(SiteSqlConnection c) =>
        new SqlConnectionStringBuilder { DataSource = c.Server, InitialCatalog = c.Database, UserID = c.User, Password = c.Password }.ConnectionString;
}
