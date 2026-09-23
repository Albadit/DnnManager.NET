using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Tui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DnnManager.Presentation.Views;

internal sealed class MainMenuView
{
    private readonly ConsoleScreen _screen;
    private readonly IServiceProvider _sp;
    private readonly ILogger<MainMenuView> _log;

    public MainMenuView(ConsoleScreen screen, IServiceProvider sp, ILogger<MainMenuView> log)
    {
        _screen = screen; _sp = sp; _log = log;
    }

    private sealed record MenuItem(string Label, Func<CancellationToken, Task> Action);

    public async Task RunAsync(CancellationToken ct)
    {
        var items = new[]
        {
            new MenuItem("Setup a new DNN project",          c => RunScopedAsync<SetupView>(v => v.RunAsync(c))),
            new MenuItem("Setup an existing project folder", c => RunScopedAsync<ExistingProjectView>(v => v.RunAsync(c))),
            new MenuItem("Clone a DNN project (local / FTP)", c => RunScopedAsync<CloneView>(v => v.RunAsync(c))),
            new MenuItem("Remove a DNN project",             c => RunScopedAsync<RemoveView>(v => v.RunAsync(c))),
            new MenuItem("Check prerequisites",              c => RunScopedAsync<PrerequisitesView>(v => v.RunAsync(c))),
            new MenuItem("Show all projects info",           c => RunScopedAsync<ProjectsListView>(v => v.RunAsync(c))),
            new MenuItem("Database (backup / overwrite)",   RunDatabaseSubMenuAsync),
            new MenuItem("Edit saved connections",          c => RunScopedAsync<ConnectionsView>(v => v.RunAsync(c))),
        };

        int idx = 0;
        while (!ct.IsCancellationRequested)
        {
            var menu = new SelectableList<MenuItem>(_screen)
            {
                Title = "DNN Project Manager",
                Items = items,
                Display = i => i.Label,
                Hint = "↑/↓ · 1-9 quick · Enter · Esc to quit"
            };
            var chosen = menu.Show(idx);
            if (chosen is null)
            {
                if (new ConfirmDialog(_screen).Show("Are you sure you want to quit?", defaultYes: false))
                    return;
                continue;
            }
            idx = Array.IndexOf(items, chosen);
            try
            {
                await chosen.Action(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Action failed");
                Console.ForegroundColor = Theme.Error;
                Console.WriteLine($"\n  ✗ Unexpected error: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine("\n  Press any key to return…");
                ConsoleInput.Flush();
                Console.ReadKey(true);
            }
        }
    }

    private async Task RunScopedAsync<TView>(Func<TView, Task> body) where TView : notnull
    {
        using var scope = _sp.CreateScope();
        var view = scope.ServiceProvider.GetRequiredService<TView>();
        await body(view);
    }

    private async Task RunDatabaseSubMenuAsync(CancellationToken ct)
    {
        var items = new[]
        {
            new MenuItem("Backup database",    c => RunScopedAsync<ExportView>(v => v.RunAsync(c))),
            new MenuItem("Overwrite database", c => RunScopedAsync<ImportView>(v => v.RunAsync(c))),
        };
        var menu = new SelectableList<MenuItem>(_screen)
        {
            Title = "Database",
            Items = items,
            Display = i => i.Label,
            Hint = "↑/↓ · 1-9 quick · Enter · Esc to go back"
        };
        var chosen = menu.Show();
        if (chosen is null) return;
        await chosen.Action(ct);
    }
}
