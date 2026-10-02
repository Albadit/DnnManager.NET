using System.Windows;
using System.Windows.Controls;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>A one-line text prompt - optionally checked as it is typed, with what is wrong under the box.</summary>
public partial class InputDialog : Window
{
    private readonly Func<string, string?>? _validate;

    private InputDialog(string question, string? initial, string okText, Func<string, string?>? validate)
    {
        _validate = validate;
        InitializeComponent();
        ThemeManager.Track(this);
        Question.Text = question;
        OkButton.Content = okText;
        Text.Text = initial ?? string.Empty;
        Check();
        Loaded += (_, _) => { Text.Focus(); Text.SelectAll(); };
    }

    private string Value => Text.Text.Trim();

    /// <summary>
    /// Returns the entered text (trimmed), or null when cancelled or left blank. <paramref name="validate"/> says what is
    /// wrong with a text (null when it is fine); OK - labelled <paramref name="okText"/> - waits until it is fine.
    /// </summary>
    public static string? Show(string question, string? initial = null, string okText = "OK", Func<string, string?>? validate = null)
    {
        var dialog = new InputDialog(question, initial, okText, validate)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true && dialog.Value.Length > 0 ? dialog.Value : null;
    }

    private void Text_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) Check();
    }

    private void Check()
    {
        var problem = Value.Length == 0 ? null : _validate?.Invoke(Value);
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = Value.Length > 0 && problem is null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
