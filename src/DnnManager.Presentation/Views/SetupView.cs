using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Tui;

namespace DnnManager.Presentation.Views;

internal sealed class SetupView
{
    private readonly ConsoleScreen _screen;
    private readonly SetupProjectUseCase _useCase;
    private readonly IDnnReleaseService _releases;
    private readonly IProjectRepository _repo;
    private readonly ExistingProjectView _existing;
    private readonly StatusWriter _status;
    private readonly TuiProgressReporter _reporter;
    private readonly TextPrompt _text;

    public SetupView(ConsoleScreen screen, SetupProjectUseCase useCase, IDnnReleaseService releases,
        IProjectRepository repo, ExistingProjectView existing,
        StatusWriter status, TuiProgressReporter reporter, TextPrompt text)
    {
        _screen = screen; _useCase = useCase; _releases = releases; _repo = repo; _existing = existing;
        _status = status; _reporter = reporter; _text = text;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _screen.Clear();
        _screen.DrawCentredTitle(1, "Setup a new DNN project", Theme.HeaderFg);
        Console.SetCursorPosition(0, 3);

        // Re-prompt on a bad name instead of dropping the user back to the menu - the name becomes a
        // folder under the base directory, an IIS site/app-pool, a hostname and a database name.
        string name;
        while (true)
        {
            var entered = _text.Show("Project name");
            if (string.IsNullOrWhiteSpace(entered)) { _status.Fail("Cancelled."); _status.Pause(); return; }

            var check = ProjectName.Validate(entered);
            if (check.Success) { name = entered; break; }
            _status.Fail(check.Error!);
        }

        // The folder is already there (copied in by hand, a git checkout, an earlier run…): usually
        // only the website and database are missing, so offer that before overwriting any files.
        var overwrite = false;
        if (_repo.ProjectExists(name))
        {
            var action = ExistingFolderMenu.Pick(_screen, $"Folder '{name}' already exists - what do you want to do?",
                offerRedownload: true);
            if (action is null) return;
            if (action != ExistingFolderAction.Redownload)
            {
                await _existing.RunForAsync(name, action == ExistingFolderAction.IisAndDatabase, ct);
                return;
            }
            overwrite = true;
        }

        var apis = _releases.KnownReleaseApis.ToList();
        var apiMenu = new SelectableList<string>(_screen)
        {
            Title = "Select a DNN source",
            Items = apis,
            Display = s => s
        };
        var api = apiMenu.Show();
        if (api is null) { _status.Fail("Cancelled."); _status.Pause(); return; }

        _screen.Clear();
        _screen.DrawCentredTitle(1, $"Setup '{name}'", Theme.HeaderFg);
        Console.SetCursorPosition(0, 3);
        var version = _text.Show("DNN version (blank = latest)", allowEmpty: true);

        var req = new SetupProjectRequest
        {
            ProjectName = name,
            ReleaseApiUrl = api,
            Version = string.IsNullOrWhiteSpace(version) ? null : version,
            AllowOverwrite = overwrite
        };
        var result = await _useCase.ExecuteAsync(req, _reporter, ct);
        if (!result.Success) _status.Fail(result.Error ?? "Setup failed.");
        _status.Pause();
    }
}
