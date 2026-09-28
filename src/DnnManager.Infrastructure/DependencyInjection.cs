using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Docker;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Github;
using DnnManager.Infrastructure.Iis;
using DnnManager.Infrastructure.Prereq;
using DnnManager.Infrastructure.Processes;
using DnnManager.Infrastructure.Projects;
using DnnManager.Infrastructure.Sql;
using DnnManager.Infrastructure.WebConfigs;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<IProjectRepository, FileSystemProjectRepository>();
        services.AddSingleton<IIisManager, IisManager>();
        services.AddSingleton<ISqlServerService, SqlServerService>();
        services.AddSingleton<IDockerComposeService, DockerComposeService>();
        services.AddSingleton<IPrerequisiteChecker, WindowsPrerequisiteChecker>();
        services.AddSingleton<IHttpConnectivityChecker, HttpConnectivityChecker>();
        services.AddSingleton<IProjectFileCopier, ProjectFileCopier>();
        services.AddSingleton<IProjectScaffolder, ProjectScaffolder>();
        services.AddSingleton<IWebConfigService, WebConfigService>();
        services.AddSingleton<IRemoteSqlBackupService, RemoteSqlBackupService>();
        services.AddSingleton<ISqlConnectionTester, SqlConnectionTester>();
        services.AddSingleton<IBacpacService, SqlPackageService>();

        services.AddHttpClient<IDnnReleaseService, GitHubDnnReleaseService>();
        services.AddHttpClient<IDnnPackageInstaller, DnnPackageInstaller>();
        return services;
    }
}
