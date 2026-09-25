using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Tui;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Views;

/// <summary>What to do with a project folder that already exists.</summary>
internal enum ExistingFolderAction { IisOnly, IisAndDatabase, DatabaseOnly, Redownload }

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
            new(ExistingFolderAction.DatabaseOnly,   "Keep the files - set up the local database only"),
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

/// <summary>Sets up a project folder that already exists: its IIS website, its local database, or both.</summary>
internal sealed class ExistingProjectView
{
    private readonly ConsoleScreen _screen;
    private readonly HostExistingProjectUseCase _useCase;
    private readonly IProjectRepository _repo;
    private readonly IIisManager _iis;
    private readonly StatusWriter _status;
    private readonly TuiProgressReporter _reporter;
    private readonly TextPrompt _text;
    private readonly AppOptions _opts;

    public ExistingProjectView(ConsoleScreen screen, HostExistingProjectUseCase useCase, IProjectRepository repo,
        IIisManager iis, StatusWriter status, TuiProgressReporter reporter, TextPrompt text, IOptions<AppOptions> opts)
    {
        _screen = screen; _useCase = useCase; _repo = repo; _iis = iis;
        _status = status; _reporter = reporter; _text = text; _opts = opts.Value;
    }

    private sealed record FolderChoice(string Name, string Label);
    private enum BackupKind { File, EnterPath, None }
    private sealed record BackupChoice(BackupKind Kind, string? Path, string Label);
    // File == null means "no backup - leave / create the database empty".
    private sealed record BackupPick(string? File);

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
        await RunForAsync(chosen.Name, action.Value, ct);
    }

    /// <summary>
    /// Runs <paramref name="action"/> (IIS only, IIS + database, or database only) for
    /// <paramref name="projectName"/>. Also reached from "Setup a new DNN project".
    /// </summary>
    public async Task RunForAsync(string projectName, ExistingFolderAction action, CancellationToken ct)
    {
        var setupDatabase = action is ExistingFolderAction.IisAndDatabase or ExistingFolderAction.DatabaseOnly;
        string? backupFile = null;
        if (setupDatabase)
        {
            var pick = PickBackupFile(projectName);
            if (pick is null) return; // Esc - back to the menu
            backupFile = pick.File;
        }

        _screen.Clear();
        _screen.DrawCentredTitle(1, $"Set up existing project '{projectName}'", Theme.HeaderFg);
        Console.SetCursorPosition(0, 3);

        var req = new HostExistingProjectRequest
        {
            ProjectName = projectName,
            SetupIis = action is ExistingFolderAction.IisOnly or ExistingFolderAction.IisAndDatabase,
            SetupDatabase = setupDatabase,
            BackupFilePath = backupFile
        };
        var result = await _useCase.ExecuteAsync(req, _reporter, ct);
        if (!result.Success) _status.Fail(result.Error ?? "Setup failed.");
        _status.Pause();
    }

    /// <summary>
    /// Asks which backup to restore into the database: a .bacpac/.bak found in the project (its backups
    /// folder or its root, newest first), one at a typed path, or none. Returns null when cancelled.
    /// </summary>
    private BackupPick? PickBackupFile(string projectName)
    {
        var project = _repo.Build(projectName);
        var found = new[] { project.BackupDirectory, project.ProjectDirectory }
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateFiles)
            .Where(LocalSqlContainer.IsBackupFile)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime);

        var items = found
            .Select(f => new BackupChoice(BackupKind.File, f.FullName,
                $"{Path.GetRelativePath(project.ProjectDirectory, f.FullName)}  " +
                $"({f.Length / 1024d / 1024d:N1} MB, {f.LastWriteTime:yyyy-MM-dd HH:mm})"))
            .Append(new BackupChoice(BackupKind.EnterPath, null, "[ Enter a file path… ]"))
            .Append(new BackupChoice(BackupKind.None, null, "[ No backup - keep the database / create it empty ]"))
            .ToArray();

        while (true)
        {
            var choice = new SelectableList<BackupChoice>(_screen)
            {
                Title = $"'{projectName}' - restore a .bacpac / .bak into the database",
                Hint = "↑/↓ · Enter · Esc to cancel",
                Items = items,
                Display = c => c.Label
            }.Show();
            if (choice is null) return null;

            switch (choice.Kind)
            {
                case BackupKind.File: return new BackupPick(choice.Path);
                case BackupKind.None: return new BackupPick(null);
            }

            var typed = PromptBackupPath();
            if (typed is not null) return new BackupPick(typed);
            // Esc at the path prompt goes back to the list rather than out of the whole flow.
        }
    }

    // Re-prompts until the path names an existing .bacpac/.bak; null on Esc / blank.
    private string? PromptBackupPath()
    {
        _screen.Clear();
        _screen.DrawCentredTitle(1, "Backup file", Theme.HeaderFg);
        Console.SetCursorPosition(0, 3);
        while (true)
        {
            // Explorer's "Copy as path" wraps the path in quotes - accept it as pasted.
            var path = _text.Show("Path to .bacpac / .bak (Esc to go back)")?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!File.Exists(path))
                _status.Fail($"File not found: {path}");
            else if (!LocalSqlContainer.IsBackupFile(path))
                _status.Fail("Choose a .bacpac or .bak file.");
            else
                return Path.GetFullPath(path);
        }
    }
}
