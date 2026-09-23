using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Tui;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Views;

/// <summary>What to do with a project folder that already exists.</summary>
internal enum ExistingFolderAction { IisOnly, IisAndDatabase, Redownload }

internal static class ExistingFolderMenu
{
    private sealed record Choice(ExistingFolderAction Action, string Label);

    /// <summary>
    /// Asks how to set up an existing folder. <paramref name="offerRedownload"/> adds the option to
    /// extract a fresh DNN package over it (used by setup). Returns null when cancelled.
    /// </summary>
    public static ExistingFolderAction? Pick(ConsoleScreen screen, string title, bool offerRedownload)
    {
        var items = new List<Choice>
        {
            new(ExistingFolderAction.IisOnly,        "Keep the files - set up the IIS website only"),
            new(ExistingFolderAction.IisAndDatabase, "Keep the files - set up the IIS website + local database"),
        };
        if (offerRedownload)
            items.Add(new(ExistingFolderAction.Redownload, "Download DNN into the folder (overwrites files)"));

        return new SelectableList<Choice>(screen)
        {
            Title = title,
            Hint = "↑/↓ · Enter · Esc to cancel",
            Items = items,
            Display = c => c.Label
        }.Show()?.Action;
    }
}

/// <summary>Hosts a project folder that already exists: IIS website, optionally a local database.</summary>
internal sealed class ExistingProjectView
{
    private readonly ConsoleScreen _screen;
    private readonly HostExistingProjectUseCase _useCase;
    private readonly IProjectRepository _repo;
    private readonly IIisManager _iis;
    private readonly StatusWriter _status;
    private readonly TuiProgressReporter _reporter;
    private readonly AppOptions _opts;

    public ExistingProjectView(ConsoleScreen screen, HostExistingProjectUseCase useCase, IProjectRepository repo,
        IIisManager iis, StatusWriter status, TuiProgressReporter reporter, IOptions<AppOptions> opts)
    {
        _screen = screen; _useCase = useCase; _repo = repo; _iis = iis;
        _status = status; _reporter = reporter; _opts = opts.Value;
    }

    private sealed record FolderChoice(string Name, string Label);

    public async Task RunAsync(CancellationToken ct)
    {
        var folders = _repo.ListAllProjectDirectories();
        if (folders.Count == 0)
        {
            _screen.Clear();
            _status.Fail($"No project folders found in {_opts.BaseDirectory}.");
            _status.Pause();
            return;
        }

        // Show which folders already have a website, so the ones still needing one stand out.
        var sites = _iis.GetSiteStates();
        var items = folders
            .Select(n => new FolderChoice(n, sites.TryGetValue(n, out var state) ? $"{n}  (IIS: {state})" : $"{n}  (no IIS site)"))
            .ToArray();
        var chosen = new SelectableList<FolderChoice>(_screen)
        {
            Title = $"Set up an existing project folder ({_opts.BaseDirectory})",
            Hint = "↑/↓ · Enter · Esc to cancel",
            Items = items,
            Display = c => c.Label
        }.Show();
        if (chosen is null) return;

        // The folder name becomes the IIS site, app pool and hostname, so it must be a valid project name.
        var check = ProjectName.Validate(chosen.Name);
        if (!check.Success)
        {
            _screen.Clear();
            _status.Fail($"'{chosen.Name}' can't be used as a project: {check.Error} Rename the folder and retry.");
            _status.Pause();
            return;
        }

        var action = ExistingFolderMenu.Pick(_screen, $"'{chosen.Name}' - what should be set up?", offerRedownload: false);
        if (action is null) return;
        await RunForAsync(chosen.Name, action == ExistingFolderAction.IisAndDatabase, ct);
    }

    /// <summary>Runs the setup for <paramref name="projectName"/>. Also reached from "Setup a new DNN project".</summary>
    public async Task RunForAsync(string projectName, bool setupDatabase, CancellationToken ct)
    {
        _screen.Clear();
        _screen.DrawCentredTitle(1, $"Set up existing project '{projectName}'", Theme.HeaderFg);
        Console.SetCursorPosition(0, 3);

        var req = new HostExistingProjectRequest { ProjectName = projectName, SetupDatabase = setupDatabase };
        var result = await _useCase.ExecuteAsync(req, _reporter, ct);
        if (!result.Success) _status.Fail(result.Error ?? "Setup failed.");
        _status.Pause();
    }
}
