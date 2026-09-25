using System.Windows;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>A one-line text prompt.</summary>
public partial class InputDialog : Window
{
    private InputDialog(string question, string? initial)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        Question.Text = question;
        Text.Text = initial ?? string.Empty;
        Loaded += (_, _) => { Text.Focus(); Text.SelectAll(); };
    }

    private string Value => Text.Text;

    /// <summary>Returns the entered text, or null when cancelled or left blank.</summary>
    public static string? Show(string question, string? initial = null)
    {
        var dialog = new InputDialog(question, initial)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value) ? dialog.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
