using System.Windows.Input;

namespace DnnManager.Presentation.Services;

/// <summary>
/// A keyboard shortcut: modifiers and one key - written as VS Code writes them, <c>Ctrl+Shift+P</c>, <c>Ctrl+`</c>,
/// <c>Shift+F5</c>. Only shortcuts with Ctrl or Alt, or a function key, are allowed: a plain letter, Enter or Space
/// would take a key away from typing and from the lists and buttons that use it.
/// </summary>
public readonly record struct Shortcut(ModifierKeys Modifiers, Key Key)
{
    // How keys are written - the character on the key where it has one (US layout, as VS Code shows them).
    private static readonly Dictionary<Key, string> Names = new()
    {
        [Key.OemComma] = ",", [Key.OemPeriod] = ".", [Key.Oem3] = "`", [Key.OemMinus] = "-", [Key.OemPlus] = "=",
        [Key.Oem2] = "/", [Key.Oem5] = "\\", [Key.OemOpenBrackets] = "[", [Key.Oem6] = "]", [Key.Oem1] = ";",
        [Key.OemQuotes] = "'", [Key.Next] = "PageDown", [Key.Prior] = "PageUp", [Key.Return] = "Enter", [Key.Back] = "Backspace",
        [Key.Escape] = "Esc", [Key.Delete] = "Delete", [Key.Insert] = "Insert",
        [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4", [Key.D5] = "5", [Key.D6] = "6",
        [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",
    };

    private static readonly Dictionary<string, Key> ByName =
        Names.GroupBy(n => n.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The keys text boxes use for editing - a shortcut on one of them is allowed, but warned about.</summary>
    private static readonly Shortcut[] Editing =
        [.. new[] { Key.A, Key.C, Key.V, Key.X, Key.Z, Key.Y }.Select(k => new Shortcut(ModifierKeys.Control, k))];

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Names.TryGetValue(Key, out var name) ? name : Key.ToString());
        return string.Join('+', parts);
    }

    /// <summary>Reads <c>Ctrl+Shift+P</c>; null when it isn't a shortcut.</summary>
    public static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // "Ctrl++" is Ctrl and the plus key: split off the last key first.
        var trimmed = text.Trim();
        var split = trimmed.EndsWith("++", StringComparison.Ordinal) ? trimmed.Length - 1 : trimmed.LastIndexOf('+');
        var keyText = split < 0 ? trimmed : trimmed[(split + 1)..];
        var modifiers = ModifierKeys.None;
        if (split > 0)
            foreach (var part in trimmed[..split].Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                ModifierKeys? modifier = part.ToLowerInvariant() switch
                {
                    "ctrl" or "control" => ModifierKeys.Control,
                    "shift" => ModifierKeys.Shift,
                    "alt" => ModifierKeys.Alt,
                    "win" or "windows" or "meta" => ModifierKeys.Windows,
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

    /// <summary>The shortcut a key press is - null for a modifier on its own.</summary>
    public static Shortcut? Of(KeyEventArgs e)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        return key == Key.None || IsModifier(key) ? null : new Shortcut(Keyboard.Modifiers, key);
    }

    /// <summary>Why this can't be a shortcut; null when it can.</summary>
    public string? Problem =>
        IsFunctionKey || Modifiers.HasFlag(ModifierKeys.Control) || Modifiers.HasFlag(ModifierKeys.Alt) ? null
            : "Use Ctrl or Alt with it, or a function key (F1-F24) - plain keys are for typing and moving around.";

    /// <summary>A text box's editing key (Ctrl+C, Ctrl+V…) - it would no longer copy or paste in one.</summary>
    public bool IsEditingKey => Editing.Contains(this);

    private bool IsFunctionKey => Key is >= Key.F1 and <= Key.F24;

    private static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
