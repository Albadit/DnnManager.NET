using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using DnnManager.Desktop.Services;

namespace DnnManager.Desktop.Views.Projects;

/// <summary>
/// What the Projects page's dialogs are made of - the Windows app's dialog windows (Controls/*Dialog.xaml) built in code:
/// a window of a fixed width, its fields, an error line, OK (primary) and Cancel on the right; Enter and Esc as theirs.
/// </summary>
internal static class DialogKit
{
    private static readonly Uri StylesUri = new("avares://DnnManager.Desktop/Views/Projects/ProjectsStyles.axaml");

    /// <summary>The page's styles, for a window of its own - the dialogs use its text classes.</summary>
    public static StyleInclude Styles() => new(new Uri("avares://DnnManager.Desktop/")) { Source = StylesUri };

    public static Window Window(string title, double width, bool resizable = false, double height = double.NaN)
    {
        var window = new Window
        {
            Title = title, Width = width, CanResize = resizable, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = double.IsNaN(height) ? SizeToContent.Height : SizeToContent.Manual
        };
        if (!double.IsNaN(height)) window.Height = height;
        window.Styles.Add(Styles());
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            window.Close(false);
            e.Handled = true;
        };
        return window;
    }

    public static TextBlock Intro(string text = "") => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Label(string text) => new() { Text = text, Classes = { "fieldLabel" } };
    public static TextBlock Hint(string text = "") => new() { Text = text, Classes = { "hint" } };
    public static TextBlock Problem() => new() { Classes = { "errorLine" }, IsVisible = false };

    public static void Say(TextBlock problem, string? text)
    {
        problem.Text = text ?? "";
        problem.IsVisible = text is not null;
    }

    public static Button Ok(string text, string tip)
    {
        var button = new Button { Content = text, MinWidth = 80, IsDefault = true, Classes = { "primary" } };
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>OK and Cancel, on the right - Cancel closes with false.</summary>
    public static StackPanel Buttons(Window window, Button ok, string cancelText = "Cancel")
    {
        var cancel = new Button { Content = cancelText, MinWidth = 80, IsCancel = true };
        ToolTip.SetTip(cancel, $"{cancelText} (Esc)");
        cancel.Click += (_, _) => window.Close(false);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel }
        };
    }

    /// <summary>Shows <paramref name="window"/> modal to the main window; true when it was closed with OK.</summary>
    public static async Task<bool> ShowAsync(Window window)
    {
        if (Dialogs.Owner is { IsVisible: true } owner) return await window.ShowDialog<bool>(owner);
        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.TrySetResult(window.Tag is true);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        return await closed.Task;
    }

    /// <summary>Closes <paramref name="window"/> with OK.</summary>
    public static void Accept(Window window)
    {
        window.Tag = true;
        window.Close(true);
    }
}

/// <summary>
/// A password box with an eye to show what was typed (the Windows app's PasswordInput) - nothing kept anywhere else.
/// </summary>
internal sealed class PasswordInput : DockPanel
{
    private readonly TextBox _box = new() { PasswordChar = '•', RevealPassword = false };
    private readonly ToggleButton _eye = new() { Classes = { "icon" }, Margin = new Thickness(4, 0, 0, 0) };

    public PasswordInput(string name)
    {
        _eye.Content = OperatingSystem.IsWindows()
            ? new TextBlock { Text = ThemeBrush.Glyph("GlyphShow"), Classes = { "glyph" }, FontSize = 13 }
            : new TextBlock { Text = "Show", FontSize = 12 };
        ToolTip.SetTip(_eye, "Show password");
        Avalonia.Automation.AutomationProperties.SetName(_eye, "Show password");
        Avalonia.Automation.AutomationProperties.SetName(_box, name);
        _eye.IsCheckedChanged += (_, _) =>
        {
            _box.RevealPassword = _eye.IsChecked == true;
            ToolTip.SetTip(_eye, _box.RevealPassword ? "Hide password" : "Show password");
        };
        SetDock(_eye, Dock.Right);
        Children.Add(_eye);
        Children.Add(_box);
        _box.TextChanged += (_, _) => Changed?.Invoke();
    }

    public string Password
    {
        get => _box.Text ?? "";
        set => _box.Text = value;
    }

    public event Action? Changed;

    public void Reveal() => _eye.IsChecked = true;

    public void FocusBox() => _box.Focus();
}
