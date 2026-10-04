using System.Windows;
using System.Windows.Documents;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// What's new: the release notes of one or more versions, newest first (<see cref="ReleaseNotes"/>) - shown by the
/// first start of a newer version, and by the command palette's What's new.
/// </summary>
public partial class WhatsNewDialog : Window
{
    private WhatsNewDialog(IReadOnlyList<ReleaseNote> notes)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        if (notes.Count == 1) Title = $"What's new in DNN Manager {notes[0].Version}";
        var document = new FlowDocument
        {
            PagePadding = new Thickness(28, 22, 28, 22),
            FontFamily = FontFamily,
            FontSize = 13.5,
            LineHeight = 21,
            TextAlignment = TextAlignment.Left
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimary");
        document.SetResourceReference(FlowDocument.BackgroundProperty, "WindowBg");
        foreach (var note in notes) MarkdownDocument.Append(document, note.Markdown, note.LinkTarget);
        Viewer.Document = document;
    }

    /// <summary>Shows <paramref name="notes"/> - nothing when there are none (a build without them).</summary>
    public static void Show(IReadOnlyList<ReleaseNote> notes)
    {
        if (notes.Count == 0) return;
        var dialog = new WhatsNewDialog(notes)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }

    private void Releases_Click(object sender, RoutedEventArgs e) => Shell.Open($"{AppReleaseFeed.Repository}/releases");
}
