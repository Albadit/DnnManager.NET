using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Tui;

namespace DnnManager.Presentation.Views;

internal sealed class ExportView
{
    private readonly ConsoleScreen _screen;
    private readonly ExportDatabaseUseCase _useCase;
    private readonly IProjectRepository _repo;
    private readonly StatusWriter _status;
    private readonly TuiProgressReporter _reporter;

    public ExportView(ConsoleScreen screen, ExportDatabaseUseCase useCase, IProjectRepository repo,
        StatusWriter status, TuiProgressReporter reporter)
    { _screen = screen; _useCase = useCase; _repo = repo; _status = status; _reporter = reporter; }

    public async Task RunAsync(CancellationToken ct)
    {
        var (project, env) = EnvironmentSelector.Pick(_screen, _repo, "Backup database - select project");
        if (project is null) return;
        _screen.Clear();
        var r = await _useCase.ExecuteAsync(project, env, _reporter, ct);
        if (!r.Success) _status.Fail(r.Error ?? "Export failed.");
        _status.Pause();
    }
}

internal sealed class ImportView
{
    private readonly ConsoleScreen _screen;
    private readonly ImportDatabaseUseCase _useCase;
    private readonly IProjectRepository _repo;
    private readonly StatusWriter _status;
    private readonly TuiProgressReporter _reporter;

    public ImportView(ConsoleScreen screen, ImportDatabaseUseCase useCase, IProjectRepository repo,
        StatusWriter status, TuiProgressReporter reporter)
    { _screen = screen; _useCase = useCase; _repo = repo; _status = status; _reporter = reporter; }

    public async Task RunAsync(CancellationToken ct)
    {
        var (project, env) = EnvironmentSelector.Pick(_screen, _repo, "Overwrite database - select project");
        if (project is null) return;

        var p = _repo.Build(project);
        var dir = p.BackupDirectory;
        // Azure production can only be overwritten from a .bacpac, so don't offer .bak there.
        var prod = env == DnnEnvironment.Production;
        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".bacpac", StringComparison.OrdinalIgnoreCase)
                         || (!prod && f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(File.GetLastWriteTime).ToList()
            : new List<string>();
        if (files.Count == 0)
        {
            _screen.Clear();
            _status.Fail(prod ? $"No .bacpac files in {dir} (Azure SQL requires a .bacpac)." : $"No .bak or .bacpac files in {dir}");
            _status.Pause();
            return;
        }

        var bakMenu = new SelectableList<string>(_screen)
        {
            Title = $"Select a backup file in {dir}",
            Items = files,
            Display = f => $"{Path.GetFileName(f)}  ({new FileInfo(f).Length / 1024d / 1024d:N1} MB)"
        };
        var bak = bakMenu.Show();
        if (bak is null) return;

        _screen.Clear();
        var r = await _useCase.ExecuteAsync(project, env, bak, _reporter, ct);
        if (!r.Success) _status.Fail(r.Error ?? "Import failed.");
        _status.Pause();
    }
}

internal static class EnvironmentSelector
{
    public static (string? Project, DnnEnvironment Env) Pick(ConsoleScreen screen, IProjectRepository repo, string title)
    {
        var projects = repo.ListConfiguredProjects();
        if (projects.Count == 0)
        {
            screen.Clear();
            Console.ForegroundColor = Theme.Error;
            Console.WriteLine("\n  No configured projects (none have a web.config).");
            Console.ResetColor();
            Console.WriteLine("  Press any key…");
            ConsoleInput.Flush();
            Console.ReadKey(true);
            return (null, DnnEnvironment.Developer);
        }
        var proj = new SelectableList<string>(screen) { Title = title, Items = projects, Display = p => p }.Show();
        if (proj is null) return (null, DnnEnvironment.Developer);

        var envs = new[]
        {
            new EnvChoice(DnnEnvironment.Developer,  "developer (local Docker SQL Server)"),
            new EnvChoice(DnnEnvironment.Production, "production (remote SQL Server)"),
        };
        var envChoice = new SelectableList<EnvChoice>(screen)
        {
            Title = "Choose environment",
            Items = envs,
            Display = e => e.Label
        }.Show();
        if (envChoice is null) return (null, DnnEnvironment.Developer);
        return (proj, envChoice.Env);
    }

    private sealed record EnvChoice(DnnEnvironment Env, string Label);
}
