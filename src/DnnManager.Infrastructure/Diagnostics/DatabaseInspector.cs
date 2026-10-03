using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Sql;
using Microsoft.Data.SqlClient;

namespace DnnManager.Infrastructure.Diagnostics;

public sealed record SqlServerFacts(string? Instance, string? Version, string? Level, string? Edition, string? Collation, int? MajorVersion);

public sealed record DatabaseFile(string Name, string Type, double SizeMb, double? UsedMb, string MaxSize, string Growth, string PhysicalName);

public sealed record DatabaseProperties(
    string Name, int? CompatibilityLevel, string? Collation, string? RecoveryModel, string? State, bool? ReadOnly, bool? AutoShrink,
    bool? AutoClose, string? SnapshotIsolation, bool? ReadCommittedSnapshot, string? PageVerify, DateTime? Created,
    IReadOnlyList<DatabaseFile> Files, string? ConnectionEncrypted, string? SignedInAs);

public sealed record DnnPortalDetails(int Id, string Name, string? DefaultLanguage, string? HomeDirectory, bool Expired,
    string? PrimaryAlias, IReadOnlyList<(string Alias, string? Culture)> OtherAliases);

/// <summary>What DNN's own tables say - each part null when its table isn't there, or can't be read.</summary>
public sealed record DnnDatabaseFacts(
    string? Qualifier,
    string? Version,
    DateTime? Installed,
    DateTime? Upgraded,
    IReadOnlyList<DnnPortalDetails> Portals,
    int? Users,
    int? Roles,
    int? ScheduleItems,
    int? ScheduleEnabled,
    long? EventLogRecords,
    DateTime? EventLogOldest,
    DateTime? EventLogNewest,
    int? DesktopModules,
    IReadOnlyDictionary<string, int> Packages,
    IReadOnlyList<(string Type, bool Enabled)> AuthenticationProviders,
    IReadOnlyDictionary<string, string> HostSettings);

/// <param name="Problem">Why the database couldn't be reached at all; null when it was.</param>
/// <param name="Notes">What couldn't be read of it (a permission, a table) - the rest is still there.</param>
public sealed record DatabaseInspection(
    string? Problem,
    SqlServerFacts? Server,
    DatabaseProperties? Database,
    DnnDatabaseFacts? Dnn,
    IReadOnlyList<string> Notes);

/// <summary>
/// Reads a site's database for its Details page - with the site's own web.config connection (its login, or Windows
/// authentication as DNN Manager's user), never DNN Manager's settings. Read-only: SELECTs and server properties.
/// </summary>
public static class DatabaseInspector
{
    /// <summary>The host settings worth seeing while developing - never a secret one.</summary>
    private static readonly string[] HostSettingNames =
    [
        "SchedulerMode", "PerformanceSetting", "UseFriendlyUrls", "AutoAddPortalAlias", "DebugMode", "MaxUploadSize",
        "UseCustomErrorMessages", "EventLogBuffer", "SMTPServer", "DisableUpgradeCheck", "FileExtensions", "CacheProvider"
    ];

    public static async Task<DatabaseInspection> InspectAsync(SiteSqlConnection site, CancellationToken ct, int timeoutSeconds = 8)
    {
        var notes = new List<string>();
        SqlConnection conn;
        try
        {
            conn = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = site.Server,
                InitialCatalog = site.Database,
                UserID = site.User,
                Password = site.Password,
                IntegratedSecurity = site.User.Length == 0,
                Encrypt = true,
                TrustServerCertificate = true,
                ConnectTimeout = timeoutSeconds,
                ApplicationName = "DNN Manager (Details)"
            }.ConnectionString);
            await conn.OpenAsync(ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return new DatabaseInspection(FirstLine(ex.Message), null, null, null, notes);
        }

        await using (conn)
        {
            var server = await Try(notes, "the SQL Server's properties", async () =>
            {
                await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), " +
                                                     "CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(256)), " +
                                                     "CAST(SERVERPROPERTY('Collation') AS nvarchar(256)), CAST(SERVERPROPERTY('ProductMajorVersion') AS int)", conn);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return new SqlServerFacts(S(r, 0), S(r, 1), S(r, 2), S(r, 3), S(r, 4), r.IsDBNull(5) ? null : r.GetInt32(5));
            });
            var database = await Try(notes, "the database's properties", () => DatabaseAsync(conn, notes, ct));
            var dnn = await Try(notes, "DNN's tables", () => DnnAsync(conn, notes, ct));
            return new DatabaseInspection(null, server, database, dnn, notes);
        }
    }

    private static async Task<DatabaseProperties> DatabaseAsync(SqlConnection conn, List<string> notes, CancellationToken ct)
    {
        DatabaseProperties props;
        await using (var cmd = new SqlCommand(
                   "SELECT name, compatibility_level, collation_name, recovery_model_desc, state_desc, is_read_only, is_auto_shrink_on, " +
                   "is_auto_close_on, snapshot_isolation_state_desc, is_read_committed_snapshot_on, page_verify_option_desc, create_date " +
                   "FROM sys.databases WHERE database_id = DB_ID()", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            props = new DatabaseProperties(r.GetString(0), r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1)), S(r, 2), S(r, 3), S(r, 4),
                B(r, 5), B(r, 6), B(r, 7), S(r, 8), B(r, 9), S(r, 10), r.IsDBNull(11) ? null : r.GetDateTime(11), [], null, null);
        }

        var files = await Try(notes, "the database's files", async () =>
        {
            var list = new List<DatabaseFile>();
            await using var cmd = new SqlCommand(
                "SELECT name, type_desc, size * 8.0 / 1024, FILEPROPERTY(name, 'SpaceUsed') * 8.0 / 1024, max_size, growth, is_percent_growth, physical_name " +
                "FROM sys.database_files", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var maxPages = r.GetInt32(4);
                var growth = r.GetInt32(5);
                list.Add(new DatabaseFile(r.GetString(0), r.GetString(1), Convert.ToDouble(r.GetValue(2)),
                    r.IsDBNull(3) ? null : Convert.ToDouble(r.GetValue(3)),
                    maxPages switch { -1 => "unlimited", 0 => "no growth", 268435456 => "2 TB", _ => $"{maxPages * 8.0 / 1024:N0} MB" },
                    growth == 0 ? "none" : r.GetBoolean(6) ? $"{growth}%" : $"{growth * 8.0 / 1024:N0} MB",
                    r.GetString(7)));
            }
            return list;
        }) ?? [];

        // Not every login may look at its own connection (VIEW SERVER STATE) - then it isn't said.
        string? encrypted = null, signedInAs = null;
        try
        {
            await using var cmd = new SqlCommand("SELECT encrypt_option, SUSER_SNAME() FROM sys.dm_exec_connections WHERE session_id = @@SPID", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                encrypted = S(r, 0);
                signedInAs = S(r, 1);
            }
        }
        catch (SqlException)
        {
            try
            {
                await using var who = new SqlCommand("SELECT SUSER_SNAME()", conn);
                signedInAs = await who.ExecuteScalarAsync(ct) as string;
            }
            catch (SqlException) { /* nothing more to say */ }
        }
        return props with { Files = files, ConnectionEncrypted = encrypted, SignedInAs = signedInAs };
    }

    private static async Task<DnnDatabaseFacts?> DnnAsync(SqlConnection conn, List<string> notes, CancellationToken ct)
    {
        // DNN's tables can carry an objectQualifier prefix (dnn_Portals): found from the Version table next to Portals.
        var q = await DnnTables.QualifierAsync(conn, "Version", ct);
        if (q is null) return null;
        string T(string table) => DnnTables.Name(q, table);

        string? version = null;
        DateTime? installed = null, upgraded = null;
        await Try(notes, "DNN's Version table", async () =>
        {
            await using var cmd = new SqlCommand(
                $"SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build), CreatedDate, (SELECT MIN(CreatedDate) FROM {T("Version")}) " +
                $"FROM {T("Version")} ORDER BY Major DESC, Minor DESC, Build DESC", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                version = S(r, 0);
                upgraded = r.IsDBNull(1) ? null : r.GetDateTime(1);
                installed = r.IsDBNull(2) ? null : r.GetDateTime(2);
            }
            return true;
        });

        var portals = await Try(notes, "the portals", () => PortalsAsync(conn, q, ct)) ?? [];
        var users = await Count(conn, $"SELECT COUNT(*) FROM {T("Users")}", notes, "users", ct);
        var roles = await Count(conn, $"SELECT COUNT(*) FROM {T("Roles")}", notes, "roles", ct);
        var modules = await Count(conn, $"SELECT COUNT(*) FROM {T("DesktopModules")}", notes, "modules", ct);

        int? scheduled = null, enabled = null;
        await Try(notes, "the scheduler's jobs", async () =>
        {
            await using var cmd = new SqlCommand($"SELECT COUNT(*), SUM(CASE WHEN Enabled = 1 THEN 1 ELSE 0 END) FROM {T("Schedule")}", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            scheduled = r.GetInt32(0);
            enabled = r.IsDBNull(1) ? 0 : r.GetInt32(1);
            return true;
        });

        long? events = null;
        DateTime? oldest = null, newest = null;
        await Try(notes, "the event log", async () =>
        {
            await using var cmd = new SqlCommand($"SELECT COUNT_BIG(*), MIN(LogCreateDate), MAX(LogCreateDate) FROM {T("EventLog")}", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            events = r.GetInt64(0);
            oldest = r.IsDBNull(1) ? null : r.GetDateTime(1);
            newest = r.IsDBNull(2) ? null : r.GetDateTime(2);
            return true;
        });

        var packages = await Try(notes, "the installed extensions", async () =>
        {
            var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            await using var cmd = new SqlCommand($"SELECT PackageType, COUNT(*) FROM {T("Packages")} GROUP BY PackageType", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) byType[r.GetString(0)] = r.GetInt32(1);
            return byType;
        }) ?? [];

        var authentication = await Try(notes, "the authentication providers", async () =>
        {
            var list = new List<(string, bool)>();
            await using var cmd = new SqlCommand($"SELECT AuthenticationType, IsEnabled FROM {T("Authentication")} ORDER BY AuthenticationType", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetBoolean(1)));
            return list;
        }) ?? [];

        var settings = await Try(notes, "the host settings", async () =>
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using var cmd = new SqlCommand(
                $"SELECT SettingName, SettingValue FROM {T("HostSettings")} WHERE SettingName IN ({string.Join(", ", HostSettingNames.Select((_, i) => $"@n{i}"))})", conn);
            for (var i = 0; i < HostSettingNames.Length; i++) cmd.Parameters.AddWithValue($"@n{i}", HostSettingNames[i]);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) found[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            return found;
        }) ?? [];

        return new DnnDatabaseFacts(q.Length == 0 ? null : q, version, installed, upgraded, portals, users, roles, scheduled, enabled, events, oldest,
            newest, modules, packages, authentication, settings);
    }

    private static async Task<List<DnnPortalDetails>> PortalsAsync(SqlConnection conn, string q, CancellationToken ct)
    {
        string T(string table) => DnnTables.Name(q, table);
        bool localized, primaryFlag, culture;
        await using (var cols = new SqlCommand($"SELECT OBJECT_ID('{T("PortalLocalization")}'), COL_LENGTH('{T("PortalAlias")}', 'IsPrimary'), " +
                                               $"COL_LENGTH('{T("PortalAlias")}', 'CultureCode')", conn))
        await using (var r = await cols.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            localized = !r.IsDBNull(0);
            primaryFlag = !r.IsDBNull(1);
            culture = !r.IsDBNull(2);
        }
        var name = localized
            ? $"(SELECT TOP 1 pl.PortalName FROM {T("PortalLocalization")} pl WHERE pl.PortalID = p.PortalID " +
              "ORDER BY CASE WHEN pl.CultureCode = p.DefaultLanguage THEN 0 ELSE 1 END, pl.CultureCode)"
            : "NULL";

        var portals = new List<(int Id, string Name, string? Language, string? Home, bool Expired)>();
        await using (var cmd = new SqlCommand(
                         $"SELECT p.PortalID, {name}, p.DefaultLanguage, p.HomeDirectory, " +
                         $"CASE WHEN p.ExpiryDate IS NOT NULL AND p.ExpiryDate < GETDATE() THEN 1 ELSE 0 END FROM {T("Portals")} p ORDER BY p.PortalID", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                portals.Add((r.GetInt32(0), S(r, 1) ?? $"Portal {r.GetInt32(0)}", S(r, 2), S(r, 3), r.GetInt32(4) == 1));

        var aliases = new Dictionary<int, List<(string Alias, string? Culture, bool Primary)>>();
        await using (var cmd = new SqlCommand(
                         $"SELECT PortalID, HTTPAlias, {(culture ? "CultureCode" : "NULL")}, {(primaryFlag ? "CAST(IsPrimary AS int)" : "0")} " +
                         $"FROM {T("PortalAlias")} ORDER BY PortalID, {(primaryFlag ? "IsPrimary DESC, " : "")}PortalAliasID", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                if (!aliases.TryGetValue(id, out var list)) aliases[id] = list = [];
                list.Add((r.GetString(1), S(r, 2), r.GetInt32(3) == 1));
            }

        return portals.Select(p =>
        {
            var list = aliases.GetValueOrDefault(p.Id) ?? [];
            var primary = list.FirstOrDefault(a => a.Primary).Alias ?? list.FirstOrDefault().Alias;
            return new DnnPortalDetails(p.Id, p.Name, p.Language, p.Home, p.Expired, primary,
                list.Where(a => a.Alias != primary).Select(a => (a.Alias, a.Culture)).ToList());
        }).ToList();
    }

    private static async Task<int?> Count(SqlConnection conn, string sql, List<string> notes, string what, CancellationToken ct) =>
        await Try<int?>(notes, $"the {what}", async () =>
        {
            await using var cmd = new SqlCommand(sql, conn);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        });

    /// <summary>One part of the inspection: what goes wrong is a note - the other parts still come.</summary>
    private static async Task<T?> Try<T>(List<string> notes, string what, Func<Task<T>> read)
    {
        try { return await read(); }
        catch (SqlException ex)
        {
            notes.Add($"{char.ToUpper(what[0])}{what[1..]} couldn't be read: {FirstLine(ex.Message)}");
            return default;
        }
    }

    private static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
    private static bool? B(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToBoolean(r.GetValue(i));
    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
