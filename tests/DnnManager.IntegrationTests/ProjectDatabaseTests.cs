using System.IO.Compression;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Settings;
using DnnManager.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>
/// A project never destroys or quietly shares another one's database: Import gives the new project a database of its
/// own whatever its zip's web.config names, Host doesn't take over a database another IIS site uses, and a project's
/// site never replaces another folder's IIS site of the same name. SQL Server and IIS are stand-ins that record what
/// would be done.
/// </summary>
[TestClass]
public sealed class ProjectDatabaseTests
{
    private string _run = "";

    [TestInitialize]
    public void Initialize() => _run = TestEnvironment.NewRunDirectory();

    [TestCleanup]
    public void Cleanup() => TestEnvironment.DeleteDirectory(_run);

    [TestMethod]
    public async Task Import_GetsADatabaseOfItsOwn_NotTheOneItsZipNames()
    {
        // Exported from the project "shop" on this PC: its web.config names [shop] on the local container.
        var exported = Site(Path.Combine(_run, "exported"), "localhost,1433", "shop");
        var zip = Path.Combine(_run, "shop.zip");
        ZipFile.CreateFromDirectory(exported, zip);
        var backup = Path.Combine(_run, "shop.bak");
        await File.WriteAllTextAsync(backup, "a backup");
        var sql = new RecordingSql("shop");

        var (result, reporter, prompt, _) = await RunAsync(sql, new UntouchedIis(), (sp, r) => sp.GetRequiredService<ImportProjectUseCase>()
            .ExecuteAsync(new ImportProjectRequest { ProjectName = "shopcopy", ZipPath = zip, BackupFilePath = backup }, r, CancellationToken.None));

        Assert.IsTrue(result.Success, result.Error + Environment.NewLine + reporter.Text);
        CollectionAssert.AreEqual(new[] { "shopcopy" }, sql.Restored, "The backup went into another database than the new project's own.");
        Assert.IsFalse(sql.Touched("shop"), "The source project's database was changed: " + string.Join(", ", sql.Calls));
        Assert.IsFalse(prompt.Questions.Any(q => q.Contains("[shop]", StringComparison.Ordinal)), string.Join(Environment.NewLine, prompt.Questions));
        var webConfig = await File.ReadAllTextAsync(Path.Combine(_run, "projects", "shopcopy", "web.config"));
        StringAssert.Contains(webConfig, "Initial Catalog=shopcopy");
        // Signed in with a login of its own, owner of its database only - not the container's sa.
        StringAssert.Contains(webConfig, "User ID=dnn_shopcopy");
        CollectionAssert.Contains(sql.Calls, "Login dnn_shopcopy shopcopy");
    }

    [TestMethod]
    public async Task Host_DoesNotTakeOverTheDatabaseOfAnotherSite()
    {
        // "copy" is a copy of "shop"'s folder: both web.configs name [shop] - written two ways.
        var shop = Site(Path.Combine(_run, "elsewhere", "shop"), "localhost", "shop");
        Site(Path.Combine(_run, "projects", "copy"), "127.0.0.1,1433", "shop");
        var iis = new UntouchedIis();
        iis.Sites["shop"] = new IisSiteRuntime(1, "Started", "shop", "Started", [], [], shop);
        var sql = new RecordingSql("shop");

        var (result, reporter, _, _) = await RunAsync(sql, iis, (sp, r) => sp.GetRequiredService<HostExistingProjectUseCase>()
            .ExecuteAsync(new HostExistingProjectRequest { ProjectName = "copy", SetupIis = false, SetupDatabase = true }, r, CancellationToken.None));

        Assert.IsTrue(result.Success, result.Error + Environment.NewLine + reporter.Text);
        CollectionAssert.AreEqual(new[] { "copy" }, sql.Created, reporter.Text);
        StringAssert.Contains(reporter.Text, "the database of the IIS site 'shop'");
        Assert.IsFalse(sql.Touched("shop"), string.Join(", ", sql.Calls));
    }

    [TestMethod]
    public async Task Host_KeepsTheDatabaseItsWebConfigNames_WhenNoOtherSiteUsesIt()
    {
        Site(Path.Combine(_run, "projects", "blog"), "localhost,1433", "blog_dev");
        var sql = new RecordingSql("blog_dev");

        var (result, reporter, _, _) = await RunAsync(sql, new UntouchedIis(), (sp, r) => sp.GetRequiredService<HostExistingProjectUseCase>()
            .ExecuteAsync(new HostExistingProjectRequest { ProjectName = "blog", SetupIis = false, SetupDatabase = true }, r, CancellationToken.None));

        Assert.IsTrue(result.Success, result.Error + Environment.NewLine + reporter.Text);
        Assert.AreEqual(0, sql.Created.Count, reporter.Text);
        StringAssert.Contains(reporter.Text, "[blog_dev] already exists");
    }

    [TestMethod]
    public async Task Host_RefusesAnIisSiteOfTheSameNameServingAnotherFolder()
    {
        Site(Path.Combine(_run, "projects", "shop"), "localhost,1433", "shop");
        var iis = new UntouchedIis();
        iis.Sites["SHOP"] = new IisSiteRuntime(1, "Started", "shop", "Started", [], [], @"C:\work\shop");

        var (result, _, _, _) = await RunAsync(new RecordingSql(), iis, (sp, r) => sp.GetRequiredService<HostExistingProjectUseCase>()
            .ExecuteAsync(new HostExistingProjectRequest { ProjectName = "shop", SetupIis = true }, r, CancellationToken.None));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, @"IIS already has a site named 'shop', serving C:\work\shop");
        Assert.AreEqual(0, iis.Changes.Count, "IIS was changed: " + string.Join(", ", iis.Changes));
    }

    [TestMethod]
    public async Task Host_RecreatesTheSiteOfItsOwnFolder()
    {
        var folder = Site(Path.Combine(_run, "projects", "shop"), "localhost,1433", "shop");
        var iis = new UntouchedIis();
        iis.Sites["shop"] = new IisSiteRuntime(1, "Started", "shop", "Started", [], [], folder + @"\");

        var (result, reporter, _, _) = await RunAsync(new RecordingSql(), iis, (sp, r) => sp.GetRequiredService<HostExistingProjectUseCase>()
            .ExecuteAsync(new HostExistingProjectRequest { ProjectName = "shop", SetupIis = true }, r, CancellationToken.None));

        Assert.IsTrue(result.Success, result.Error + Environment.NewLine + reporter.Text);
        CollectionAssert.Contains(iis.Changes, "CreateSite shop");
    }

    [TestMethod]
    public async Task Clone_AsksBeforeReplacingADatabase_AndCopiesNothingOnNo()
    {
        var source = Site(Path.Combine(_run, "elsewhere", "shop"), "sql.example.com,1433", "shop");
        var sql = new RecordingSql("shopcopy");

        var (result, reporter, prompt, _) = await RunAsync(sql, new UntouchedIis(), (sp, r) => sp.GetRequiredService<CloneProjectUseCase>()
            .ExecuteAsync(new CloneProjectRequest
            {
                TargetProjectName = "shopcopy", SourceDirectory = source, SourceBackupServerPath = Path.Combine(_run, "shop.bak")
            }, r, CancellationToken.None));

        Assert.IsTrue(result.IsAborted, result.Error + Environment.NewLine + reporter.Text);
        Assert.IsTrue(prompt.Questions.Any(q => q.Contains("[shopcopy] already exists", StringComparison.Ordinal)), string.Join(Environment.NewLine, prompt.Questions));
        Assert.IsFalse(Directory.Exists(Path.Combine(_run, "projects", "shopcopy")), "Files were copied before the database was settled.");
        Assert.IsFalse(sql.Touched("shopcopy"), string.Join(", ", sql.Calls));
    }

    [TestMethod]
    public async Task Clone_TheSourcesDatabaseComesFromTheSourcesWebConfig()
    {
        // Files kept: the target's web.config already names the target's database - the source's is the one to copy.
        var source = Site(Path.Combine(_run, "elsewhere", "shop"), "localhost,1433", "shop");
        Site(Path.Combine(_run, "projects", "shopcopy"), "localhost,1433", "shopcopy");
        var sql = new RecordingSql("shop");

        var (result, reporter, _, _) = await RunAsync(sql, new UntouchedIis(), (sp, r) => sp.GetRequiredService<CloneProjectUseCase>()
            .ExecuteAsync(new CloneProjectRequest
            {
                TargetProjectName = "shopcopy", SourceDirectory = source, SourceBackupServerPath = Path.Combine(_run, "shop.bak"),
                CopyFiles = false, CreateIisSite = false
            }, r, CancellationToken.None));

        StringAssert.Contains(reporter.Text, "Source DB: [shop] on localhost,1433");
        // The stand-in can't back up: the run fails there - and takes the empty copy it made away again.
        Assert.IsFalse(result.Success);
        Assert.IsFalse(sql.Calls.Contains("Drop shop"), string.Join(", ", sql.Calls));
    }

    // ─── The run ──────────────────────────────────────────────────────────

    /// <summary>A DNN site's folder with a web.config naming <paramref name="database"/> on <paramref name="server"/>.</summary>
    private static string Site(string folder, string server, string database)
    {
        Directory.CreateDirectory(folder);
        var connection = $"Data Source={server};Initial Catalog={database};User ID=sa;Password=Not-A-Password-1";
        File.WriteAllText(Path.Combine(folder, "web.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <connectionStrings>
                <add name="SiteSqlServer" connectionString="{connection}" providerName="System.Data.SqlClient" />
              </connectionStrings>
              <appSettings>
                <add key="SiteSqlServer" value="{connection}" />
              </appSettings>
            </configuration>
            """);
        return folder;
    }

    private async Task<(Result Result, RecordingReporter Reporter, TestPrompt Prompt, ServiceProvider Services)> RunAsync(
        RecordingSql sql, UntouchedIis iis, Func<IServiceProvider, IProgressReporter, Task<Result>> run)
    {
        var options = new AppOptions
        {
            BaseDirectory = Path.Combine(_run, "projects"),
            HostnameSuffix = "localhost",
            SitePort = 80,
            Docker = new DockerOptions { ContainerIp = "localhost", DefaultPort = 1433, SaPassword = "Not-A-Password-1", Collation = "Latin1_General_CI_AS" }
        };
        var paths = new AppDataPaths(Path.Combine(_run, "data"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddApplication();
        services.AddInfrastructure(paths, new SettingsStore(paths));
        services.RemoveAll<IIisManager>();
        services.AddSingleton<IIisManager>(iis);
        services.RemoveAll<ISqlServerService>();
        services.AddSingleton<ISqlServerService>(sql);
        services.RemoveAll<ISqlConnectionTester>();
        services.AddSingleton<ISqlConnectionTester, AnsweringTester>();
        services.RemoveAll<IHttpConnectivityChecker>();
        services.AddSingleton<IHttpConnectivityChecker, NoHttp>();
        services.RemoveAll<IPrerequisiteChecker>();
        services.AddSingleton<IPrerequisiteChecker, NoIisFeatures>();
        var prompt = new TestPrompt();
        services.AddSingleton<IUserPrompt>(prompt);
        var provider = services.BuildServiceProvider();
        var reporter = new RecordingReporter();
        Result result;
        using (var scope = provider.CreateScope())
            result = await run(scope.ServiceProvider, reporter);
        return (result, reporter, prompt, provider);
    }

    /// <summary>The local SQL Server: the databases there before, and what was done to which.</summary>
    private sealed class RecordingSql(params string[] existing) : ISqlServerService
    {
        private readonly HashSet<string> _existing = new(existing, StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];
        public List<string> Created { get; } = [];
        public List<string> Restored { get; } = [];

        public bool Touched(string database) => Calls.Any(c => c.EndsWith(" " + database, StringComparison.OrdinalIgnoreCase) &&
                                                               !c.StartsWith("Exists ", StringComparison.Ordinal));

        private Result Did(string call)
        {
            Calls.Add(call);
            return Result.Ok();
        }

        public Task<Result<bool>> DatabaseExistsAsync(string database, CancellationToken ct)
        {
            Calls.Add($"Exists {database}");
            return Task.FromResult(Result<bool>.Ok(_existing.Contains(database)));
        }

        public Task<Result> CreateDatabaseAsync(DatabaseConfig db, CancellationToken ct)
        {
            Created.Add(db.DatabaseName);
            _existing.Add(db.DatabaseName);
            return Task.FromResult(Did($"Create {db.DatabaseName}"));
        }

        public Task<Result> DropDatabaseAsync(string database, CancellationToken ct)
        {
            _existing.Remove(database);
            return Task.FromResult(Did($"Drop {database}"));
        }

        public Task<Result<string>> BackupDatabaseLocalAsync(string database, string backupFileName, CancellationToken ct) =>
            Task.FromResult(Result<string>.Fail("Not in these tests."));

        public Task<Result> RestoreDatabaseLocalAsync(DatabaseConfig db, string backupFilePath, CancellationToken ct)
        {
            Restored.Add(db.DatabaseName);
            _existing.Add(db.DatabaseName);
            return Task.FromResult(Did($"Restore {db.DatabaseName}"));
        }

        public Task<Result> RemapPortalAliasesAsync(string database, string hostnameSuffix, string newAlias, CancellationToken ct) =>
            Task.FromResult(Did($"Alias {database}"));

        public Task<Result<int>> DisableSslAsync(string database, CancellationToken ct)
        {
            Did($"Ssl {database}");
            return Task.FromResult(Result<int>.Ok(0));
        }

        public Task<Result> GrantSiteLoginAsync(string database, string login, string password, CancellationToken ct) =>
            Task.FromResult(Did($"Login {login} {database}"));

        public Task<Result> DropLoginAsync(string login, CancellationToken ct) => Task.FromResult(Did($"DropLogin {login}"));
    }

    private sealed class AnsweringTester : ISqlConnectionTester
    {
        public Task<Result<string>> TestAsync(SiteSqlConnection connection, CancellationToken ct, int timeoutSeconds = 15) =>
            Task.FromResult(Result<string>.Ok("SQL Server (a stand-in)"));

        public Task<Result<IReadOnlyList<string>>> ListDatabasesAsync(SiteSqlConnection server, CancellationToken ct, int timeoutSeconds = 15) =>
            Task.FromResult(Result<IReadOnlyList<string>>.Ok([]));
    }

    private sealed class NoHttp : IHttpConnectivityChecker
    {
        public Task<Result<int>> CheckAsync(string url, int timeoutSeconds, CancellationToken ct) => Task.FromResult(Result<int>.Fail("not in tests"));
    }
}
