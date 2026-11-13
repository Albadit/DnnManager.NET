using Avalonia.Input;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop.Services;

/// <summary>
/// Something DNN Manager can do from the keyboard - by its shortcut, and from the command palette (Ctrl+Shift+P) - as the
/// Windows app's (src/DnnManager.Presentation/Services/AppCommands.cs). Its id is the key its shortcut is saved under
/// (keyboard.shortcuts), shared with the Windows app.
/// </summary>
public sealed class AppCommand
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>The group in the palette: "Application", "Pages", "Layout"…</summary>
    public required string Area { get; init; }
    public string? DefaultShortcut { get; init; }
    public string Keywords { get; init; } = "";
    public bool InPalette { get; init; } = true;
    public Func<bool> IsAvailable { get; init; } = () => true;
    public Action? Run { get; init; }

    public string Label => $"{Area}: {Title}";

    public override string ToString() => Label;
}

/// <summary>Every command, and the shortcuts as chosen (Settings → Keyboard shortcuts) or their defaults.</summary>
public sealed class AppCommands
{
    private readonly List<AppCommand> _commands = [];
    private readonly SettingsStore _store;
    private readonly ILogger<AppCommands> _log;
    private Dictionary<string, string> _custom;

    public AppCommands(SettingsStore store, IOptions<AppOptions> options, ILogger<AppCommands> log)
    {
        _store = store;
        _log = log;
        _custom = new Dictionary<string, string>(options.Value.KeyboardShortcuts, StringComparer.Ordinal);
        options.Value.Changed += () =>
        {
            _custom = new Dictionary<string, string>(options.Value.KeyboardShortcuts, StringComparer.Ordinal);
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    public IReadOnlyList<AppCommand> All => _commands;

    /// <summary>Adds a command - a page's own too (pages add theirs as they are made).</summary>
    public void Add(AppCommand command)
    {
        if (_commands.Any(c => c.Id == command.Id)) throw new InvalidOperationException($"Command {command.Id} is there twice.");
        _commands.Add(command);
    }

    public AppCommand? Find(string id) => _commands.FirstOrDefault(c => c.Id == id);

    public Shortcut? DefaultOf(AppCommand command) => Shortcut.Parse(command.DefaultShortcut);

    public Shortcut? ShortcutOf(AppCommand command) =>
        _custom.TryGetValue(command.Id, out var chosen) ? Shortcut.Parse(chosen) : DefaultOf(command);

    public AppCommand? Match(Shortcut pressed) => _commands.FirstOrDefault(c => ShortcutOf(c) == pressed);

    /// <summary>A command's shortcut as shown ("Ctrl+B"), or "".</summary>
    public string ShortcutText(string id) => Find(id) is { } command && ShortcutOf(command) is { } shortcut ? shortcut.ToString() : "";

    /// <summary>Sets <paramref name="command"/>'s shortcut (null: none) and saves it; the problem when it can't be.</summary>
    public string? Set(AppCommand command, Shortcut? shortcut)
    {
        if (shortcut is { Problem: { } problem }) return problem;
        var next = new Dictionary<string, string>(_custom, StringComparer.Ordinal);
        if (shortcut == DefaultOf(command)) next.Remove(command.Id);
        else next[command.Id] = shortcut?.ToString() ?? "";
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

/// <summary>
/// A key with its modifiers, written as VS Code (and the Windows app) writes it: "Ctrl+Shift+P", "Ctrl+`", "F1". Ctrl is
/// the Command key on macOS, as VS Code's shortcuts there.
/// </summary>
public readonly record struct Shortcut(KeyModifiers Modifiers, Key Key)
{
    private static readonly Dictionary<Key, string> Names = new()
    {
        [Key.OemComma] = ",", [Key.OemPeriod] = ".", [Key.Oem3] = "`", [Key.OemMinus] = "-", [Key.OemPlus] = "=",
        [Key.Oem2] = "/", [Key.Oem5] = "\\", [Key.OemOpenBrackets] = "[", [Key.Oem6] = "]", [Key.Oem1] = ";",
        [Key.OemQuotes] = "'", [Key.PageDown] = "PageDown", [Key.PageUp] = "PageUp", [Key.Enter] = "Enter", [Key.Back] = "Backspace",
        [Key.Escape] = "Esc", [Key.Delete] = "Delete", [Key.Insert] = "Insert",
        [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4", [Key.D5] = "5", [Key.D6] = "6",
        [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",
    };

    private static readonly Dictionary<string, Key> ByName =
        Names.GroupBy(n => n.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    // The command key: Ctrl, or ⌘ on macOS.
    private static KeyModifiers Command => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(Command)) parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl");
        if (OperatingSystem.IsMacOS() && Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add(OperatingSystem.IsMacOS() ? "Option" : "Alt");
        if (!OperatingSystem.IsMacOS() && Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(Names.TryGetValue(Key, out var name) ? name : Key.ToString());
        return string.Join('+', parts);
    }

    /// <summary>"Ctrl+Shift+P" as a shortcut; null when it isn't one.</summary>
    public static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var split = trimmed.EndsWith("++", StringComparison.Ordinal) ? trimmed.Length - 1 : trimmed.LastIndexOf('+');
        var keyText = split < 0 ? trimmed : trimmed[(split + 1)..];
        var modifiers = KeyModifiers.None;
        if (split > 0)
            foreach (var part in trimmed[..split].Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                KeyModifiers? modifier = part.ToLowerInvariant() switch
                {
                    "ctrl" or "control" or "cmd" or "command" => Command,
                    "shift" => KeyModifiers.Shift,
                    "alt" or "option" => KeyModifiers.Alt,
                    "win" or "windows" or "meta" => KeyModifiers.Meta,
                    _ => null
                };
                if (modifier is null) return null;
                modifiers |= modifier.Value;
            }
        if (ByName.TryGetValue(keyText, out var named)) return new Shortcut(modifiers, named);
        if (keyText.Length == 1 && char.IsDigit(keyText[0])) return new Shortcut(modifiers, Key.D0 + (keyText[0] - '0'));
        return Enum.TryParse<Key>(keyText, ignoreCase: true, out var key) && key != Key.None && !IsModifier(key)
            ? new Shortcut(modifiers, key) : null;
    }

    /// <summary>The shortcut pressed; null for a modifier alone.</summary>
    public static Shortcut? Of(KeyEventArgs e)
    {
        var key = e.Key switch { Key.NumPad0 => Key.D0, >= Key.NumPad1 and <= Key.NumPad9 => Key.D1 + (e.Key - Key.NumPad1), _ => e.Key };
        return key == Key.None || IsModifier(key) ? null : new Shortcut(e.KeyModifiers, key);
    }

    /// <summary>Why it can't be a shortcut: a plain key is for typing.</summary>
    public string? Problem =>
        Key is >= Key.F1 and <= Key.F24 || Modifiers.HasFlag(KeyModifiers.Control) || Modifiers.HasFlag(KeyModifiers.Meta) ||
        Modifiers.HasFlag(KeyModifiers.Alt) ? null
            : "Use Ctrl or Alt with it, or a function key (F1-F24) - plain keys are for typing and moving around.";

    private static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
