using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Infrastructure.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Dnn;

/// <summary>
/// DNN's own unattended install - <c>GET /Install/Install.aspx?mode=install</c> with an install template that names the
/// host account and the website - and what DNN's installation wizard does around it. The first request has DNN write new
/// machine keys and redirect to itself; the second runs the whole install and streams its progress, ending in
/// "Installation Complete" (DNN then deletes its installer). Proven against clean DNN 10.3.3 installs with SQL Server
/// (Windows and SQL authentication) and LocalDB file databases; the result matches a wizard install.
/// </summary>
public sealed partial class DnnInstaller : IDnnInstaller
{
    // Measured: 35-60 s for the whole install, gaps between progress lines of at most ~11 s.
    private static readonly TimeSpan InstallLimit = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QuietWarning = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan QuietLimit = TimeSpan.FromMinutes(10);
    // The first page after the install runs the modules' upgrades: 12-45 s measured.
    private static readonly TimeSpan WarmUpLimit = TimeSpan.FromMinutes(3);
    private const int MaxRedirects = 6;

    private readonly ILogger<DnnInstaller> _log;

    public DnnInstaller(ILogger<DnnInstaller> log) => _log = log;

    // ─── Before installing ────────────────────────────────────────────────

    public Result CheckPackage(string siteDirectory)
    {
        if (siteDirectory.Length > DnnAccountRules.MaxSitePathLength)
            return Result.Fail($"The site's folder is too deep for DNN's installer ({siteDirectory.Length} characters, at most " +
                               $"{DnnAccountRules.MaxSitePathLength}) - some of its extensions would fail to install. Use a shorter " +
                               @"projects folder (Settings → Projects), e.g. C:\DNN.");
        var install = Path.Combine(siteDirectory, "Install");
        if (!File.Exists(Path.Combine(install, "Install.aspx")))
            return Result.Fail(@"The package has no Install\Install.aspx - it isn't a DNN install package, or it was installed before.");
        if (!File.Exists(Path.Combine(install, DnnInstallTemplate.ShippedFileName)))
            return Result.Fail($@"The package has no Install\{DnnInstallTemplate.ShippedFileName} - DNN's install template.");
        if (File.Exists(Path.Combine(siteDirectory, "installBlocker.lock")))
            return Result.Fail("An installation of this site is running, or one was interrupted (installBlocker.lock).");
        var settings = AppSettings(Path.Combine(siteDirectory, "web.config"));
        if (settings.ContainsKey("InstallVersion") || settings.ContainsKey("InstallationDate"))
            return Result.Fail("This DNN has been installed (or started installing) before - an automatic install needs a fresh package.");
        return Result.Ok();
    }

    public async Task<Result> PrepareLocalDbFileAsync(string siteDirectory, DatabaseConnection connection, CancellationToken ct)
    {
        try
        {
            await LocalDbFiles.WithDatabaseAsync(siteDirectory, connection, async conn =>
            {
                // The shipped file is at level 100 (SQL Server 2008); DNN 10's HTML module needs 130+ (STRING_SPLIT).
                using var raise = new SqlCommand(
                    "DECLARE @level int = (SELECT compatibility_level FROM sys.databases WHERE name = 'model'); " +
                    "IF (SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID()) < @level BEGIN " +
                    "DECLARE @sql nvarchar(200) = N'ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = ' + CAST(@level AS nvarchar(10)); EXEC (@sql); END", conn);
                await raise.ExecuteNonQueryAsync(ct);
                return true;
            }, ct);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Preparing the LocalDB file failed");
            return Result.Fail($@"Could not prepare App_Data\{connection.Database}: {FirstLine(ex.Message)}");
        }
    }

    // ─── The install ──────────────────────────────────────────────────────

    public async Task<Result> InstallAsync(DnnSiteAddress site, DnnAccount account, DatabaseConnection database,
        IProgressReporter reporter, CancellationToken ct)
    {
        var problems = DnnAccountRules.Problems(account);
        if (problems.Count > 0) return Result.Fail(string.Join(" ", problems));

        var secrets = new Secrets(account.Password, database.Password);
        try
        {
            // Written right before the request: while DNN isn't installed its installer asks nobody for a password.
            DnnInstallTemplate.Write(site.Directory, account, site.Alias);
            return await RunAsync(site, CountPackages(site.Directory), secrets, reporter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("DNN install failed: {Message}", secrets.Hide(ex.Message));
            return Result.Fail(secrets.Hide(FirstLine(ex.Message)));
        }
        finally
        {
            // It holds the host password - and with it any visitor could run the installer again.
            DnnInstallTemplate.Delete(site.Directory);
        }
    }

    private async Task<Result> RunAsync(DnnSiteAddress site, int packages, Secrets secrets, IProgressReporter reporter, CancellationToken ct)
    {
        using var http = CreateClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(InstallLimit);

        var output = new DnnInstallOutput();
        var installed = 0;
        var lastHeard = DateTime.UtcNow;
        var warned = false;
        var stuck = false;
        // DNN writes a line per step; a long silence means it hangs (e.g. on a locked file).
        await using var watchdog = new Timer(_ =>
        {
            var quiet = DateTime.UtcNow - lastHeard;
            if (quiet > QuietLimit)
            {
                stuck = true;
                try { limit.Cancel(); } catch (ObjectDisposedException) { }
            }
            else if (quiet > QuietWarning && !warned)
            {
                warned = true;
                reporter.Warn("DNN hasn't reported progress for 2 minutes - still waiting for its installation.");
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        var path = "/Install/Install.aspx?mode=install";
        try
        {
            for (var request = 1; ; request++)
            {
                if (request > MaxRedirects) return Result.Fail("DNN's installer kept redirecting to itself.");
                lastHeard = DateTime.UtcNow;
                using var response = await http.SendAsync(Get(site, path), HttpCompletionOption.ResponseHeadersRead, limit.Token);
                lastHeard = DateTime.UtcNow;
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    var target = Target(site, response);
                    if (target?.AbsolutePath.Equals("/Install/Install.aspx", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // DNN wrote new machine keys, restarts, and asks to be called again.
                        reporter.Progress("Running DNN installation: DNN prepares its configuration…");
                        path = target.PathAndQuery;
                        continue;
                    }
                    return Result.Fail($"DNN's installer sent the request to {target?.PathAndQuery ?? "nowhere"} - often a sign that the " +
                                       "site may not write its own web.config.");
                }
                if (status != 200)
                    return Result.Fail($"DNN's installer answered HTTP {status} {response.ReasonPhrase} - see the site's logs in Portals\\_default\\Logs.");

                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
                var decoder = Encoding.UTF8.GetDecoder();
                var bytes = new byte[16 * 1024];
                var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                int read;
                while ((read = await stream.ReadAsync(bytes, limit.Token)) > 0)
                {
                    lastHeard = DateTime.UtcNow;
                    var count = decoder.GetChars(bytes, 0, read, chars, 0);
                    foreach (var step in output.Add(new string(chars, 0, count))) Report(step);
                }
                foreach (var step in output.Finish()) Report(step);
                break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Fail(stuck
                ? "DNN stopped reporting progress for 10 minutes - its installation is stuck."
                : "DNN's installation took longer than 30 minutes.");
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail($"Could not reach the site at {site.Url}: {FirstLine(ex.Message)}");
        }
        catch (IOException ex)
        {
            return Result.Fail($"The connection to the site broke off during the installation: {FirstLine(ex.Message)}");
        }

        var outcome = output.Outcome();
        return outcome.Success ? outcome : Result.Fail(secrets.Hide(outcome.Error!));

        void Report(DnnInstallStep step)
        {
            var text = secrets.Hide(step.Text);
            if (step.Succeeded == false)
            {
                reporter.Fail($"DNN: {text}");
                return;
            }
            if (StartsWith(text, "Installing Package File"))
            {
                installed++;
                reporter.Progress($"Running DNN installation: installing {After(text, "Installing Package File")} " +
                                  (packages > 0 ? $"({Math.Min(installed, packages)} of {packages})…" : "…"));
            }
            else if (StartsWith(text, "Creating Site Alias")) reporter.Info($"Creating portal alias {After(text, "Creating Site Alias")}…");
            else if (StartsWith(text, "Creating Site")) reporter.Info($"Creating portal '{After(text, "Creating Site")}'…");
            else if (StartsWith(text, "Successfully Installed Site")) reporter.Success("Portal created.");
            else if (StartsWith(text, "Installing DNN")) reporter.Info("Running DNN installation…");
            else if (!StartsWith(text, "Installation Complete")) reporter.Progress($"Running DNN installation: {text}…");
        }
    }

    /// <summary>The extension packages DNN installs with itself - one progress line each.</summary>
    private static int CountPackages(string siteDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(siteDirectory, "Install"), "*.zip", SearchOption.AllDirectories).Count();
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // ─── After installing ─────────────────────────────────────────────────

    public async Task<Result> CompleteAsync(DnnSiteAddress site, DnnAccount account, DatabaseConnection database, CancellationToken ct)
    {
        var secrets = new Secrets(account.Password, database.Password);
        try
        {
            var expected = DnnVersionOf(site.Directory);
            return await WithSiteDatabaseAsync(site.Directory, database, async conn =>
            {
                var q = await DnnTables.QualifierAsync(conn, "Version", ct);
                if (q is null) return Result.Fail("The database has no DNN tables - the installation didn't reach it.");

                string? installed;
                using (var version = new SqlCommand($"SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build) FROM dbo.[{q}Version] ORDER BY VersionId DESC", conn))
                    installed = await version.ExecuteScalarAsync(ct) as string;
                if (expected is not null && installed != expected)
                    return Result.Fail($"The database is at DNN {installed ?? "(nothing)"}, the site's files at {expected}.");

                using (var alias = new SqlCommand($"SELECT COUNT(*) FROM dbo.[{q}PortalAlias] WHERE PortalID = 0 AND HTTPAlias = @alias", conn))
                {
                    alias.Parameters.AddWithValue("@alias", site.Alias);
                    if (Convert.ToInt32(await alias.ExecuteScalarAsync(ct)) == 0)
                        return Result.Fail($"DNN didn't create the portal alias {site.Alias}.");
                }

                // Install.aspx makes the host - the site's administrator since DNN 9.3 - change its password at the first
                // sign-in; the wizard doesn't. Same for the pages it marks secure (the wizard's SSL step undoes that).
                using (var host = new SqlCommand(
                           $"UPDATE dbo.[{q}Users] SET UpdatePassword = 0 WHERE IsSuperUser = 1 AND Username = @user; SELECT @@ROWCOUNT; " +
                           $"UPDATE dbo.[{q}Tabs] SET IsSecure = 0 WHERE PortalID = 0 AND IsSecure = 1;", conn))
                {
                    host.Parameters.AddWithValue("@user", account.UserName);
                    if (Convert.ToInt32(await host.ExecuteScalarAsync(ct)) == 0)
                        return Result.Fail($"DNN didn't create the host account '{account.UserName}'.");
                }
                return Result.Ok();
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Completing the DNN install failed: {Message}", secrets.Hide(ex.Message));
            return Result.Fail($"Could not check the installed site's database: {secrets.Hide(FirstLine(ex.Message))}");
        }
    }

    public async Task<Result> WarmUpAsync(DnnSiteAddress site, DateTime installStartedUtc, IProgressReporter reporter, CancellationToken ct)
    {
        reporter.Progress("Opening the new site for the first time (DNN finishes its modules then)…");
        var openedAt = DateTime.Now;
        using var http = CreateClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(WarmUpLimit);
        var path = "/";
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (hop >= 10) return Result.Fail("The site keeps redirecting.");
                using var response = await http.SendAsync(Get(site, path), limit.Token);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    var target = Target(site, response);
                    if (target is null) return Result.Fail($"The site answered HTTP {status} without saying where to.");
                    if (target.AbsolutePath.StartsWith("/Install", StringComparison.OrdinalIgnoreCase))
                        return Result.Fail("The site still sends visitors to DNN's installer.");
                    if (target.AbsolutePath.Contains("ErrorPage", StringComparison.OrdinalIgnoreCase) ||
                        target.Query.Contains("error=", StringComparison.OrdinalIgnoreCase))
                        return Result.Fail(@"The site shows DNN's error page - see its logs in Portals\_default\Logs.");
                    path = target.PathAndQuery;
                    continue;
                }
                if (status != 200)
                    return Result.Fail($@"The site answered HTTP {status} after the installation - see its logs in Portals\_default\Logs.");
                var html = await response.Content.ReadAsStringAsync(limit.Token);
                if (html.Contains("InstallWizard", StringComparison.OrdinalIgnoreCase))
                    return Result.Fail("The site still shows DNN's installation wizard.");
                break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Fail("The new site didn't answer within 3 minutes.");
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail($"Could not reach the new site at {site.Url}: {FirstLine(ex.Message)}");
        }

        foreach (var problem in LogProblems(site.Directory, installStartedUtc, openedAt))
            reporter.Warn(problem);
        return Result.Ok();
    }

    /// <summary>
    /// What DNN logged as an error during the install or the first visit after it. Before its tables exist DNN always logs
    /// a few errors about them (GetDatabaseVersion, GetHostSettings) - only what came after the install counts there.
    /// </summary>
    private static IEnumerable<string> LogProblems(string siteDirectory, DateTime installStartedUtc, DateTime openedAt)
    {
        var problems = new List<string>();
        var logs = Path.Combine(siteDirectory, "Portals", "_default", "Logs");
        if (Directory.Exists(logs))
        {
            foreach (var file in Directory.EnumerateFiles(logs, "InstallerLog*.resources"))
                if (ReadShared(file).FirstOrDefault(l => l.Contains("[ERROR]", StringComparison.Ordinal)) is { } error)
                    problems.Add($"DNN's installer logged an error: {Short(error)} (Portals\\_default\\Logs\\{Path.GetFileName(file)})");

            foreach (var file in Directory.EnumerateFiles(logs, "*.log.resources"))
                foreach (var line in ReadShared(file).Where(l => l.Contains("[ERROR]", StringComparison.Ordinal) || l.Contains("[FATAL]", StringComparison.Ordinal)))
                    if (LogTime(line) is { } at && at >= openedAt)
                    {
                        problems.Add($"DNN logged an error on the first visit: {Short(line)} (Portals\\_default\\Logs\\{Path.GetFileName(file)})");
                        break;
                    }
        }

        // A database script that ran cleanly leaves an empty log behind.
        var scripts = Path.Combine(siteDirectory, "Providers", "DataProviders", "SqlDataProvider");
        if (Directory.Exists(scripts))
            foreach (var file in Directory.EnumerateFiles(scripts, "*.log.resources"))
                if (File.GetLastWriteTimeUtc(file) >= installStartedUtc &&
                    ReadShared(file).FirstOrDefault(l => l.Trim().Trim('\uFEFF').Length > 0) is { } first)
                    problems.Add($"A DNN database script reported a problem: {Short(first)} (Providers\\DataProviders\\SqlDataProvider\\{Path.GetFileName(file)})");
        return problems.Take(5);
    }

    public void CleanUp(string siteDirectory, bool installed)
    {
        DnnInstallTemplate.Delete(siteDirectory);
        TryDelete(Path.Combine(siteDirectory, "installBlocker.lock"));
        if (!installed) return;

        // DNN deletes its installer pages after an install - in case it didn't, nobody should run them again.
        foreach (var page in new[] { "Install.aspx", "InstallWizard.aspx", "UpgradeWizard.aspx" })
            foreach (var file in new[] { page, page + ".cs", page + ".designer.cs" })
                TryDelete(Path.Combine(siteDirectory, "Install", file));

        // Copies of web.config from the install - with the connection string, password included.
        var config = Path.Combine(siteDirectory, "Config");
        if (Directory.Exists(config))
            foreach (var backup in Directory.EnumerateDirectories(config, "Backup_*"))
                try { Directory.Delete(backup, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
    }

    // ─── Host password ────────────────────────────────────────────────────

    public async Task<Result> ChangeHostPasswordAsync(string siteDirectory, DatabaseConnection database, string userName,
        string newPassword, CancellationToken ct)
    {
        var membership = Membership.Read(Path.Combine(siteDirectory, "web.config"));
        if (membership is null) return Result.Fail("The site's web.config has no membership provider to go by.");
        if (!membership.PasswordFormat.Equals("Hashed", StringComparison.OrdinalIgnoreCase))
            return Result.Fail($"This site keeps passwords '{membership.PasswordFormat}', not hashed - change the password in DNN (Persona Bar → Users).");
        if (membership.HashAlgorithm is null || !MembershipPasswords.IsSupported(membership.HashAlgorithm))
            return Result.Fail("The site's web.config doesn't say how passwords are hashed - change the password in DNN (Persona Bar → Users).");
        if (membership.Problem(newPassword) is { } problem) return Result.Fail(problem);

        var secrets = new Secrets(newPassword, database.Password);
        var salt = MembershipPasswords.NewSalt();
        var hash = MembershipPasswords.Hash(newPassword, salt, membership.HashAlgorithm);
        try
        {
            return await WithSiteDatabaseAsync(siteDirectory, database, async conn =>
            {
                var q = await DnnTables.QualifierAsync(conn, "Version", ct);
                if (q is null) return Result.Fail("The site's database has no DNN tables.");
                using (var host = new SqlCommand($"SELECT COUNT(*) FROM dbo.[{q}Users] WHERE Username = @user AND IsSuperUser = 1", conn))
                {
                    host.Parameters.AddWithValue("@user", userName);
                    if (Convert.ToInt32(await host.ExecuteScalarAsync(ct)) == 0)
                        return Result.Fail($"'{userName}' isn't a host account of this site.");
                }

                await using var transaction = (SqlTransaction)await conn.BeginTransactionAsync(ct);
                using (var set = new SqlCommand("dbo.aspnet_Membership_SetPassword", conn, transaction) { CommandType = System.Data.CommandType.StoredProcedure })
                {
                    set.Parameters.AddWithValue("@ApplicationName", membership.ApplicationName);
                    set.Parameters.AddWithValue("@UserName", userName);
                    set.Parameters.AddWithValue("@NewPassword", hash);
                    set.Parameters.AddWithValue("@PasswordSalt", salt);
                    set.Parameters.AddWithValue("@CurrentTimeUtc", DateTime.UtcNow);
                    set.Parameters.AddWithValue("@PasswordFormat", 1);
                    var returned = set.Parameters.Add("@return", System.Data.SqlDbType.Int);
                    returned.Direction = System.Data.ParameterDirection.ReturnValue;
                    await set.ExecuteNonQueryAsync(ct);
                    if (returned.Value is int code && code != 0)
                        return Result.Fail($"DNN's membership has no user '{userName}' in application '{membership.ApplicationName}'.");
                }
                using (var unlock = new SqlCommand("dbo.aspnet_Membership_UnlockUser", conn, transaction) { CommandType = System.Data.CommandType.StoredProcedure })
                {
                    unlock.Parameters.AddWithValue("@ApplicationName", membership.ApplicationName);
                    unlock.Parameters.AddWithValue("@UserName", userName);
                    await unlock.ExecuteNonQueryAsync(ct);
                }
                using (var flag = new SqlCommand($"UPDATE dbo.[{q}Users] SET UpdatePassword = 0 WHERE Username = @user AND IsSuperUser = 1", conn, transaction))
                {
                    flag.Parameters.AddWithValue("@user", userName);
                    await flag.ExecuteNonQueryAsync(ct);
                }
                await transaction.CommitAsync(ct);
                return Result.Ok();
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Changing the host password failed: {Message}", secrets.Hide(ex.Message));
            return Result.Fail($"Could not change the password: {secrets.Hide(FirstLine(ex.Message))}");
        }
    }

    /// <summary>The site's <c>membership</c> settings from web.config - what decides how DNN stores and checks passwords.</summary>
    private sealed record Membership(string PasswordFormat, string? HashAlgorithm, string ApplicationName, int MinLength,
        int MinNonAlphanumeric, string? StrengthPattern)
    {
        public static Membership? Read(string webConfig)
        {
            if (!File.Exists(webConfig)) return null;
            var membership = XDocument.Load(webConfig).Root?.Element("system.web")?.Element("membership");
            if (membership is null) return null;
            var name = (string?)membership.Attribute("defaultProvider");
            var provider = membership.Element("providers")?.Elements("add")
                .FirstOrDefault(p => name is null || string.Equals((string?)p.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return null;
            return new Membership(
                (string?)provider.Attribute("passwordFormat") ?? "Hashed",
                (string?)membership.Attribute("hashAlgorithmType") is { Length: > 0 } algorithm ? algorithm : null,
                (string?)provider.Attribute("applicationName") ?? "DotNetNuke",
                int.TryParse((string?)provider.Attribute("minRequiredPasswordLength"), out var length) ? length : DnnAccountRules.MinPasswordLength,
                int.TryParse((string?)provider.Attribute("minRequiredNonalphanumericCharacters"), out var symbols) ? symbols : 0,
                (string?)provider.Attribute("passwordStrengthRegularExpression") is { Length: > 0 } pattern ? pattern : null);
        }

        /// <summary>What the site's membership rules have against <paramref name="password"/>; null when it may be used.</summary>
        public string? Problem(string password)
        {
            if (password.Length < MinLength) return $"The site wants passwords of at least {MinLength} characters.";
            if (password.Length > DnnAccountRules.MaxPasswordLength) return $"The password can have at most {DnnAccountRules.MaxPasswordLength} characters.";
            if (password.Count(c => !char.IsLetterOrDigit(c)) < MinNonAlphanumeric)
                return $"The site wants at least {MinNonAlphanumeric} characters that aren't letters or digits.";
            if (StrengthPattern is not null && !Regex.IsMatch(password, StrengthPattern))
                return "The password doesn't meet the site's password rules (passwordStrengthRegularExpression).";
            return null;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="work"/> on the site's database - a LocalDB file through DNN Manager's own instance.</summary>
    private static async Task<Result> WithSiteDatabaseAsync(string siteDirectory, DatabaseConnection database,
        Func<SqlConnection, Task<Result>> work, CancellationToken ct)
    {
        if (database.Kind == DatabaseKind.LocalDbFile)
            return await LocalDbFiles.WithDatabaseAsync(siteDirectory, database, work, ct);
        await using var conn = new SqlConnection(ConnectionStrings.ForApp(database, timeoutSeconds: 30));
        await conn.OpenAsync(ct);
        return await work(conn);
    }

    /// <summary>The DNN version of the site's files (bin\DotNetNuke.dll), e.g. "10.3.3"; null when it can't be read.</summary>
    private static string? DnnVersionOf(string siteDirectory)
    {
        try
        {
            var version = AssemblyName.GetAssemblyName(Path.Combine(siteDirectory, "bin", "DotNetNuke.dll")).Version;
            return version is null ? null : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> AppSettings(string webConfig)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(webConfig)) return settings;
        foreach (var add in XDocument.Load(webConfig).Root?.Element("appSettings")?.Elements("add") ?? [])
            if ((string?)add.Attribute("key") is { } key) settings[key] = (string?)add.Attribute("value") ?? "";
        return settings;
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(30)
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>A request to the site on this machine, with its own host name - the IIS binding routes it, whatever DNS says.</summary>
    private static HttpRequestMessage Get(DnnSiteAddress site, string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://127.0.0.1:{site.Port}{pathAndQuery}"));
        request.Headers.Host = site.Alias;
        return request;
    }

    private static Uri? Target(DnnSiteAddress site, HttpResponseMessage response) =>
        response.Headers.Location is not { } location ? null
        : location.IsAbsoluteUri ? location
        : new Uri(new Uri(site.Url), location);

    private static IReadOnlyList<string> ReadShared(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (IOException)
        {
            return [];
        }
    }

    // DNN's log lines start with the local time: "2026-10-01 14:39:20,123 [MACHINE][Thread:12][ERROR] …".
    private static DateTime? LogTime(string line) =>
        line.Length >= 23 && DateTime.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var at) ? at : null;

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool StartsWith(string text, string prefix) => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string After(string text, string prefix) => text[prefix.Length..].Trim().TrimStart(':').Trim();

    private static string Short(string text) => text.Length > 200 ? text[..200] + "…" : text;

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    /// <summary>Passwords to keep out of every message - as they are and as XML, HTML or a URL would carry them.</summary>
    private sealed class Secrets
    {
        private readonly string[] _forms;

        public Secrets(params string[] secrets) =>
            _forms = secrets.Where(s => s.Length >= 4)
                .SelectMany(s => new[] { s, SecurityElement.Escape(s) ?? s, WebUtility.HtmlEncode(s), Uri.EscapeDataString(s) })
                .Distinct()
                .OrderByDescending(s => s.Length)
                .ToArray();

        public string Hide(string text)
        {
            foreach (var form in _forms) text = text.Replace(form, "***", StringComparison.Ordinal);
            return text;
        }
    }
}
