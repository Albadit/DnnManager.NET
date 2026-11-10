using DnnManager.Application.Deployment;
using DnnManager.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<OperationUndo>();
        services.AddScoped<IisSiteProvisioner>();
        services.AddScoped<LocalSqlContainer>();
        services.AddScoped<SiteDatabases>();
        services.AddScoped<SetupProjectUseCase>();
        services.AddScoped<HostExistingProjectUseCase>();
        services.AddScoped<ImportProjectUseCase>();
        services.AddScoped<ExportProjectUseCase>();
        services.AddScoped<RestoreBackupUseCase>();
        services.AddScoped<UpgradeDnnUseCase>();
        services.AddScoped<RemoveProjectUseCase>();
        services.AddScoped<IisServerUseCase>();
        services.AddScoped<ControlSitesUseCase>();
        services.AddScoped<ClearSiteCacheUseCase>();
        services.AddScoped<ChangeHostPasswordUseCase>();
        services.AddScoped<SetupSqlContainerUseCase>();
        services.AddScoped<CloneProjectUseCase>();
        services.AddScoped<RenameProjectUseCase>();
        services.AddScoped<EditBindingsUseCase>();
        services.AddScoped<EditAppPoolUseCase>();
        services.AddScoped<ChangeDatabaseConnectionUseCase>();
        services.AddScoped<ExportForDeploymentUseCase>();
        // Deployments: comparing a project with its servers, push, pull, conflicts, rollback.
        services.AddSingleton<LocalScanner>();
        services.AddSingleton<SyncScanner>();
        services.AddSingleton<DeployConnections>();
        services.AddSingleton<DeployFiles>();
        services.AddScoped<SyncEngine>();
        services.AddScoped<PushChangesUseCase>();
        services.AddScoped<PullChangesUseCase>();
        services.AddScoped<ResolveConflictUseCase>();
        services.AddScoped<RollbackOperationUseCase>();
        services.AddScoped<RestoreVersionUseCase>();
        return services;
    }
}
