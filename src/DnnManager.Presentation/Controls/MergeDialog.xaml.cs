using System.Windows;
using DnnManager.Application.Deployment;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Resolves a text conflict by hand: the three-way merge of the project's version and the server's (from the baseline
/// when it was kept - otherwise every difference is a conflict), with git's markers to edit away. Saved only once no
/// marker is left, written the way the project's file is (encoding, byte order mark, line endings).
/// </summary>
public partial class MergeDialog : Window
{
    private readonly MergeResult _merge;
    private readonly TextContent _format;
    private readonly string _newline;

    internal MergeDialog(string path, TextContent? origin, TextContent local, TextContent remote)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        _format = local;
        _newline = Environment.NewLine;
        _merge = ThreeWayMerge.Merge(origin?.Lines, local.Lines, remote.Lines);
        Title = $"Merge {DeployPath.Name(path)}";
        Heading.Text = $"{path} changed in the project and on the server";
        Explanation.Text = (origin is null
                               ? "The version both started from wasn't kept, so every difference is shown as a conflict. "
                               : "What only one side changed is merged in already. ") +
                           $"{_merge.ConflictCount} conflict(s) are marked: the project's lines between <<<<<<< and =======, the server's between " +
                           "======= and >>>>>>>. Edit each into what it should be and remove the markers.";
        Reset_Click(this, new RoutedEventArgs());
    }

    /// <summary>The merged file's bytes - null when cancelled.</summary>
    public static byte[]? Show(string path, byte[]? origin, byte[] local, byte[] remote)
    {
        var localText = TextContent.From(local);
        var remoteText = TextContent.From(remote);
        if (localText is null || remoteText is null)
        {
            Dialogs.Error($"{path} isn't text - keep the project's version or take the server's.");
            return null;
        }
        var dialog = new MergeDialog(path, origin is null ? null : TextContent.From(origin), localText, remoteText)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Bytes() : null;
    }

    private IReadOnlyList<string> Lines() => Editor.Text.Replace("\r\n", "\n").Split('\n') is var lines && lines.Length > 0 && lines[^1].Length == 0
        ? lines[..^1] : Editor.Text.Replace("\r\n", "\n").Split('\n');

    private byte[] Bytes() => _format.Encode(Lines(), endsWithNewline: Editor.Text.EndsWith('\n'));

    private void Show(IEnumerable<string> lines) => Editor.Text = string.Join(_newline, lines) + (_format.EndsWithNewline ? _newline : "");

    private void Reset_Click(object sender, RoutedEventArgs e) => Show(_merge.WithMarkers());

    private void TakeLocal_Click(object sender, RoutedEventArgs e) => Show(_merge.Chunks.SelectMany(c => c.Conflict ? c.Local! : c.Lines));

    private void TakeRemote_Click(object sender, RoutedEventArgs e) => Show(_merge.Chunks.SelectMany(c => c.Conflict ? c.Remote! : c.Lines));

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var left = Lines().Count(l => l.StartsWith("<<<<<<< ", StringComparison.Ordinal));
        OkButton.IsEnabled = !MergeResult.HasMarkers(Lines());
        Status.Text = OkButton.IsEnabled ? "No conflict left - ready to save." : $"{Math.Max(left, 1)} conflict marker(s) left.";
        Status.Foreground = (System.Windows.Media.Brush)FindResource(OkButton.IsEnabled ? "SuccessText" : "LogWarn");
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
