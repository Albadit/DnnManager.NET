using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Upgrades;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DnnManager.Infrastructure.Dnn;

/// <summary>
/// The pre-upgrade analyser's eyes: reads a DNN site, its database, IIS and this PC into <see cref="DnnSiteFacts"/> -
/// without changing anything. Assemblies are read from their metadata (what they reference), never loaded.
/// </summary>
public sealed class DnnSiteInspector(IIisManager iis, AppDataPaths paths, ILogger<DnnSiteInspector> log) : IDnnSiteInspector
{
    /// <summary>The appSettings DNN's own web.config has (9.x and 10.x) and those its installer adds - the rest are the site's own.</summary>
    private static readonly HashSet<string> DnnAppSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "SiteSqlServer", "InstallTemplate", "AutoUpgrade", "UseInstallWizard", "InstallMemberRole", "ShowMissingKeys",
        "EnableCachePersistence", "HostHeader", "RemoveAngleBrackets", "PersistentCookieTimeout", "EnableServicesFrameworkTracing",
        "UpdateServiceUrl", "PreserveLoginUrl", "loginUrl", "ValidationSettings:UnobtrusiveValidationMode", "MobileViewSiteCookieName",
        "DisableMobileViewSiteCookieName", "AllowDnnImagePlaceholderText", "AllowDnnUpgradeUpload", "DisableCsp",
        "InstallVersion", "InstallationDate", "UpdateServiceRedirect", "UseDnnImageHandler"
    };

    /// <summary>What a DNN assembly's name starts with - DNN's own, and the libraries it ships.</summary>
    private static readonly string[] ShippedPrefixes =
    [
        "DotNetNuke", "Dnn.", "DNN.", "Telerik", "Microsoft.", "System.", "Newtonsoft", "log4net", "Lucene", "WebFormsMvp", "SharpZipLib",
        "ICSharpCode", "CountryListBox", "ClientDependency", "Moq", "Castle", "Antlr", "WebGrease", "Owin", "BouncyCastle", "MailKit",
        "MimeKit", "Ganss", "AngleSharp", "Lucene.Net", "Dapper", "PetaPoco", "SchwabenCode", "netstandard", "Nustache", "HtmlSanitizer",
        "LiteDB", "Ude", "WebActivatorEx", "Hellang", "SolutionFramework", "Microsoft"
    ];

    private readonly IIisManager _iis = iis;
    private readonly AppDataPaths _paths = paths;
    private readonly ILogger<DnnSiteInspector> _log = log;

    public async Task<DnnSiteFacts> InspectAsync(string siteName, string directory, DatabaseConnection? database, CancellationToken ct)
    {
        var details = _iis.GetSiteDetails(siteName);
        var facts = new DnnSiteFacts
        {
            SiteName = siteName, Directory = directory,
            FilesVersion = DnnInstall.Version(directory),
            InstallBlocked = File.Exists(Path.Combine(directory, "installBlocker.lock")),
            NetFrameworkRelease = NetFrameworkRelease(),
            AppPoolClr = details?.Pool?.Runtime, AppPoolPipeline = details?.Pool?.Pipeline,
            HasHostBinding = details?.Bindings.Any(b => b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port is not null && b.Host.Length > 0) == true,
            SiteBytes = await Task.Run(() => FolderBytes(directory), ct),
            BackupFreeBytes = FreeBytes(_paths.BackupsDirectory),
            DatabaseKind = database?.Kind, DatabaseName = database?.Database
        };
        facts = await Task.Run(() => WithWebConfig(facts), ct);
        if (database is null) return facts;
        if (database.Kind == DatabaseKind.LocalDbFile) return facts with { DatabaseProblem = "A LocalDB file - not read before an upgrade." };

        try
        {
            await using var conn = new SqlConnection(ConnectionStrings.ForApp(database, timeoutSeconds: 15));
            await conn.OpenAsync(ct);
            var server = await ReadAsync(conn, "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), " +
                                               "CAST(DATABASEPROPERTYEX(DB_NAME(), 'Status') AS nvarchar(64))", ct, r => (r.GetString(0), r.GetString(1), r.GetString(2)));
            facts = facts with { SqlServerVersion = server.Item1, SqlServerEdition = server.Item2, DatabaseState = server.Item3 };
            // Its data files' size, for the room the backups need.
            facts = facts with
            {
                DatabaseBytes = await ReadAsync(conn, "SELECT CAST(ISNULL(SUM(CAST(size AS bigint)), 0) * 8192 AS bigint) FROM sys.database_files WHERE type = 0",
                    ct, r => r.GetInt64(0))
            };
            var q = await DnnTables.QualifierAsync(conn, "Version", ct);
            if (q is null) return facts with { DatabaseProblem = "The database has no DNN tables." };
            var version = await ReadAsync(conn, $"SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build) FROM {DnnTables.Name(q, "Version")} ORDER BY Major DESC, Minor DESC, Build DESC",
                ct, r => r.GetString(0));
            var extensions = await ExtensionsAsync(conn, q, ct);
            var registered = await RegisteredAssembliesAsync(conn, q, ct);
            return facts with
            {
                DatabaseVersion = version,
                Counts = await CountAsync(conn, q, ct),
                Extensions = extensions,
                Assemblies = await Task.Run(() => Assemblies(directory, registered), ct),
                TelerikInBin = File.Exists(Path.Combine(directory, "bin", "Telerik.Web.UI.dll"))
            };
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException)
        {
            _log.LogWarning("Reading {Site}'s database for the upgrade failed: {Error}", siteName, ex.Message);
            return facts with
            {
                DatabaseProblem = ex.Message.Split('\n')[0].Trim(),
                Assemblies = Assemblies(directory, null),
                TelerikInBin = File.Exists(Path.Combine(directory, "bin", "Telerik.Web.UI.dll"))
            };
        }
    }

    public async Task<DnnContentCounts?> CountAsync(string directory, DatabaseConnection database, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(ConnectionStrings.ForApp(database, timeoutSeconds: 15));
            await conn.OpenAsync(ct);
            return await DnnTables.QualifierAsync(conn, "Version", ct) is { } q ? await CountAsync(conn, q, ct) : null;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            _log.LogWarning("Counting {Database} failed: {Error}", database.Database, ex.Message);
            return null;
        }
    }

    // ─── The database ─────────────────────────────────────────────────────

    private static async Task<DnnContentCounts> CountAsync(SqlConnection conn, string q, CancellationToken ct)
    {
        async Task<int> Count(string table, string where = "")
        {
            // A table an older DNN doesn't have counts as none.
            await using var cmd = new SqlCommand($"IF OBJECT_ID('{DnnTables.Name(q, table)}') IS NULL SELECT 0 ELSE SELECT COUNT(*) FROM {DnnTables.Name(q, table)} {where}", conn);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        return new DnnContentCounts(
            await Count("Portals"), await Count("Users"), await Count("Roles"), await Count("UserRoles"),
            await Count("Tabs", "WHERE IsDeleted = 0"), await Count("Modules"),
            await Count("TabPermission") + await Count("ModulePermission") + await Count("FolderPermission"),
            await Count("Schedule"), await Count("Packages"));
    }

    private static async Task<IReadOnlyList<DnnExtension>> ExtensionsAsync(SqlConnection conn, string q, CancellationToken ct)
    {
        // Organization and IsSystemPackage came with DNN 5 / 6 - read what this database has.
        var columns = await ColumnsAsync(conn, q, "Packages", ct);
        var owner = columns.Contains("Organization") ? "Organization" : "NULL";
        var system = columns.Contains("IsSystemPackage") ? "IsSystemPackage" : "CAST(0 AS bit)";
        await using var cmd = new SqlCommand($"SELECT Name, FriendlyName, PackageType, Version, {owner}, {system} FROM {DnnTables.Name(q, "Packages")} ORDER BY PackageType, Name", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DnnExtension>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var org = reader.IsDBNull(4) ? null : reader.GetString(4);
            var dnns = !reader.IsDBNull(5) && reader.GetBoolean(5) || IsDnnName(name) ||
                       org is not null && (org.Contains("DNN", StringComparison.OrdinalIgnoreCase) || org.Contains("DotNetNuke", StringComparison.OrdinalIgnoreCase));
            list.Add(new DnnExtension(name, reader.IsDBNull(1) ? name : reader.GetString(1), reader.GetString(2), reader.GetString(3), org, dnns));
        }
        return list;
    }

    private static async Task<HashSet<string>?> RegisteredAssembliesAsync(SqlConnection conn, string q, CancellationToken ct)
    {
        await using var cmd = new SqlCommand($"IF OBJECT_ID('{DnnTables.Name(q, "Assemblies")}') IS NOT NULL SELECT AssemblyName FROM {DnnTables.Name(q, "Assemblies")}", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct)) set.Add(Path.GetFileNameWithoutExtension(reader.GetString(0)));
        return set;
    }

    private static async Task<HashSet<string>> ColumnsAsync(SqlConnection conn, string q, string table, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t)", conn);
        cmd.Parameters.AddWithValue("@t", $"dbo.{q}{table}");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct)) set.Add(reader.GetString(0));
        return set;
    }

    private static async Task<T> ReadAsync<T>(SqlConnection conn, string sql, CancellationToken ct, Func<SqlDataReader, T> read)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException($"No answer to: {sql}");
        return read(reader);
    }

    // ─── The site's files ─────────────────────────────────────────────────

    /// <summary>
    /// The assemblies in bin that aren't DNN's or a library DNN ships - each with the DNN version it was built against and
    /// whether it uses Telerik - read from their metadata. <paramref name="registered"/>: those an extension installed
    /// (null when the database couldn't say - then all count as registered).
    /// </summary>
    internal static IReadOnlyList<DnnAssembly> Assemblies(string directory, HashSet<string>? registered)
    {
        var bin = Path.Combine(directory, "bin");
        if (!Directory.Exists(bin)) return [];
        var list = new List<DnnAssembly>();
        foreach (var file in Directory.EnumerateFiles(bin, "*.dll"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                if (!md.IsAssembly) continue;
                var name = md.GetString(md.GetAssemblyDefinition().Name);
                if (IsShipped(name)) continue;
                Version? dnn = null;
                var telerik = false;
                foreach (var handle in md.AssemblyReferences)
                {
                    var reference = md.GetAssemblyReference(handle);
                    var referenced = md.GetString(reference.Name);
                    if (referenced.Equals("DotNetNuke", StringComparison.OrdinalIgnoreCase)) dnn = reference.Version;
                    if (referenced.StartsWith("Telerik.Web.UI", StringComparison.OrdinalIgnoreCase) ||
                        referenced.Equals("DotNetNuke.Web.Deprecated", StringComparison.OrdinalIgnoreCase) ||
                        referenced.Equals("DotNetNuke.Website.Deprecated", StringComparison.OrdinalIgnoreCase))
                        telerik = true;
                }
                list.Add(new DnnAssembly(Path.GetFileName(file), name, dnn, telerik, registered is null || registered.Contains(name)));
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A native DLL, or one in use - not .NET, or not readable now.
            }
        }
        return list;
    }

    private static bool IsShipped(string assembly) => ShippedPrefixes.Any(p => assembly.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsDnnName(string package) =>
        package.StartsWith("DotNetNuke", StringComparison.OrdinalIgnoreCase) || package.StartsWith("Dnn", StringComparison.OrdinalIgnoreCase);

    // ─── web.config, this PC ──────────────────────────────────────────────

    private static DnnSiteFacts WithWebConfig(DnnSiteFacts facts)
    {
        var path = Path.Combine(facts.Directory, "web.config");
        if (!File.Exists(path)) return facts;
        try
        {
            var root = XDocument.Load(path).Root;
            var settings = root?.Element("appSettings")?.Elements("add")
                .Select(a => ((string?)a.Attribute("key") ?? "", (string?)a.Attribute("value"))).ToList() ?? [];
            return facts with
            {
                HasMachineKey = root?.Element("system.web")?.Element("machineKey") is not null,
                AutoUpgrade = settings.FirstOrDefault(s => s.Item1.Equals("AutoUpgrade", StringComparison.OrdinalIgnoreCase)).Item2,
                CustomAppSettings = settings.Select(s => s.Item1).Where(k => k.Length > 0 && !DnnAppSettings.Contains(k) &&
                                                                             !k.StartsWith("aspnet:", StringComparison.OrdinalIgnoreCase)).ToList(),
                ConnectionStrings = root?.Element("connectionStrings")?.Elements("add").Select(a => (string?)a.Attribute("name") ?? "").Where(n => n.Length > 0).ToList() ?? []
            };
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return facts;
        }
    }

    private static int? NetFrameworkRelease()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
        return key?.GetValue("Release") is int release ? release : null;
    }

    private static long FolderBytes(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long? FreeBytes(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
