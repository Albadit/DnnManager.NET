using System.Windows;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>A read-only list of label / value pairs, e.g. a project's details.</summary>
public partial class DetailsDialog : Window
{
    public sealed record Detail(string Label, string Value);

    private readonly IReadOnlyList<Detail> _details;

    private DetailsDialog(string heading, IReadOnlyList<Detail> details)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        _details = details;
        Heading.Text = heading;
        Rows.ItemsSource = details;
    }

    public static void Show(string heading, IReadOnlyList<Detail> details)
    {
        var dialog = new DetailsDialog(heading, details)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = Heading.Text + Environment.NewLine +
                   string.Join(Environment.NewLine, _details.Select(d => $"{d.Label}: {d.Value}"));
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Dialogs.Error($"Could not copy to the clipboard: {ex.Message}"); }
    }
}
