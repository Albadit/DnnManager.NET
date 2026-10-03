using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Pages.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Something DNN Manager can do from the keyboard - by its shortcut, or from the command palette. A project command
/// (<see cref="ForProject"/>) acts on the selected project; with none selected, the palette asks which one.
/// </summary>
public sealed class AppCommand
{
    /// <summary>What <c>keyboard.shortcuts</c> in settings.json names it by, e.g. <c>project.start</c>. Never changed once released.</summary>
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>The part of DNN Manager it belongs to: Projects, Project, Pages, Panel, Terminal, Application.</summary>
    public required string Area { get; init; }
    public string? DefaultShortcut { get; init; }
    /// <summary>More words the palette finds it by.</summary>
    public string Keywords { get; init; } = "";
    /// <summary>Its shortcut works while a terminal has the keyboard too - otherwise the key goes to the shell.</summary>
    public bool InTerminal { get; init; }

    /// <summary>Whether it makes sense now (a command for one thing).</summary>
    public Func<bool> IsAvailable { get; init; } = () => true;
    public Action? Run { get; init; }

    /// <summary>A project command: which projects it makes sense for, and what it does to one.</summary>
    public Func<ProjectRow, bool>? ForProject { get; init; }
    public Action<ProjectRow>? RunOn { get; init; }

    public bool IsProjectCommand => ForProject is not null;

    /// <summary>As the palette lists it: "Project: Start project".</summary>
    public string Label => $"{Area}: {Title}";

    public override string ToString() => Label;
}

/// <summary>
/// Every <see cref="AppCommand"/> and its keyboard shortcut - its default, or the one chosen in Settings - Keyboard
/// shortcuts, kept in settings.json (<c>keyboard.shortcuts</c>, only what differs from the defaults) and saved at once.
/// </summary>
public sealed class AppCommands
{
    private readonly List<AppCommand> _commands = new();
    private readonly SettingsStore _store;
    private readonly ILogger<AppCommands> _log;
    // Command id -> its shortcut as chosen ("" = none); a command not here has its default.
    private Dictionary<string, string> _custom;

    public AppCommands(SettingsStore store, IOptions<AppOptions> options, ILogger<AppCommands> log)
    {
        _store = store;
        _log = log;
        _custom = new Dictionary<string, string>(options.Value.KeyboardShortcuts, StringComparer.Ordinal);
        // Saved elsewhere (Reset settings to defaults): what the file says now.
        options.Value.Changed += () =>
        {
            _custom = new Dictionary<string, string>(options.Value.KeyboardShortcuts, StringComparer.Ordinal);
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>A shortcut was changed or reset.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AppCommand> All => _commands;

    public void Add(AppCommand command)
    {
        if (_commands.Any(c => c.Id == command.Id)) throw new InvalidOperationException($"Command {command.Id} is there twice.");
        _commands.Add(command);
    }

    public AppCommand? Find(string id) => _commands.FirstOrDefault(c => c.Id == id);

    public Shortcut? DefaultOf(AppCommand command) => Shortcut.Parse(command.DefaultShortcut);

    /// <summary>The command's shortcut now: the one chosen, or its default; null when it has none.</summary>
    public Shortcut? ShortcutOf(AppCommand command) =>
        _custom.TryGetValue(command.Id, out var chosen) ? Shortcut.Parse(chosen) : DefaultOf(command);

    public bool IsCustom(AppCommand command) => _custom.ContainsKey(command.Id);

    /// <summary>The command a key press runs - the first one listed with that shortcut.</summary>
    public AppCommand? Match(Shortcut pressed) => _commands.FirstOrDefault(c => ShortcutOf(c) == pressed);

    /// <summary>The other commands with the same shortcut as <paramref name="command"/> - only the first listed of them runs.</summary>
    public IReadOnlyList<AppCommand> ConflictsOf(AppCommand command) =>
        ShortcutOf(command) is { } shortcut ? _commands.Where(c => c != command && ShortcutOf(c) == shortcut).ToList() : [];

    /// <summary>Gives <paramref name="command"/> <paramref name="shortcut"/> (null: none) and saves it. Returns what went wrong, or null.</summary>
    public string? Set(AppCommand command, Shortcut? shortcut)
    {
        if (shortcut is { Problem: { } problem }) return problem;
        var next = new Dictionary<string, string>(_custom, StringComparer.Ordinal);
        if (shortcut == DefaultOf(command)) next.Remove(command.Id);
        else next[command.Id] = shortcut?.ToString() ?? "";
        return Save(next);
    }

    public string? Reset(AppCommand command)
    {
        var next = new Dictionary<string, string>(_custom, StringComparer.Ordinal);
        next.Remove(command.Id);
        return Save(next);
    }

    public string? ResetAll() => Save(new Dictionary<string, string>(StringComparer.Ordinal));

    private string? Save(Dictionary<string, string> next)
    {
        try
        {
            _store.Update(s => s.Keyboard.Shortcuts = new Dictionary<string, string>(next, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save the keyboard shortcuts");
            return $"Not saved - {ex.Message}";
        }
        _custom = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }
}
