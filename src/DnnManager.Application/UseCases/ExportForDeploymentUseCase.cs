using System.IO.Compression;
using System.Xml.Linq;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Export for deployment: a package for the live server in one folder - the site's files as &lt;project&gt;.zip without what
/// only matters on this PC (source control, editor folders, logs, caches, the search index), its web.config made ready for
/// the server (the live connection string or a placeholder, debug off, the HTTPS rules DNN Manager switched off back on);
/// the database as &lt;project&gt;.bacpac with the live domains as portal 0's aliases and DNN's SSL setting to match; and
/// DEPLOY.txt saying what to do with them. The local site and its database are not changed: the database is changed in
/// a temporary copy (exported, imported under another name on the same server, changed, exported again, dropped).
/// </summary>
public sealed class ExportForDeploymentUseCase(
    IProjectRepository projects,
    IProjectFileCopier copier,
    IBacpacService bacpac,
    LocalSqlContainer sql,
    IDatabaseProvisioner databases,
    IWebConfigService webConfig,
    ILogger<ExportForDeploymentUseCase> log,
    OperationUndo undo,
    IPrivateTemp temp)
{
    /// <summary>What the live site doesn't need: made on this PC, or made again by DNN.</summary>
    private static readonly string[] LocalOnly =
    [
        ".git", ".vs", ".vscode", ".idea", "node_modules",
        @"Portals\_default\Logs", @"Portals\_default\Cache", @"App_Data\Search", @"App_Data\ClientDependency"
    ];

    /// <summary>Written into web.config when no live connection string is given - to fill in on the server.</summary>
    private const string Placeholder = "Data Source=YOUR-SQL-SERVER;Initial Catalog=YOUR-DATABASE;User ID=YOUR-USER;Password=YOUR-PASSWORD";

    private static class Stage
    {
        public const string Files = "Site files";
        public const string WebConfig = "web.config";
        public const string Database = "Database";
        public const string Instructions = "Instructions";
    }

    private readonly IProjectRepository _projects = projects;
    private readonly IProjectFileCopier _copier = copier;
    private readonly IBacpacService _bacpac = bacpac;
    private readonly LocalSqlContainer _sql = sql;
    private readonly IDatabaseProvisioner _databases = databases;
    private readonly IWebConfigService _webConfig = webConfig;
    private readonly ILogger<ExportForDeploymentUseCase> _log = log;
    private readonly OperationUndo _undo = undo;
    // Not %TEMP%: the copy of the site and its database, read back to make the package.
    private readonly IPrivateTemp _temp = temp;

    public async Task<Result> ExecuteAsync(ExportForDeploymentRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var project = _projects.Build(req.ProjectName, req.ProjectDirectory);
        if (!Directory.Exists(project.ProjectDirectory)) return Result.Fail($"Project folder not found: {project.ProjectDirectory}");
        var folder = Path.GetFullPath(req.OutputFolder);
        if (SafePath.IsSameOrInside(folder, project.ProjectDirectory))
            return Result.Fail("Choose a folder outside the project's folder - the package would end up inside the site.");

        var zipPath = Path.Combine(folder, $"{project.Name}.zip");
        var bacpacPath = Path.Combine(folder, $"{project.Name}.bacpac");
        var instructions = Path.Combine(folder, "DEPLOY.txt");
        _undo.DeleteFolderOnUndo(folder);
        foreach (var file in new[] { zipPath, bacpacPath, instructions }) _undo.RestoreFileOnUndo(file);
        Directory.CreateDirectory(folder);

        reporter.Plan(Stage.Files, Stage.WebConfig, Stage.Database, Stage.Instructions);
        reporter.Context(req.Domains.Count > 0 ? string.Join(", ", req.Domains) : project.Name);
        try
        {
            reporter.Step("Zipping the site's files", Stage.Files);
            IReadOnlyList<string> filtered;
            try { filtered = BackupFilter.Read(project.ProjectDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { filtered = []; }
            reporter.Info($"Leaving out: {string.Join(", ", LocalOnly.Concat(filtered))}");
            var zip = await _copier.CreateZipAsync(project.ProjectDirectory, zipPath, LocalOnly.Concat(filtered).ToList(), reporter, ct);
            if (!zip.Success) return zip;

            reporter.Step("Making web.config ready for the server", Stage.WebConfig);
            var config = PrepareWebConfig(zipPath, req, reporter);
            if (!config.Success) return config;

            reporter.Step("Exporting the database", Stage.Database);
            var database = req.IncludeDatabase ? await ExportDatabaseAsync(project, bacpacPath, req, reporter, ct) : Result.Ok();
            if (!req.IncludeDatabase) reporter.Info("Left out, as chosen.");
            if (!database.Success) return Result.Fail($"The site's files are in {zipPath}, but the database couldn't be exported: {database.Error}");

            reporter.Step("Writing DEPLOY.txt", Stage.Instructions);
            await File.WriteAllTextAsync(instructions, Instructions(project.Name, req), ct);
            // In the log - not the summary under the stages, which is too narrow for a path; the toast opens the folder.
            reporter.Success($"Package: {folder}");
            // What it holds is the live site's: said, so it doesn't stay around forgotten.
            if (req.IncludeDatabase || req.ConnectionString is { Length: > 0 })
                reporter.Warn("The package holds " + (req.IncludeDatabase ? "the whole database (its users too)" : "") +
                              (req.IncludeDatabase && req.ConnectionString is { Length: > 0 } ? " and " : "") +
                              (req.ConnectionString is { Length: > 0 } ? "the live connection string, password included" : "") +
                              " - delete it once it is deployed (Troubleshoot → Clean up data → Deployment packages).");
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Exporting {Project} for deployment failed", req.ProjectName);
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>
    /// The zip's web.config - and the files its connectionStrings and appSettings come from (configSource) - changed for
    /// the server in a temporary folder and put back into the zip.
    /// </summary>
    internal Result PrepareWebConfig(string zipPath, ExportForDeploymentRequest req, IProgressReporter reporter)
    {
        var temp = Path.Combine(_temp.Folder, $"dnnmanager-deploy-{Guid.NewGuid():N}");
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
            // Web.config or web.config: Windows doesn't mind its capitals, and neither does the zip's reader here.
            if (Entry(zip, "web.config") is not { } entry) return Result.Fail("The site has no web.config.");
            Directory.CreateDirectory(temp);
            var webConfigPath = Path.Combine(temp, "web.config");
            entry.ExtractToFile(webConfigPath);
            // The entries as found, whatever their capitals: these are replaced - by name, a Web.config would stay next to
            // the new one, with the local connection string in it.
            var entries = new List<(ZipArchiveEntry Entry, string File)> { (entry, webConfigPath) };
            foreach (var source in ConfigSources(webConfigPath))
            {
                // connectionStrings and appSettings may both come from one file: it is put back once.
                if (Entry(zip, source) is not { } external || entries.Exists(e => e.Entry == external)) continue;
                var path = Path.Combine(temp, source.Replace('/', '\\'));
                // A rooted configSource (C:\...) would make Path.Combine leave the temporary folder.
                if (!SafePath.IsInside(path, temp)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                external.ExtractToFile(path);
                entries.Add((external, path));
            }

            var live = req.ConnectionString is { Length: > 0 } given ? given.Trim() : Placeholder;
            var written = _webConfig.WriteConnectionString(webConfigPath, live);
            if (!written.Success) return Result.Fail($"Could not set the connection string: {written.Error}");
            reporter.Success(req.ConnectionString is { Length: > 0 }
                ? "SiteSqlServer is the live connection string."
                : "SiteSqlServer is a placeholder - fill it in on the server (DEPLOY.txt).");
            if (_webConfig.SetDebug(webConfigPath, false).Success) reporter.Success("Debug compilation is off.");
            if (req.Https && _webConfig.EnableHttpsRedirectRules(webConfigPath) is { Success: true, Value.Count: > 0 } rules)
                reporter.Success($"Switched back on: {string.Join(", ", rules.Value)}");

            foreach (var (found, file) in entries)
            {
                var name = found.FullName;
                found.Delete();
                zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
            }
            return Result.Ok();
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The zip's entry <paramref name="name"/>, whatever its capitals - as Windows finds a file; null when there is none.</summary>
    private static ZipArchiveEntry? Entry(ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(name.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The files web.config's connectionStrings and appSettings are read from, as zip entry names. The site's web.config
    /// (its app pool can change it): no DTD - an entity that expands to gigabytes - and nothing fetched from elsewhere
    /// (Infrastructure's SiteXml reads it the same way).
    /// </summary>
    private static IEnumerable<string> ConfigSources(string webConfigPath)
    {
        XElement? root;
        using (var reader = System.Xml.XmlReader.Create(webConfigPath,
                   new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null }))
            root = XDocument.Load(reader).Root;
        foreach (var section in new[] { "connectionStrings", "appSettings" })
            if ((string?)root?.Element(section)?.Attribute("configSource") is { Length: > 0 } source && !source.Contains(".."))
                yield return source.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// The site's database as a .bacpac. With live domains or HTTPS, a temporary copy on the same server gets them first:
    /// exported, imported under another name, changed, exported again - then dropped.
    /// </summary>
    private async Task<Result> ExportDatabaseAsync(DnnProject project, string bacpacPath, ExportForDeploymentRequest req,
        IProgressReporter reporter, CancellationToken ct)
    {
        var found = _sql.ExportSourceOf(project);
        if (!found.Success) return Result.Fail(found.Error!);
        var source = found.Value!;
        var ensured = await _bacpac.EnsureAvailableAsync(reporter, ct);
        if (!ensured.Success) return ensured;

        var change = req.Domains.Count > 0 || req.Https;
        if (!change) return await _bacpac.ExportAsync(source, bacpacPath, reporter, ct);

        var copyName = $"{source.Database}_deploy_{DateTime.Now:yyyyMMddHHmmss}";
        var copy = new DatabaseConnection(DatabaseKind.SqlServer, source.Server, copyName,
            source.User.Length == 0 ? SqlAuthentication.Windows : SqlAuthentication.Sql, source.User, source.Password);
        var tempBacpac = Path.Combine(_temp.Folder, $"{copyName}.bacpac");
        _undo.Add($"Drop the temporary database [{copyName}]", () => _databases.DropDatabaseAsync(copy, CancellationToken.None));
        try
        {
            reporter.Info($"Copying [{source.Database}] to [{copyName}] on {source.Server} - the site's own database isn't changed…");
            var exported = await _bacpac.ExportAsync(source, tempBacpac, reporter, ct);
            if (!exported.Success) return exported;
            var imported = await _bacpac.ImportAsync(source.Server, source.User, source.Password, copyName, tempBacpac, reporter, ct);
            if (!imported.Success) return imported;

            if (req.Domains.Count > 0)
            {
                var aliases = await _databases.ReplacePortalAliasesAsync(copy, req.Domains, ct);
                if (!aliases.Success) return aliases;
                reporter.Success($"Portal aliases: {string.Join(", ", req.Domains)} ({req.Domains[0]} primary).");
            }
            var ssl = await _databases.SetSslAsync(copy, req.Https, ct);
            if (ssl.Success) reporter.Success($"DNN's SSL setting is {(req.Https ? "on" : "off")}.");
            else reporter.Warn(ssl.Error!);

            return await _bacpac.ExportAsync(new SiteSqlConnection(source.Server, copyName, source.User, source.Password), bacpacPath, reporter, ct);
        }
        finally
        {
            var dropped = await _databases.DropDatabaseAsync(copy, CancellationToken.None);
            if (!dropped.Success) reporter.Warn($"The temporary database [{copyName}] is still there: {dropped.Error}");
            try { if (File.Exists(tempBacpac)) File.Delete(tempBacpac); } catch { /* best effort */ }
        }
    }

    private static string Instructions(string name, ExportForDeploymentRequest req)
    {
        var domains = req.Domains.Count > 0 ? string.Join(", ", req.Domains) : "your domain";
        var scheme = req.Https ? "https" : "http";
        var lines = new List<string>
        {
            $"Deploying {name} - made by DNN Manager on {DateTime.Now:yyyy-MM-dd HH:mm}",
            "",
            $"1. Unzip {name}.zip into the website's folder on the server (IIS, or Azure App Service's site\\wwwroot).",
            "   Give the app pool's identity modify rights on the folder - DNN writes to App_Data and Portals.",
        };
        if (req.IncludeDatabase)
        {
            lines.Add($"2. Import {name}.bacpac into the live SQL Server: SQL Server Management Studio → Databases → Import Data-tier");
            lines.Add($"   Application, or: sqlpackage /Action:Import /SourceFile:{name}.bacpac /TargetConnectionString:\"...\"");
        }
        else lines.Add("2. Point the site at its live database (the database wasn't part of this package).");
        lines.Add(req.ConnectionString is { Length: > 0 }
            ? "3. web.config already has the live connection string (SiteSqlServer) - check it."
            : "3. In web.config, replace the placeholder connection string SiteSqlServer (connectionStrings and appSettings)\r\n   with the live database's.");
        lines.Add($"4. Bind the IIS site to {domains}{(req.Https ? " - with a certificate on an https binding" : "")}.");
        lines.Add(req.Domains.Count > 0
            ? $"   DNN's portal aliases are already {domains}."
            : "   DNN's portal aliases are as they were locally - add the live domain in DNN (Settings → Site Settings → Site Aliases).");
        lines.Add($"5. Open {scheme}://{(req.Domains.Count > 0 ? req.Domains[0] : "your-domain")}/ and sign in as a host.");
        lines.Add("");
        lines.Add("web.config has debug compilation off" + (req.Https ? " and the HTTPS redirect rules DNN Manager had switched off back on." : "."));
        return string.Join("\r\n", lines) + "\r\n";
    }
}
