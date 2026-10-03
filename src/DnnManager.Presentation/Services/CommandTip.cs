using System.Windows;
using System.Windows.Controls;

namespace DnnManager.Presentation.Services;

/// <summary>
/// A button's tooltip with the shortcut of the command it does, as the shortcut is now - "Show the panel (Ctrl+J)". Set
/// <c>s:CommandTip.Command="panel.toggle"</c> next to its <c>ToolTip</c> (a text, which code may change too): each time
/// the tooltip opens, the command's shortcut from Settings → Keyboard shortcuts is put after it - nothing when the
/// command has none. So a changed shortcut shows at once, and no tooltip names a key that no longer does it.
/// </summary>
public static class CommandTip
{
    /// <summary>The commands and their shortcuts - set by the main window once they are registered.</summary>
    public static AppCommands? Commands { get; set; }

    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached("Command", typeof(string),
        typeof(CommandTip), new PropertyMetadata(null, OnCommandChanged));

    public static string? GetCommand(DependencyObject element) => (string?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, string? value) => element.SetValue(CommandProperty, value);

    // The tooltip as written, and as last shown with the shortcut - to tell that from a new text code set since.
    private static readonly DependencyProperty WrittenProperty = DependencyProperty.RegisterAttached("Written", typeof(string),
        typeof(CommandTip), new PropertyMetadata(null));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(string),
        typeof(CommandTip), new PropertyMetadata(null));

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.ToolTipOpening -= Opening;
        if (e.NewValue is not null) element.ToolTipOpening += Opening;
    }

    private static void Opening(object sender, ToolTipEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (element.ToolTip is not string current) return;
        var written = current == (string?)element.GetValue(ShownProperty) ? (string)element.GetValue(WrittenProperty) : current;
        var tip = With(written, GetCommand(element));
        element.SetValue(WrittenProperty, written);
        element.SetValue(ShownProperty, tip);
        element.ToolTip = tip;
    }

    /// <summary><paramref name="text"/> with the shortcut of <paramref name="commandId"/> after it in brackets - or as it is.</summary>
    public static string With(string text, string? commandId) =>
        commandId is not null && Commands?.Find(commandId) is { } command && Commands.ShortcutOf(command) is { } shortcut
            ? $"{text} ({shortcut})"
            : text;
}
