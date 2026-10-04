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
        services.AddScoped<SetupProjectUseCase>();
        services.AddScoped<HostExistingProjectUseCase>();
        services.AddScoped<ImportProjectUseCase>();
        services.AddScoped<ExportProjectUseCase>();
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
        return services;
    }
}
