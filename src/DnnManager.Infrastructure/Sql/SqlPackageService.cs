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
        reporter.Info($"Running: dotnet tool install Microsoft.SqlPackage --tool-path {PrivateTemp.ToolsPath}");
        try
        {
            var r = await _proc.RunAsync("dotnet",
                new[] { "tool", "install", "Microsoft.SqlPackage", "--tool-path", PrivateTemp.ToolsPath }, ct);

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
            return Result.Fail($"BACPAC export failed: {Tail(err, outp)}");
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
        if (!r.Success)
        {
            var (err, outp) = (Hide(r.StdErr, saPassword), Hide(r.StdOut, saPassword));
            _log.LogError("SqlPackage import failed: {Err}\n{Out}", err, outp);
            return Result.Fail($"BACPAC import failed: {Tail(err, outp)}");
        }
        reporter.Success("BACPAC imported.");
        return Result.Ok();
    }

    /// <summary>A SQL login - or, without a user, Windows authentication as whoever runs DNN Manager.</summary>
    private static string ConnectionString(string server, string database, string user, string password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 60
        };
        if (user.Length == 0) builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = user;
            builder.Password = password;
        }
        return builder.ConnectionString;
    }

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

    private static string Tail(string err, string outp)
    {
        var text = string.IsNullOrWhiteSpace(err) ? outp : err;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? "(no output)" : string.Join(" | ", lines.TakeLast(3));
    }
}
