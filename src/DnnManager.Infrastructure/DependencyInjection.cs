using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Docker;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Github;
using DnnManager.Infrastructure.Iis;
using DnnManager.Infrastructure.Monitoring;
using DnnManager.Infrastructure.Prereq;
using DnnManager.Infrastructure.Processes;
using DnnManager.Infrastructure.Projects;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Sql;
using DnnManager.Infrastructure.Startup;
using DnnManager.Infrastructure.WebConfigs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <param name="settings">The settings store Program loaded the settings with; its folder holds the user's files.</param>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, AppDataPaths paths, SettingsStore settings)
    {
        services.AddSingleton(paths);
        services.AddSingleton(new Data.AppDatabase(paths));
        services.AddSingleton(settings);
        services.AddSingleton<DailyLogFile>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<IProjectRepository, FileSystemProjectRepository>();
        services.AddSingleton<IProjectRecords, ProjectRecords>();
        services.AddSingleton<IKeepWarmRecords, KeepWarmRecords>();
        services.AddSingleton<IIisManager, IisManager>();
        services.AddSingleton<ISqlServerService, SqlServerService>();
        services.AddSingleton<IDockerComposeService, DockerComposeService>();
        services.AddSingleton<IPrerequisiteChecker, WindowsPrerequisiteChecker>();
        services.AddSingleton<IHttpConnectivityChecker, HttpConnectivityChecker>();
        services.AddSingleton<IProjectFileCopier, ProjectFileCopier>();
        services.AddSingleton<IProjectScaffolder, ProjectScaffolder>();
        services.AddSingleton<IFileLockService, FileLockService>();
        services.AddSingleton<IPrivateTemp, PrivateTemp>();
        services.AddSingleton<IWebConfigService, WebConfigService>();
        services.AddSingleton<IRemoteSqlBackupService, RemoteSqlBackupService>();
        services.AddSingleton<ISqlConnectionTester, SqlConnectionTester>();
        services.AddSingleton<IDatabaseProvisioner, DatabaseProvisioner>();
        services.AddSingleton<IDnnInstaller, Dnn.DnnInstaller>();
        services.AddSingleton<ISecretStore, WindowsCredentialStore>();
        services.AddSingleton<IBacpacService, SqlPackageService>();
        services.AddSingleton<HostResourceMonitor>();
        services.AddSingleton<ProcessSampler>();
        services.AddSingleton<StartupTask>();
        services.AddSingleton<AppDataCleaner>();

        // What keeps the Projects page current without a Refresh: the monitor, and the things in Windows that tell it
        // when to look again (see ServerStateMonitor).
        services.AddSingleton<IChangeSource>(sp =>
        {
            // The folder in the settings as they are now - it follows a change of them.
            var options = sp.GetRequiredService<IOptions<AppOptions>>().Value;
            return new FolderChangeSource(ChangeKind.Projects, "the projects folder", () => options.BaseDirectory);
        });
        services.AddSingleton<IChangeSource>(_ => new FolderChangeSource(ChangeKind.Iis, "IIS's configuration",
            Path.Combine(Environment.SystemDirectory, "inetsrv", "config"), "applicationHost.config"));
        services.AddSingleton<IChangeSource>(_ => new ServiceStatusSource(ChangeKind.Iis, "the IIS service", "W3SVC"));
        services.AddSingleton<IChangeSource>(_ => new EventLogSource(ChangeKind.Iis, "IIS's events", "System",
            "*[System[Provider[@Name='Microsoft-Windows-WAS' or @Name='Microsoft-Windows-IIS-W3SVC']]]"));
        services.AddSingleton<ServerStateMonitor>();
        services.AddSingleton<IServerStateFeed>(sp => sp.GetRequiredService<ServerStateMonitor>());
        // Keeps the sites switched to "keep warm" warm while DNN Manager runs - it follows the monitor.
        services.AddSingleton<KeepWarm.KeepWarmService>();
        // Keeps the sites' host names in the hosts file, so they open without internet - it follows the monitor.
        services.AddSingleton<Hosts.HostsFileService>();
        services.AddSingleton<ProjectsFolderGuard>();
        services.AddSingleton<SiteLogs.SiteLogCatalog>();
        services.AddSingleton<Application.Upgrades.IDnnSiteInspector, Dnn.DnnSiteInspector>();
        services.AddSingleton<Application.Upgrades.IDnnUpgradeChecks, Dnn.DnnUpgradeChecks>();

        // A lookup that doesn't answer within 30 seconds counts as offline (the saved releases are used) - not 100.
        services.AddHttpClient<IDnnReleaseService, GitHubDnnReleaseService>(http => http.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IDnnPackageInstaller, DnnPackageInstaller>();
        return services;
    }
}
