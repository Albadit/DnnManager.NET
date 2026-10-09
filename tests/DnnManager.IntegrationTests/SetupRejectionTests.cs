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
/// New project with automatic setup refuses what DNN's installer would fail on - before it creates anything: no project
/// folder, no IIS website, no database, no record. Needs neither IIS nor SQL Server: IIS is a stand-in that records any
/// change, and the local SQL container points at a port nobody listens on.
/// </summary>
[TestClass]
public sealed class SetupRejectionTests
{
    private const string Name = "dnnit_refused";

    private string _run = "";

    [TestInitialize]
    public void Initialize() => _run = TestEnvironment.NewRunDirectory();

    [TestCleanup]
    public void Cleanup() => TestEnvironment.DeleteDirectory(_run);

    [TestMethod]
    public async Task HostPasswordTooShort_NothingIsCreated()
    {
        // DNN finishes "successfully" without a host account for a password this short.
        var outcome = await SetUpAsync(Path.Combine(_run, "p"), Account() with { Password = "abc" });
        StringAssert.Contains(outcome.Result.Error!, "at least 7");
        outcome.AssertNothingCreated();
    }

    [TestMethod]
    public async Task HostPasswordDnnsLoginFormRefuses_NothingIsCreated()
    {
        var outcome = await SetUpAsync(Path.Combine(_run, "p"), Account() with { Password = "Valid<Pass1" });
        StringAssert.Contains(outcome.Result.Error!, "can't contain <");
        outcome.AssertNothingCreated();
    }

    [TestMethod]
    public async Task AutomaticWithoutAccount_NothingIsCreated()
    {
        var outcome = await SetUpAsync(Path.Combine(_run, "p"), null);
        StringAssert.Contains(outcome.Result.Error!, "host account");
        outcome.AssertNothingCreated();
    }

    [TestMethod]
    public async Task ProjectsFolderTooDeep_NothingIsCreated()
    {
        // DNN's packages fail to install into a site path much longer than 100 characters (Windows' 260 for their files).
        var outcome = await SetUpAsync(Path.Combine(_run, new string('d', 100)), Account());
        StringAssert.Contains(outcome.Result.Error!, "too deep");
        outcome.AssertNothingCreated();
    }

    [TestMethod]
    public async Task DatabaseUnreachable_NothingIsCreated()
    {
        // Nothing listens on port 1 of this machine.
        var database = new DatabaseConnection(DatabaseKind.SqlServer, "127.0.0.1,1", "dnnit_none", SqlAuthentication.Sql, "dnnit_user", "Never-Sent-1");
        var outcome = await SetUpAsync(Path.Combine(_run, "p"), Account(), database);
        StringAssert.Contains(outcome.Result.Error!, "The database isn't ready");
        StringAssert.Contains(outcome.Reporter.Text, "Testing database connection");
        Assert.IsFalse(outcome.Reporter.Text.Contains("Creating project directory", StringComparison.Ordinal), outcome.Reporter.Text);
        Assert.IsFalse(outcome.Reporter.Text.Contains("Never-Sent-1", StringComparison.Ordinal), "The database password is in the Output.");
        Assert.IsFalse(outcome.Result.Error!.Contains("Never-Sent-1", StringComparison.Ordinal), "The database password is in the error.");
        outcome.AssertNothingCreated();
    }

    [TestMethod]
    public async Task HostNameOfAnotherSite_NothingIsCreated()
    {
        // IIS would take the second binding and then not start the new site - and DNN's installer would talk to the shop.
        var outcome = await SetUpAsync(Path.Combine(_run, "p"), Account(), iis: iis =>
            iis.Sites["shop"] = Site("shop", new IisBinding("http", "*", 8199, "LOCALHOST", false)));
        StringAssert.Contains(outcome.Result.Error!, "already the address of the IIS site 'shop'");
        Assert.IsFalse(outcome.Reporter.Text.Contains("Downloading DNN", StringComparison.Ordinal), outcome.Reporter.Text);
        outcome.AssertNothingCreated();
    }

    [TestMethod]
    public async Task IisSiteOfTheSameNameServingAnotherFolder_NothingIsCreated()
    {
        // Hosted from C:\work, or made in IIS Manager: making the new project's site would remove it.
        var outcome = await SetUpAsync(Path.Combine(_run, "p"), Account(), iis: iis =>
            iis.Sites[Name] = new IisSiteRuntime(1, "Started", Name, "Started", [], [], $@"C:\work\{Name}"));
        StringAssert.Contains(outcome.Result.Error!, $"IIS already has a site named '{Name}'");
        Assert.IsFalse(outcome.Reporter.Text.Contains("Downloading DNN", StringComparison.Ordinal), outcome.Reporter.Text);
        outcome.AssertNothingCreated();
    }

    [TestMethod]
    public void HostNames_TheSiteAlreadyAnsweringIt()
    {
        var sites = new Dictionary<string, IisSiteRuntime>
        {
            ["shop"] = Site("shop", new IisBinding("http", "*", 80, "shop.dnndev.me", false), new IisBinding("https", "*", 443, "secure.dnndev.me", true)),
            ["blog"] = Site("blog", new IisBinding("http", "*", 8080, "blog.dnndev.me", false), new IisBinding("http", "*", 80, "", false)),
        };
        Assert.AreEqual("shop", IisHostNames.SiteUsing(sites, "Shop.DnnDev.Me.", 80), "Case and a trailing dot don't make it another name.");
        Assert.IsNull(IisHostNames.SiteUsing(sites, "shop.dnndev.me", 8080), "Another port is another address.");
        Assert.AreEqual("blog", IisHostNames.SiteUsing(sites, "blog.dnndev.me", 8080));
        Assert.IsNull(IisHostNames.SiteUsing(sites, "new.dnndev.me", 80), "A binding for any name doesn't take this one: IIS prefers the named one.");
        Assert.IsNull(IisHostNames.SiteUsing(sites, "secure.dnndev.me", 80), "An https binding isn't the http address.");
        Assert.IsNull(IisHostNames.SiteUsing(sites, "shop.dnndev.me", 80, replacing: "SHOP"), "The site a new one of the same name replaces.");
    }

    private static IisSiteRuntime Site(string name, params IisBinding[] bindings) => new(1, "Started", name, "Started", [], bindings, $@"C:\DNN\{name}");

    // ─── The run ──────────────────────────────────────────────────────────

    private sealed record Outcome(Result Result, RecordingReporter Reporter, string SiteDirectory, UntouchedIis Iis, ProjectRecord? Record)
    {
        public void AssertNothingCreated()
        {
            Assert.IsFalse(Result.Success, "The setup went ahead." + Environment.NewLine + Reporter.Text);
            Assert.IsFalse(Directory.Exists(SiteDirectory), "The project's folder was created.");
            Assert.AreEqual(0, Iis.Changes.Count, "IIS was changed: " + string.Join(", ", Iis.Changes));
            Assert.IsNull(Record, "A project record was saved.");
        }
    }

    private static DnnAccount Account() => new("dnnhost", "Valid-Pass-1", "host@dnnit.example", "Refused", "en-US", "Default Website");

    private async Task<Outcome> SetUpAsync(string projects, DnnAccount? account, DatabaseConnection? database = null, Action<UntouchedIis>? iis = null)
    {
        var stand = new UntouchedIis();
        iis?.Invoke(stand);
        await using var services = Services(projects, Path.Combine(_run, "data"), stand);
        var reporter = new RecordingReporter();
        Result result;
        using (var scope = services.CreateScope())
            result = await scope.ServiceProvider.GetRequiredService<SetupProjectUseCase>().ExecuteAsync(new SetupProjectRequest
            {
                ProjectName = Name,
                ReleaseApiUrl = "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases",
                Version = "v" + TestEnvironment.DnnVersion,
                HostName = "localhost",
                Port = 8199,
                InstallMode = DnnInstallMode.Automatic,
                Account = account,
                Database = database
            }, reporter, CancellationToken.None);
        return new Outcome(result, reporter, Path.Combine(projects, Name), stand, services.GetRequiredService<IProjectRecords>().Find(Name));
    }

    private static ServiceProvider Services(string projects, string data, UntouchedIis iis)
    {
        var options = new AppOptions
        {
            BaseDirectory = projects,
            HostnameSuffix = "localhost",
            SitePort = 80,
            Docker = new DockerOptions { ContainerIp = "127.0.0.1", DefaultPort = 1, SaPassword = "Not-A-Password-1", Collation = "Latin1_General_CI_AS" }
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
        // A release that is never downloaded: every refusal comes before the download.
        var packages = new TestDnnPackages(Path.Combine(projects, "never-downloaded.zip"), TestEnvironment.DnnVersion);
        services.RemoveAll<IDnnReleaseService>();
        services.RemoveAll<IDnnPackageInstaller>();
        services.AddSingleton<IDnnReleaseService>(packages);
        services.AddSingleton<IDnnPackageInstaller>(packages);
        services.AddSingleton<IUserPrompt, TestPrompt>();
        return services.BuildServiceProvider();
    }
}
