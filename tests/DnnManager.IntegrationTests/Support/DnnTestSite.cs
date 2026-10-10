using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests.Support;

/// <summary>
/// A real DNN site for a test: DNN Manager's own services, IIS Express playing IIS, the given DNN releases served from the
/// cache, and a database on the run's SQL Server container - installed by DNN Manager's New project (automatic setup).
/// </summary>
public sealed class DnnTestSite : IAsyncDisposable
{
    public const string ReleasesApi = "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases";

    private DnnTestSite(string name, string directory, int port, DnnAccount host, IisExpressSites iis, ServiceProvider services)
    {
        Name = name; Directory = directory; Port = port; Host = host; Iis = iis; Services = services;
    }

    public string Name { get; }
    public string Directory { get; }
    public int Port { get; }
    public DnnAccount Host { get; }
    public IisExpressSites Iis { get; }
    public ServiceProvider Services { get; }

    public Uri Url => new($"http://localhost:{Port}/");

    public DatabaseConnection Database => Services.GetRequiredService<LocalSqlContainer>().Connection(Name);

    /// <summary>
    /// DNN <paramref name="version"/> installed as <paramref name="name"/> - with <paramref name="releases"/> (every version
    /// the test upgrades to too) known to it.
    /// </summary>
    public static async Task<DnnTestSite> InstallAsync(string run, string name, string version, string containerName, string container, string saPassword,
        RecordingReporter reporter, params string[] releases)
    {
        var projects = Path.Combine(run, "p");
        var data = Path.Combine(run, "data_" + name);
        var iis = new IisExpressSites(Path.Combine(run, "iis"));
        var options = new AppOptions
        {
            BaseDirectory = projects,
            HostnameSuffix = "localhost",
            SitePort = 80,
            Docker = new DockerOptions
            {
                // The test's own container: DNN Manager runs sqlcmd in it by name (the site's login) - not the user's dnn-sqlserver.
                ContainerName = containerName, ContainerIp = "127.0.0.1", DefaultPort = int.Parse(container.Split(',')[1]), SaPassword = saPassword,
                Collation = "Latin1_General_CI_AS"
            }
        };
        var paths = new AppDataPaths(data);
        var cached = await DnnPackages.ReleasesAsync(data, [version, .. releases]);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(Options.Create(options));
        collection.AddApplication();
        collection.AddInfrastructure(paths, new SettingsStore(paths));
        collection.RemoveAll<IIisManager>();
        collection.AddSingleton<IIisManager>(iis);
        collection.RemoveAll<IPrerequisiteChecker>();
        collection.AddSingleton<IPrerequisiteChecker, NoIisFeatures>();
        collection.RemoveAll<IDnnReleaseService>();
        collection.RemoveAll<IDnnPackageInstaller>();
        collection.AddSingleton<IDnnReleaseService>(cached);
        collection.AddSingleton<IDnnPackageInstaller>(cached);
        collection.AddSingleton<IUserPrompt, TestPrompt>();
        var services = collection.BuildServiceProvider();

        var port = TestEnvironment.FreePort();
        var host = new DnnAccount("dnnhost", TestEnvironment.Password(), "host@dnnit.example", $"Upgrade test {name}", "en-US", "Default Website");
        var site = new DnnTestSite(name, Path.Combine(projects, name), port, host, iis, services);
        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SetupProjectUseCase>().ExecuteAsync(new SetupProjectRequest
        {
            ProjectName = name, ReleaseApiUrl = ReleasesApi, Version = "v" + version, HostName = "localhost", Port = port,
            InstallMode = DnnInstallMode.Automatic, Account = host
        }, reporter, CancellationToken.None);
        // DNN is installed, but its first page - compiled by ASP.NET on IIS Express - took longer than New project waits:
        // a slow first compile on a busy PC (seen now and then), not a failed install. Restarted, it gets another try.
        if (!result.Success && result.Error?.Contains("didn't answer within", StringComparison.Ordinal) == true)
        {
            reporter.Warn($"Test site: {result.Error} - restarting it and trying its home page again.");
            iis.StopSite(name);
            iis.StartSite(name);
            try
            {
                if ((await new DnnBrowser(site.Url).HomeAsync()).Status == 200) result = Result.Ok();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* still failed */ }
        }
        if (!result.Success)
        {
            await site.DisposeAsync();
            throw new InvalidOperationException($"DNN {version} didn't install: {result.Error}{Environment.NewLine}{reporter.Text}");
        }
        // From here on, stopped as IIS stops a site: its worker process ends in its own time - what DNN's lock needs handled.
        iis.StopsLikeIis = true;
        return site;
    }

    public async ValueTask DisposeAsync()
    {
        Iis.Dispose();
        try { await Services.GetRequiredService<IDatabaseProvisioner>().DropDatabaseAsync(Database, CancellationToken.None); }
        catch { /* the container goes with the run */ }
        await Services.DisposeAsync();
    }
}
