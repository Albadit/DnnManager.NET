using Avalonia.Controls;
using Avalonia.Interactivity;
using DnnManager.Application.Deployment;
using DnnManager.Desktop.Services;

namespace DnnManager.Desktop.Views;

/// <summary>
/// Resolves a text conflict by hand: the three-way merge of the project's version and the server's (from the baseline
/// when it was kept - otherwise every difference is a conflict), with git's markers to edit away. Saved only once no
/// marker is left, written the way the project's file is (encoding, byte order mark, line endings). As the Windows app's
/// Controls/MergeDialog.
/// </summary>
public partial class MergeDialog : Window
{
    private readonly MergeResult _merge = new([]);
    private readonly TextContent _format = TextContent.Empty;
    private readonly string _newline = Environment.NewLine;
    private byte[]? _result;

    /// <summary>For the designer only.</summary>
    public MergeDialog() => InitializeComponent();

    internal MergeDialog(string path, TextContent? origin, TextContent local, TextContent remote)
    {
        InitializeComponent();
        // Typed or set: the markers left counted again.
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Follow();
        };
        _format = local;
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

    /// <summary>The merged file's bytes - null until saved, and when cancelled.</summary>
    internal byte[]? Result => _result;

    /// <summary>The merged file's bytes - null when cancelled.</summary>
    public static async Task<byte[]?> ShowAsync(string path, byte[]? origin, byte[] local, byte[] remote)
    {
        var localText = TextContent.From(local);
        var remoteText = TextContent.From(remote);
        if (localText is null || remoteText is null)
        {
            await Dialogs.MessageAsync($"{path} isn't text - keep the project's version or take the server's.");
            return null;
        }
        var dialog = new MergeDialog(path, origin is null ? null : TextContent.From(origin), localText, remoteText);
        if (Dialogs.Owner is { IsVisible: true } owner) await dialog.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }
        return dialog._result;
    }

    private IReadOnlyList<string> Lines()
    {
        var lines = (Editor.Text ?? "").Replace("\r\n", "\n").Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private byte[] Bytes() => _format.Encode(Lines(), endsWithNewline: (Editor.Text ?? "").EndsWith('\n'));

    private void Show(IEnumerable<string> lines)
    {
        Editor.Text = string.Join(_newline, lines) + (_format.EndsWithNewline ? _newline : "");
        Follow();
    }

    private void Reset_Click(object? sender, RoutedEventArgs e) => Show(_merge.WithMarkers());

    private void TakeLocal_Click(object? sender, RoutedEventArgs e) => Show(_merge.Chunks.SelectMany(c => c.Conflict ? c.Local! : c.Lines));

    private void TakeRemote_Click(object? sender, RoutedEventArgs e) => Show(_merge.Chunks.SelectMany(c => c.Conflict ? c.Remote! : c.Lines));

    private void Editor_TextChanged(object? sender, TextChangedEventArgs e) => Follow();

    // Saved only once no marker is left - how many are, said under the editor.
    private void Follow()
    {
        if (OkButton is null) return;
        var lines = Lines();
        var left = lines.Count(l => l.StartsWith("<<<<<<< ", StringComparison.Ordinal));
        OkButton.IsEnabled = !MergeResult.HasMarkers(lines);
        Status.Text = OkButton.IsEnabled ? "No conflict left - ready to save." : $"{Math.Max(left, 1)} conflict marker(s) left.";
        Status.Foreground = Res.Brush(OkButton.IsEnabled ? "SuccessText" : "LogWarn");
    }

    internal void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled) return;
        _result = Bytes();
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
