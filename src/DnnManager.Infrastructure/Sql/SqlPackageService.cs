using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// Wraps the <c>SqlPackage</c> CLI to export an (Azure) SQL database to a .bacpac and import it
/// into a local SQL Server. SqlPackage is the supported way to copy an Azure SQL Database, which
/// cannot produce a native .bak.
/// </summary>
public sealed class SqlPackageService(ProcessRunner proc, ILogger<SqlPackageService> log) : IBacpacService
{
    private readonly ProcessRunner _proc = proc;
    private readonly ILogger<SqlPackageService> _log = log;
    // The version installed: a known one, not whatever nuget.org has as latest the day it is first needed - it runs as
    // Administrator. Raised by hand after a look at its release notes.
    internal const string PinnedVersion = "170.5.96";

    private const string InstallHint =
        "SqlPackage was not found. Install the .NET SDK for all users and try again - DNN Manager then installs SqlPackage itself.";

    // The .NET (Core) build of SqlPackage throws "4096 (0x1000) is an invalid culture
    // identifier" because ICU can't resolve that custom-locale LCID a SQL collation maps to.
    // Forcing the .NET globalization backend to Windows NLS (instead of ICU) resolves such
    // locales the way the OS does. (Invariant mode is explicitly rejected by SqlPackage.)
    private static readonly Dictionary<string, string?> SqlPackageEnv = new()
    {
        ["DOTNET_SYSTEM_GLOBALIZATION_USENLS"] = "true"
    };

    public async Task<Result> EnsureAvailableAsync(IProgressReporter reporter, CancellationToken ct)
    {
        if (ResolveExe() is not null) return Result.Ok();

        // Not installed - provisioned as a .NET tool in DNN Manager's own tools folder, which only administrators can
        // change: it runs as Administrator. (A global tool in ~/.dotnet/tools is the user's to change - never run from
        // there.) Requires the .NET SDK; we surface a manual hint if that's missing.
        reporter.Info("SqlPackage not found - installing it (one-time)…");
        reporter.Info($"Running: dotnet tool install Microsoft.SqlPackage --version {PinnedVersion} --tool-path {PrivateTemp.ToolsPath}");
        try
        {
            // Only from nuget.org, by a NuGet.Config of DNN Manager's own: the user's (%APPDATA%\NuGet) is theirs to change,
            // and a package source added there would put its "SqlPackage" in a folder whose programs run as Administrator.
            // The source mapping says it once more: every package from nuget.org, and from nowhere else.
            var nugetConfig = Path.Combine(PrivateTemp.Path, "sqlpackage.nuget.config");
            await File.WriteAllTextAsync(nugetConfig,
                """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
                  </packageSources>
                  <packageSourceMapping>
                    <packageSource key="nuget.org">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """, ct);
            var r = await _proc.RunAsync("dotnet",
                new[] { "tool", "install", "Microsoft.SqlPackage", "--version", PinnedVersion, "--tool-path", PrivateTemp.ToolsPath, "--configfile", nugetConfig }, ct);

            // `install` exits non-zero when the tool is already present, so don't trust the exit
            // code alone - the real test is whether we can now resolve the executable.
            if (ResolveExe() is not null)
            {
                reporter.Success("SqlPackage is ready.");
                return Result.Ok();
            }

            _log.LogError("SqlPackage auto-install failed: {Err}\n{Out}", r.StdErr, r.StdOut);
            return Result.Fail($"Automatic SqlPackage install did not succeed: {Tail(r.StdErr, r.StdOut)}. " +
                               "Install the .NET SDK for all users (dotnet in Program Files), then retry.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not run 'dotnet tool install' for SqlPackage");
            return Result.Fail($"Could not run 'dotnet' to install SqlPackage ({ex.Message}). " +
                               "Install the .NET SDK for all users (so 'dotnet' is on PATH), then retry.");
        }
    }

    /// <summary>
    /// Finds the SqlPackage executable: DNN Manager's own tools folder, then a copy on PATH that only administrators can
    /// change (e.g. one installed with SQL Server in Program Files). It runs as Administrator: never one of the user's own
    /// (a global .NET tool in ~/.dotnet/tools).
    /// </summary>
    private static string? ResolveExe()
    {
        var toolPath = Path.Combine(PrivateTemp.ToolsPath, "sqlpackage.exe");
        if (File.Exists(toolPath)) return toolPath;
        return TrustedPrograms.Find("sqlpackage.exe", out _) is { } trusted && Path.IsPathRooted(trusted) ? trusted : null;
    }

    public async Task<Result> ExportAsync(SiteSqlConnection source, string bacpacPath, IProgressReporter reporter, CancellationToken ct)
    {
        var exe = ResolveExe();
        if (exe is null) return Result.Fail(InstallHint);

        var cs = ConnectionString(NormalizeServer(source.Server), source.Database, source.User, source.Password);

        var args = new[]
        {
            "/Action:Export",
            $"/SourceConnectionString:{cs}",
            $"/TargetFile:{bacpacPath}",
            "/OverwriteFiles:True"
        };

        reporter.Info($"Exporting [{source.Database}] from {NormalizeServer(source.Server)} (BACPAC)");
        reporter.Info("This can take several minutes for a large database…");
        var r = await _proc.RunAsync(exe, args, ct, SqlPackageEnv);
        if (!r.Success)
        {
            // SqlPackage may repeat its connection string: the password never goes into the log or a message.
            var (err, outp) = (Hide(r.StdErr, source.Password), Hide(r.StdOut, source.Password));
            _log.LogError("SqlPackage export failed: {Err}\n{Out}", err, outp);
            TryDelete(bacpacPath); // don't leave a partial/zero-byte .bacpac behind for a later import to pick.
            return Result.Fail($"BACPAC export failed: {Tail(err, outp)}{UnseenLoginHint(err + outp, source.User)}");
        }
        if (!File.Exists(bacpacPath) || new FileInfo(bacpacPath).Length == 0)
        {
            TryDelete(bacpacPath);
            return Result.Fail("BACPAC export reported success but produced no usable file.");
        }
        reporter.Success($"Exported BACPAC ({new FileInfo(bacpacPath).Length / 1024d / 1024d:N1} MB).");
        return Result.Ok();
    }

    public async Task<Result> ImportAsync(string targetServer, string saUser, string saPassword,
        string databaseName, string bacpacPath, IProgressReporter reporter, CancellationToken ct)
    {
        var exe = ResolveExe();
        if (exe is null) return Result.Fail(InstallHint);

        var cs = ConnectionString(targetServer, databaseName, saUser, saPassword);

        var args = new List<string>
        {
            "/Action:Import",
            $"/TargetConnectionString:{cs}",
            $"/SourceFile:{bacpacPath}"
        };

        reporter.Info($"Importing BACPAC into [{databaseName}]");
        reporter.Info("This can take several minutes…");
        var r = await _proc.RunAsync(exe, args, ct, SqlPackageEnv);

        // A partially contained database - one with users that have a password of their own, as an Azure SQL database
        // often has - can only be created on a server with 'contained database authentication' on, and SQL Server
        // (the container too) has it off. SqlPackage then stops at ALTER DATABASE … SET CONTAINMENT = PARTIAL, before
        // any data: switched on, the empty database it left dropped, and the import run once more.
        if (!r.Success && NeedsContainedAuthentication(r.StdErr + r.StdOut))
        {
            reporter.Info($"The database has users with a password of their own (a contained database) - switching on " +
                          $"'contained database authentication' on {targetServer}, which SQL Server needs for it");
            var on = await EnableContainedAuthenticationAsync(targetServer, saUser, saPassword, databaseName, ct);
            if (on.Success)
                r = await _proc.RunAsync(exe, args, ct, SqlPackageEnv);
            else
                _log.LogWarning("Could not switch on contained database authentication: {Err}", on.Error);
        }

        if (!r.Success)
        {
            var (err, outp) = (Hide(r.StdErr, saPassword), Hide(r.StdOut, saPassword));
            _log.LogError("SqlPackage import failed: {Err}\n{Out}", err, outp);
            return Result.Fail($"BACPAC import failed: {ImportError(err, outp)}");
        }
        reporter.Success("BACPAC imported.");
        return Result.Ok();
    }

    /// <summary>SQL Server's Msg 12824: the database is (partially) contained and the server has contained database authentication off.</summary>
    internal static bool NeedsContainedAuthentication(string output) =>
        output.Contains("Msg 12824", StringComparison.Ordinal) ||
        output.Contains("'contained database authentication' must be set to 1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Switches on 'contained database authentication' on <paramref name="server"/> and drops the empty
    /// <paramref name="database"/> the failed import left - only when it has no table: SqlPackage imports into a new or
    /// empty database, and one with tables in it is never this method's to drop.
    /// </summary>
    private static async Task<Result> EnableContainedAuthenticationAsync(string server, string user, string password,
        string database, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(ConnectionStrings.For(server, "master", user, password, 30).ConnectionString);
            await conn.OpenAsync(ct);
            await using (var on = new SqlCommand(
                "EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;", conn))
                await on.ExecuteNonQueryAsync(ct);

            // The database name goes in as a parameter - QUOTENAME builds the statement on the server.
            await using var drop = new SqlCommand(
                "IF DB_ID(@db) IS NOT NULL BEGIN " +
                "  DECLARE @tables int; DECLARE @count nvarchar(max) = N'SELECT @n = COUNT(*) FROM ' + QUOTENAME(@db) + N'.sys.tables'; " +
                "  EXEC sp_executesql @count, N'@n int OUTPUT', @n = @tables OUTPUT; " +
                "  IF @tables = 0 BEGIN DECLARE @sql nvarchar(max) = N'DROP DATABASE ' + QUOTENAME(@db); EXEC (@sql); END " +
                "END", conn);
            drop.Parameters.AddWithValue("@db", database);
            await drop.ExecuteNonQueryAsync(ct);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>
    /// What went wrong in an import: SQL Server's own message (the "Msg …" line) when there is one - the last lines of
    /// SqlPackage's output are the script it ran, not why it failed.
    /// </summary>
    internal static string ImportError(string err, string outp)
    {
        var text = string.IsNullOrWhiteSpace(err) ? outp : err;
        var msg = text.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("Msg ", StringComparison.Ordinal) && l.Contains("Level ", StringComparison.Ordinal));
        if (msg is null) return Tail(err, outp);
        // "Error SQL72014: Core Microsoft SqlClient Data Provider: Msg 12824, Level 16, State 1, Line 5 The sp_configure…"
        var at = msg.IndexOf("Msg ", StringComparison.Ordinal);
        return msg[at..];
    }

    /// <summary>
    /// A SQL login - or, without a user, Windows authentication as whoever runs DNN Manager. On SqlPackage's command line,
    /// password and all: Import and Export take no publish profile, and the other ways of giving the password
    /// (/SourcePassword, /TargetPassword) are command-line arguments too. SqlPackage runs elevated, as DNN Manager does,
    /// so programs without administrator rights can't read its command line; its output is never logged with the
    /// password in it (<see cref="Hide"/>).
    /// </summary>
    private static string ConnectionString(string server, string database, string user, string password) =>
        ConnectionStrings.For(server, database, user, password, 60).ConnectionString;

    // SqlPackage/SqlClient accept "host,port"; drop the optional "tcp:" prefix.
    private static string NormalizeServer(string server)
    {
        var s = server.Trim();
        return s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    /// <summary><paramref name="text"/> with <paramref name="password"/> blanked out - for what goes into the log or a message.</summary>
    internal static string Hide(string text, string? password) =>
        string.IsNullOrEmpty(password) ? text : text.Replace(password, "********", StringComparison.Ordinal);

    /// <summary>
    /// What to do when SqlPackage met a user whose login the exporting one can't see (SQL71501): the .bacpac holds the login
    /// of every user, and a login that isn't a sysadmin sees no other login - a live site's restored backup keeps its host's user.
    /// </summary>
    private static string UnseenLoginHint(string output, string user) =>
        output.Contains("SQL71501", StringComparison.Ordinal) && output.Contains("reference to Login", StringComparison.OrdinalIgnoreCase)
            ? $" The database has a user whose login {(user.Length > 0 ? user : "your Windows account")} can't see - export it " +
              "signed in as a sysadmin (or a login with VIEW ANY DEFINITION), or drop that user if the site doesn't use it."
            : "";

    private static string Tail(string err, string outp)
    {
        var text = string.IsNullOrWhiteSpace(err) ? outp : err;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? "(no output)" : string.Join(" | ", lines.TakeLast(3));
    }
}
