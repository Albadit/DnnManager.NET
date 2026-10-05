using System.Reflection;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Dnn;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Sql;
using DnnManager.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>
/// New project with automatic setup, end to end, on a clean DNN install package: DNN Manager's own SetupProjectUseCase
/// - with IIS Express playing IIS - and then what a user would see: the home page instead of DNN's installation wizard,
/// the host account signing in, the portal and its alias, DNN's tables, a restart that doesn't install again, clean
/// logs, and no password in any message. Once with each kind of database - and once with manual setup, which leaves
/// DNN's own installation wizard for the first visit.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class AutomaticInstallTests
{
    private static string _run = "";
    private static string? _package;
    private static string? _localDbName, _localDb;
    private static string? _containerName, _container, _saPassword;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        if (!IisExpressSites.Installed) return;
        _run = TestEnvironment.NewRunDirectory();
        _package = await TestEnvironment.DnnPackageAsync();
        if (TestEnvironment.SqlLocalDb() is not null)
        {
            _localDbName = "dnnit_" + Path.GetFileName(_run);
            _localDb = TestEnvironment.CreateLocalDbInstance(_localDbName);
        }
        if (TestEnvironment.DockerAvailable())
        {
            _containerName = "dnnit-mssql-" + Path.GetFileName(_run);
            _saPassword = TestEnvironment.Password(24, "-_");
            var port = Enumerable.Range(14330, 60).First(p => !IisExpressSites.PortOpen(p));
            _container = await TestEnvironment.StartSqlContainerAsync(_containerName, port, _saPassword);
        }
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        if (_containerName is not null) TestEnvironment.RemoveContainer(_containerName);
        if (_localDbName is not null) TestEnvironment.DeleteLocalDbInstance(_localDbName);
        if (_run.Length > 0) TestEnvironment.DeleteDirectory(_run);
    }

    [TestMethod]
    public async Task WindowsAuthentication_ShowsTheSiteInsteadOfTheWizard()
    {
        if (_localDb is null) Assert.Inconclusive("SQL Server Express LocalDB isn't installed.");
        await InstallAndVerifyAsync("dnnit_win", new DatabaseConnection(DatabaseKind.SqlServer, _localDb!, "dnnit_win", SqlAuthentication.Windows));
    }

    [TestMethod]
    public async Task LocalSqlContainer_ShowsTheSiteInsteadOfTheWizard()
    {
        if (_container is null) Assert.Inconclusive("Docker with Linux containers isn't available for a SQL Server container.");
        // No database chosen: DNN Manager's default - a database named like the project on the local container, as sa.
        await InstallAndVerifyAsync("dnnit_box", null);
    }

    [TestMethod]
    public async Task SqlAuthentication_OwnLoginOnAnEmptyDatabase()
    {
        if (_container is null) Assert.Inconclusive("Docker with Linux containers isn't available for a SQL Server container.");
        // A login of its own (not sa) owning an empty database made beforehand, with a password connection strings and
        // T-SQL have to quote.
        var password = TestEnvironment.Password(24, ";={'\"");
        await using (var conn = new SqlConnection(Sa("master")))
        {
            await conn.OpenAsync();
            await Exec(conn, "CREATE DATABASE [dnnit_sql]");
            using var login = new SqlCommand("DECLARE @sql nvarchar(max) = N'CREATE LOGIN [dnnit_user] WITH PASSWORD = ' + QUOTENAME(@p, '''') + N', CHECK_POLICY = OFF'; EXEC (@sql);", conn);
            login.Parameters.AddWithValue("@p", password);
            await login.ExecuteNonQueryAsync();
            conn.ChangeDatabase("dnnit_sql");
            await Exec(conn, "CREATE USER [dnnit_user] FOR LOGIN [dnnit_user]; ALTER ROLE db_owner ADD MEMBER [dnnit_user];");
        }
        await InstallAndVerifyAsync("dnnit_sql",
            new DatabaseConnection(DatabaseKind.SqlServer, _container!, "dnnit_sql", SqlAuthentication.Sql, "dnnit_user", password));
    }

    [TestMethod]
    public async Task LocalDbFile_ShowsTheSiteInsteadOfTheWizard()
    {
        if (_localDb is null) Assert.Inconclusive("SQL Server Express LocalDB isn't installed.");
        await InstallAndVerifyAsync("dnnit_file",
            new DatabaseConnection(DatabaseKind.LocalDbFile, _localDb!, DatabaseConnection.LocalDbFileName, SqlAuthentication.Windows));
    }

    [TestMethod]
    public async Task ManualSetup_LeavesDnnsInstallationWizard()
    {
        if (!IisExpressSites.Installed) Assert.Inconclusive("IIS Express isn't installed.");
        if (_localDb is null && _container is null) Assert.Inconclusive("Neither LocalDB nor Docker is available for a database.");
        const string name = "dnnit_manual";
        var projects = Path.Combine(_run, "p");
        using var iis = new IisExpressSites(Path.Combine(_run, "iis"));
        await using var services = Services(projects, Path.Combine(_run, "data_" + name), iis);
        var database = _localDb is not null
            ? new DatabaseConnection(DatabaseKind.SqlServer, _localDb, name, SqlAuthentication.Windows)
            : services.GetRequiredService<LocalSqlContainer>().Connection(name);
        var siteDirectory = Path.Combine(projects, name);
        var port = TestEnvironment.FreePort();
        var reporter = new RecordingReporter();
        try
        {
            Result result;
            using (var scope = services.CreateScope())
                result = await scope.ServiceProvider.GetRequiredService<SetupProjectUseCase>().ExecuteAsync(new SetupProjectRequest
                {
                    ProjectName = name,
                    ReleaseApiUrl = "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases",
                    Version = "v" + TestEnvironment.DnnVersion,
                    HostName = "localhost",
                    Port = port,
                    InstallMode = DnnInstallMode.Manual,
                    Database = database
                }, reporter, CancellationToken.None);
            TestContext.WriteLine(reporter.Text);
            Assert.IsTrue(result.Success, $"{result.Error}{Environment.NewLine}{reporter.Text}");
            StringAssert.Contains(reporter.Text, "to complete the DNN Installation Wizard");

            // DNN isn't installed: its installer is all there, no template with a password, the database is empty.
            foreach (var file in new[] { @"Install\InstallWizard.aspx", @"Install\Install.aspx", @"Install\DotNetNuke.install.config.resources" })
                Assert.IsTrue(File.Exists(Path.Combine(siteDirectory, file)), $"{file} is gone.");
            Assert.IsFalse(File.Exists(Path.Combine(siteDirectory, @"Install\DotNetNuke.install.config")), "An install template was written.");
            Assert.IsFalse(File.ReadAllText(Path.Combine(siteDirectory, "web.config")).Contains("\"InstallVersion\"", StringComparison.Ordinal),
                "web.config says DNN is installed.");
            await WithDatabaseAsync(siteDirectory, database, async conn =>
            {
                Assert.AreEqual(0, await Scalar<int>(conn, "SELECT COUNT(*) FROM sys.tables"), "The database isn't empty.");
                return true;
            });

            // The first visit goes to DNN's wizard, which shows (after a redirect to itself, once DNN wrote its machine key).
            var browser = new DnnBrowser(new Uri($"http://localhost:{port}/"));
            var home = await browser.HomeAsync();
            Assert.IsTrue(home.Uri.AbsolutePath.StartsWith("/Install/InstallWizard.aspx", StringComparison.OrdinalIgnoreCase),
                "The first visit doesn't go to the wizard: " + string.Join(" | ", home.Hops));
            var wizard = await browser.PageAsync(home.Uri.PathAndQuery.TrimStart('/'));
            Assert.AreEqual(200, wizard.Status, string.Join(" | ", wizard.Hops));
            Assert.IsTrue(wizard.Html.Contains("InstallWizard", StringComparison.OrdinalIgnoreCase), "That isn't DNN's installation wizard.");

            Assert.AreEqual(DnnInstallMode.Manual, services.GetRequiredService<IProjectRecords>().Find(name)?.InstallMode);
        }
        finally
        {
            iis.StopSite(name);
            await services.GetRequiredService<IDatabaseProvisioner>().DropDatabaseAsync(database, CancellationToken.None);
        }
    }

    // ─── The run and what it is checked for ───────────────────────────────

    private async Task InstallAndVerifyAsync(string name, DatabaseConnection? chosen)
    {
        if (!IisExpressSites.Installed) Assert.Inconclusive("IIS Express isn't installed.");
        var projects = Path.Combine(_run, "p");
        var data = Path.Combine(_run, "data_" + name);
        using var iis = new IisExpressSites(Path.Combine(_run, "iis"));
        await using var services = Services(projects, data, iis);
        var installer = services.GetRequiredService<IDnnInstaller>();
        var port = TestEnvironment.FreePort();
        var account = new DnnAccount("dnnhost", TestEnvironment.Password(), "host@dnnit.example",
            $"DNN IT {name} & Co", "en-US", "Default Website");
        var siteDirectory = Path.Combine(projects, name);
        var database = chosen ?? services.GetRequiredService<LocalSqlContainer>().Connection(name);
        var reporter = new RecordingReporter();
        try
        {
            Result result;
            using (var scope = services.CreateScope())
                result = await scope.ServiceProvider.GetRequiredService<SetupProjectUseCase>().ExecuteAsync(new SetupProjectRequest
                {
                    ProjectName = name,
                    ReleaseApiUrl = "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases",
                    Version = "v" + TestEnvironment.DnnVersion,
                    HostName = "localhost",
                    Port = port,
                    InstallMode = DnnInstallMode.Automatic,
                    Account = account,
                    Database = chosen
                }, reporter, CancellationToken.None);
            TestContext.WriteLine(reporter.Text);
            Assert.IsTrue(result.Success, $"{result.Error}{Environment.NewLine}{reporter.Text}");

            // The Output panel: the steps as named, and no password - not the host's, not the database's.
            foreach (var step in new[] { "Testing database connection", "Creating project directory", "Creating IIS application pool and website",
                         "Creating database", "Configuring DNN", "Running DNN installation", "Creating portal", "Creating host account",
                         "Starting website", "DNN installation completed" })
                Assert.IsTrue(reporter.Text.Contains(step, StringComparison.Ordinal), $"Output has no '{step}'.");
            Assert.IsFalse(reporter.Text.Contains(account.Password, StringComparison.Ordinal), "The host password is in the Output.");
            if (database.Password.Length > 0)
                Assert.IsFalse(reporter.Text.Contains(database.Password, StringComparison.Ordinal), "The database password is in the Output.");
            Assert.IsFalse(reporter.Lines.Any(l => l.StartsWith("WARN DNN", StringComparison.Ordinal) || l.StartsWith("WARN A DNN", StringComparison.Ordinal)),
                "DNN logged errors: " + string.Join(" | ", reporter.Lines.Where(l => l.StartsWith("WARN", StringComparison.Ordinal))));
            if (database.Kind == DatabaseKind.LocalDbFile)
                Assert.IsTrue(iis.ProfileEnabled(name), "The app pool's user profile (needed by LocalDB under IIS) wasn't loaded.");

            // Nothing left with a password in plain text, nothing left that installs DNN again.
            foreach (var file in new[] { @"Install\DotNetNuke.install.config", @"Install\DotNetNuke.install.config.resources",
                         @"Install\Install.aspx", @"Install\InstallWizard.aspx", @"Install\UpgradeWizard.aspx", "installBlocker.lock" })
                Assert.IsFalse(File.Exists(Path.Combine(siteDirectory, file)), $"{file} is still there.");
            Assert.IsFalse(Directory.Exists(Path.Combine(siteDirectory, "Config")) &&
                           Directory.EnumerateDirectories(Path.Combine(siteDirectory, "Config"), "Backup_*").Any(), "DNN's web.config backups are still there.");
            var webConfig = File.ReadAllText(Path.Combine(siteDirectory, "web.config"));
            StringAssert.Contains(webConfig, "\"InstallVersion\"");

            // What a visitor sees: the site, not the wizard.
            var browser = new DnnBrowser(new Uri($"http://localhost:{port}/"));
            var home = await browser.HomeAsync();
            Assert.AreEqual(200, home.Status, string.Join(" | ", home.Hops));
            Assert.IsFalse(home.Uri.AbsolutePath.StartsWith("/Install", StringComparison.OrdinalIgnoreCase), "The site sends visitors to the installer.");
            Assert.IsFalse(home.Html.Contains("InstallWizard", StringComparison.OrdinalIgnoreCase), "The home page shows the installation wizard.");
            Assert.AreEqual(account.WebsiteName, DnnBrowser.LogoTitle(home.Html));

            // The host account signs in - without being made to change its password - and only with its password.
            var signIn = await browser.SignInAsync(account.UserName, account.Password);
            Assert.IsTrue(signIn.SignedIn, signIn.Detail);
            var wrong = await browser.SignInAsync(account.UserName, account.Password + "x");
            Assert.IsFalse(wrong.AuthCookieSet, "A wrong password signed in: " + wrong.Detail);

            // The database: one portal with its alias, the host, DNN's tables at the files' version.
            if (database.Kind == DatabaseKind.LocalDbFile) iis.StopSite(name);
            await WithDatabaseAsync(siteDirectory, database, async conn =>
            {
                Assert.AreEqual(1, await Scalar<int>(conn, "SELECT COUNT(*) FROM dbo.Portals"));
                Assert.AreEqual(1, await Scalar<int>(conn, $"SELECT COUNT(*) FROM dbo.PortalAlias WHERE PortalID = 0 AND IsPrimary = 1 AND HTTPAlias = 'localhost:{port}'"));
                Assert.AreEqual(0, await Scalar<int>(conn, "SELECT COUNT(*) FROM dbo.Tabs WHERE PortalID = 0 AND IsSecure = 1"), "Pages are marked secure (the wizard clears that).");
                using (var host = new SqlCommand("SELECT IsSuperUser, UpdatePassword, Email FROM dbo.Users WHERE Username = @u", conn))
                {
                    host.Parameters.AddWithValue("@u", account.UserName);
                    using var row = await host.ExecuteReaderAsync();
                    Assert.IsTrue(await row.ReadAsync(), "No host account in the database.");
                    Assert.IsTrue(row.GetBoolean(0), "The host isn't a superuser.");
                    Assert.IsFalse(row.GetBoolean(1), "The host has to change its password at the first sign-in.");
                    Assert.AreEqual(account.Email, row.GetString(2));
                }
                foreach (var table in new[] { "Portals", "PortalAlias", "Tabs", "Modules", "TabModules", "Users", "UserPortals", "Roles", "UserRoles",
                             "aspnet_Users", "aspnet_Membership", "HostSettings", "PortalSettings", "Packages", "DesktopModules", "Version", "Schedule" })
                    Assert.AreEqual(1, await Scalar<int>(conn, $"SELECT COUNT(*) FROM sys.tables WHERE name = '{table}'"), $"Table {table} is missing.");
                var dll = AssemblyName.GetAssemblyName(Path.Combine(siteDirectory, "bin", "DotNetNuke.dll")).Version!;
                Assert.AreEqual($"{dll.Major}.{dll.Minor}.{dll.Build}",
                    await Scalar<string>(conn, "SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build) FROM dbo.[Version] ORDER BY VersionId DESC"));

                // DNN Manager hashes passwords exactly as DNN's membership provider does.
                using var stored = new SqlCommand("SELECT m.Password, m.PasswordSalt FROM dbo.aspnet_Membership m JOIN dbo.aspnet_Users u ON u.UserId = m.UserId WHERE u.UserName = @u", conn);
                stored.Parameters.AddWithValue("@u", account.UserName);
                using var membership = await stored.ExecuteReaderAsync();
                Assert.IsTrue(await membership.ReadAsync());
                Assert.AreEqual(membership.GetString(0), MembershipPasswords.Hash(account.Password, membership.GetString(1), "SHA256"));
                return true;
            });
            if (database.Kind == DatabaseKind.LocalDbFile) iis.StartSite(name);

            // A restart doesn't install DNN again.
            iis.StopSite(name);
            iis.StartSite(name);
            var again = await browser.HomeAsync();
            Assert.AreEqual(200, again.Status, string.Join(" | ", again.Hops));
            Assert.AreEqual(account.WebsiteName, DnnBrowser.LogoTitle(again.Html));

            // Change host password: the new one signs in, the old one no longer.
            var newPassword = TestEnvironment.Password();
            if (database.Kind == DatabaseKind.LocalDbFile) iis.StopSite(name);
            var changed = await installer.ChangeHostPasswordAsync(siteDirectory, database, account.UserName, newPassword, CancellationToken.None);
            Assert.IsTrue(changed.Success, changed.Error);
            if (database.Kind == DatabaseKind.LocalDbFile) iis.StartSite(name);
            else iis.RecycleAppPool(name);
            Assert.IsTrue((await browser.SignInAsync(account.UserName, newPassword)).SignedIn, "The new password doesn't sign in.");
            Assert.IsFalse((await browser.SignInAsync(account.UserName, account.Password)).AuthCookieSet, "The old password still signs in.");

            var record = services.GetRequiredService<IProjectRecords>().Find(name);
            Assert.AreEqual(DnnInstallMode.Automatic, record?.InstallMode);
            Assert.AreEqual(account.UserName, record?.HostUserName);
        }
        finally
        {
            iis.StopSite(name);
            if (database.Kind == DatabaseKind.LocalDbFile)
                await LocalDbFiles.DetachAsync(database.Server, LocalDbFiles.PathOf(siteDirectory, database), CancellationToken.None);
            else
                await services.GetRequiredService<IDatabaseProvisioner>().DropDatabaseAsync(database, CancellationToken.None);
        }
    }

    // ─── DNN Manager's services, with IIS Express for IIS ─────────────────

    private static ServiceProvider Services(string projects, string data, IisExpressSites iis)
    {
        var options = new AppOptions
        {
            BaseDirectory = projects,
            HostnameSuffix = "localhost",
            SitePort = 80,
            Docker = new DockerOptions
            {
                ContainerIp = "127.0.0.1",
                DefaultPort = _container is null ? 1433 : int.Parse(_container.Split(',')[1]),
                SaPassword = _saPassword ?? "",
                Collation = "Latin1_General_CI_AS"
            }
        };
        var paths = new AppDataPaths(data);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddApplication();
        services.AddInfrastructure(paths, new SettingsStore(paths));
        services.RemoveAll<IIisManager>();
        services.AddSingleton<IIisManager>(iis);
        services.RemoveAll<IPrerequisiteChecker>();
        services.AddSingleton<IPrerequisiteChecker, NoIisFeatures>();
        var packages = new TestDnnPackages(_package!, TestEnvironment.DnnVersion);
        services.RemoveAll<IDnnReleaseService>();
        services.RemoveAll<IDnnPackageInstaller>();
        services.AddSingleton<IDnnReleaseService>(packages);
        services.AddSingleton<IDnnPackageInstaller>(packages);
        services.AddSingleton<IUserPrompt, TestPrompt>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> WithDatabaseAsync<T>(string siteDirectory, DatabaseConnection database, Func<SqlConnection, Task<T>> work)
    {
        if (database.Kind == DatabaseKind.LocalDbFile) return await LocalDbFiles.WithDatabaseAsync(siteDirectory, database, work, CancellationToken.None);
        await using var conn = new SqlConnection(ConnectionStrings.ForApp(database));
        await conn.OpenAsync();
        return await work(conn);
    }

    private static string Sa(string database) => new SqlConnectionStringBuilder
    {
        DataSource = _container, InitialCatalog = database, UserID = "sa", Password = _saPassword,
        Encrypt = true, TrustServerCertificate = true, Pooling = false
    }.ConnectionString;

    private static async Task Exec(SqlConnection conn, string sql)
    {
        using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(SqlConnection conn, string sql)
    {
        using var cmd = new SqlCommand(sql, conn);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }
}