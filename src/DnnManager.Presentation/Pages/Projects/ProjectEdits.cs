using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages.Projects;

/// <summary>
/// Changing a project after it was made, and packaging it for the live server: each asks in its dialog, then runs as an
/// operation (the Output tab, Cancel takes it back). The same from the Details tabs' Edit buttons, the project's menu
/// and the command palette.
/// </summary>
internal static class ProjectEdits
{
    /// <summary>Not while another operation runs, and only for a site whose folder is there.</summary>
    public static bool CanEdit(IServiceProvider services, ProjectRow row) =>
        !services.GetRequiredService<OperationRunner>().IsBusy && Directory.Exists(row.Path);

    public static async void Rename(IServiceProvider services, ProjectRow row)
    {
        if (!CanEdit(services, row)) return;
        var options = services.GetRequiredService<IOptions<AppOptions>>().Value;
        var iis = services.GetRequiredService<IIisManager>();
        var (name, path) = (row.Name, row.Path);
        var sites = iis.GetSiteStates();
        string? Problem(string candidate) =>
            ProjectName.Validate(candidate) is { Success: false } invalid ? invalid.Error
            : !candidate.Equals(name, StringComparison.OrdinalIgnoreCase) && sites.ContainsKey(candidate) ? $"IIS already has a site named '{candidate}'."
            : null;
        // Offered when the site answers on <name>.<suffix> and its database is named like it - DNN Manager's own naming.
        var host = options.HostnameFor(name);
        var currentHost = row.IisSite.Bindings.Any(b => b.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) ? host : null;
        var database = DatabaseOf(services, row) is { Kind: not DatabaseKind.LocalDbFile } db && db.Database.Equals(name, StringComparison.OrdinalIgnoreCase)
            ? db.Database : null;

        if (RenameProjectDialog.Show(name, Problem, currentHost, options.HostnameFor, database) is not { } choice) return;
        var request = new RenameProjectRequest
        {
            SiteName = name, Directory = path, NewName = choice.NewName, RenameHost = choice.RenameHost, RenameDatabase = choice.RenameDatabase
        };
        if (await Run(services, $"Rename '{name}' → '{choice.NewName}'",
                (sp, reporter, ct) => sp.GetRequiredService<RenameProjectUseCase>().ExecuteAsync(request, reporter, ct)))
            Toast.Show($"'{name}' is '{choice.NewName}' now.", ToastKind.Success);
    }

    public static async void EditBindings(IServiceProvider services, ProjectRow row)
    {
        if (!CanEdit(services, row)) return;
        var (name, path) = (row.Name, row.Path);
        var http = row.IisSite.Bindings.Where(b => b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port is not null)
            .Select(b => (b.Host, b.Port!.Value)).ToList();
        var others = row.IisSite.Bindings.Where(b => !b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) || b.Port is null)
            .Select(b => b.ToString()).ToList();
        if (BindingsDialog.Show(name, http, others) is not { } bindings) return;
        if (await Run(services, $"Edit the host names of '{name}'",
                (sp, reporter, ct) => sp.GetRequiredService<EditBindingsUseCase>().ExecuteAsync(name, path, bindings, reporter, ct)))
            Toast.Show($"'{name}' answers on {string.Join(", ", bindings.Select(b => b.Host.Length > 0 ? DnnSiteAddress.AliasFor(b.Host, b.Port) : $"*:{b.Port}"))}.",
                ToastKind.Success);
    }

    public static async void EditAppPool(IServiceProvider services, ProjectRow row)
    {
        if (!CanEdit(services, row)) return;
        var (name, path) = (row.Name, row.Path);
        if (services.GetRequiredService<IIisManager>().GetSiteDetails(name) is not { Pool: { } pool })
        {
            Dialogs.Error($"'{name}' has no app pool to edit.");
            return;
        }
        if (AppPoolDialog.Show(name, pool.Name, IisPoolSettings.From(pool)) is not { } settings) return;
        if (await Run(services, $"Edit the app pool of '{name}'",
                (sp, reporter, ct) => sp.GetRequiredService<EditAppPoolUseCase>().ExecuteAsync(name, path, settings, reporter, ct)))
            Toast.Show($"The app pool of '{name}' was changed and recycled.", ToastKind.Success);
    }

    public static async void ChangeDatabase(IServiceProvider services, ProjectRow row)
    {
        if (!CanEdit(services, row)) return;
        var (name, path) = (row.Name, row.Path);
        var login = services.GetRequiredService<IIisManager>().AppPoolIdentity(name);
        if (DatabaseConnectionDialog.Show(services, name, DatabaseOf(services, row), login) is not { } connection) return;
        if (await Run(services, $"Change the database connection of '{name}'",
                (sp, reporter, ct) => sp.GetRequiredService<ChangeDatabaseConnectionUseCase>().ExecuteAsync(name, path, connection, reporter, ct)))
            Toast.Show($"'{name}' uses {connection.Describe()}.", ToastKind.Success);
    }

    public static async void ExportForDeployment(IServiceProvider services, ProjectRow row)
    {
        if (!CanEdit(services, row)) return;
        var folder = Path.Combine(services.GetRequiredService<AppDataPaths>().DeploymentsDirectory, $"{row.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");
        var hasDatabase = row.Project.DatabaseName is not null && !row.Project.DatabaseIsFile;
        if (DeploymentExportDialog.Show(row.Name, row.Path, folder, hasDatabase) is not { } request) return;
        if (await Run(services, $"Export '{row.Name}' for deployment",
                (sp, reporter, ct) => sp.GetRequiredService<ExportForDeploymentUseCase>().ExecuteAsync(request, reporter, ct)))
            Toast.Show($"The deployment package of '{row.Name}' is ready.", ToastKind.Success, "Open folder", () => Shell.Open(request.OutputFolder));
    }

    /// <summary>The site's database as its web.config names it; null when it names none of its own.</summary>
    private static DatabaseConnection? DatabaseOf(IServiceProvider services, ProjectRow row)
    {
        using var scope = services.CreateScope();
        var project = scope.ServiceProvider.GetRequiredService<IProjectRepository>().Build(row.Name, row.Path);
        return scope.ServiceProvider.GetRequiredService<LocalSqlContainer>().DatabaseOf(project);
    }

    private static Task<bool> Run(IServiceProvider services, string title, Func<IServiceProvider, IProgressReporter, CancellationToken, Task<Result>> work) =>
        services.GetRequiredService<OperationRunner>().RunAsync(title, work);
}
