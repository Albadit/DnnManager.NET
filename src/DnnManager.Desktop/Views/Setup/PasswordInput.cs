using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace DnnManager.Desktop.Views.Setup;

/// <summary>
/// A password field with an eye at its right end that shows what was typed - the Windows app's PasswordInput
/// (Controls/PasswordInput.xaml). The eye is the next tab stop after the box; its name says what pressing it does now.
/// </summary>
public sealed class PasswordInput : UserControl
{
    private readonly TextBox _box;
    private readonly ToggleButton _eye;
    private readonly TextBlock _glyph;

    public PasswordInput()
    {
        _glyph = new TextBlock { FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _eye = new ToggleButton { Classes = { "eye" }, Content = _glyph };
        _box = new TextBox { PasswordChar = '•', InnerRightContent = _eye };
        _box.TextChanged += (_, _) => PasswordChanged?.Invoke(this, EventArgs.Empty);
        _eye.IsCheckedChanged += (_, _) => ShowEye();
        ShowEye();
        Content = _box;
        // The field carries the name it is given (its label): a screen reader says which password it is.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationProperties.NameProperty) AutomationProperties.SetName(_box, AutomationProperties.GetName(this));
            else if (e.Property == AutomationProperties.HelpTextProperty) AutomationProperties.SetHelpText(_box, AutomationProperties.GetHelpText(this));
        };
    }

    /// <summary>The password typed, or set from code.</summary>
    public string Password
    {
        get => _box.Text ?? "";
        set
        {
            if (Password == value) return;
            _box.Text = value;
        }
    }

    /// <summary>Raised as the password changes - typed or set.</summary>
    public event EventHandler? PasswordChanged;

    /// <summary>Shows the password (a generated one, to note down).</summary>
    public void Reveal() => _eye.IsChecked = true;

    private void ShowEye()
    {
        var shown = _eye.IsChecked == true;
        _box.RevealPassword = shown;
        _glyph.Text = MainWindow.HasIconFont
            ? (string)(Avalonia.Application.Current?.FindResource(shown ? "GlyphHide" : "GlyphShow") ?? "")
            : shown ? "Hide" : "Show";
        if (MainWindow.HasIconFont && Avalonia.Application.Current?.FindResource("IconFont") is Avalonia.Media.FontFamily font) _glyph.FontFamily = font;
        else _glyph.FontSize = 11;
        var name = shown ? "Hide password" : "Show password";
        AutomationProperties.SetName(_eye, name);
        ToolTip.SetTip(_eye, name);
        if (!MainWindow.HasIconFont) _eye.Width = 40;
    }

    internal TextBox Box => _box;
}
