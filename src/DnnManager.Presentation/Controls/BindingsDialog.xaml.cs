using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Edits the host names and ports a site answers on over http. Every row remembers the binding it started as, so DNN's
/// portal alias of a changed host name can be renamed rather than a new one added; https and other bindings are only listed.
/// </summary>
public partial class BindingsDialog : Window
{
    private readonly List<Row> _rows = [];
    private readonly IReadOnlyList<(string Host, int Port)> _original;

    private BindingsDialog(string site, IReadOnlyList<(string Host, int Port)> httpBindings, IReadOnlyList<string> otherBindings)
    {
        _original = httpBindings;
        InitializeComponent();
        ThemeManager.Track(this);
        Intro.Text = $"The host names and ports '{site}' answers on over http. DNN's portal aliases follow: a changed host name's alias " +
                     "is renamed, a new one's is added.";
        OtherList.ItemsSource = otherBindings;
        OtherPanel.Visibility = otherBindings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (host, port) in httpBindings) AddRow(host, port, host, port);
        Check();
        Loaded += (_, _) => _rows.FirstOrDefault()?.Host.Focus();
    }

    /// <summary>
    /// The http bindings as they are to be, in order - each with the host and port it was (null for one added) - or null when
    /// cancelled. <paramref name="otherBindings"/> are shown as they are, e.g. "https *:443:www.example.com".
    /// </summary>
    public static IReadOnlyList<HttpBindingEdit>? Show(string site, IReadOnlyList<(string Host, int Port)> httpBindings, IReadOnlyList<string> otherBindings)
    {
        var dialog = new BindingsDialog(site, httpBindings, otherBindings)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Edits() : null;
    }

    /// <summary>One binding being edited, and what it was - null for one added here.</summary>
    private sealed record Row(string? OldHost, int? OldPort, TextBox Host, TextBox Port, FrameworkElement View)
    {
        public string HostName => Host.Text.Trim().ToLowerInvariant();
        public int? PortNumber => int.TryParse(Port.Text.Trim(), out var port) && port is > 0 and <= 65535 ? port : null;
    }

    private Row AddRow(string? oldHost, int? oldPort, string host, int port)
    {
        var hostBox = new TextBox { Text = host, Width = 260 };
        var portBox = new TextBox { Text = port.ToString(), Width = 70, Margin = new Thickness(8, 0, 0, 0) };
        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = FindResource("GlyphDelete"),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Remove this host name"
        };
        var view = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children = { hostBox, portBox, remove } };
        var row = new Row(oldHost, oldPort, hostBox, portBox, view);
        hostBox.TextChanged += (_, _) => Check();
        portBox.TextChanged += (_, _) => Check();
        remove.Click += (_, _) =>
        {
            _rows.Remove(row);
            RowsPanel.Children.Remove(view);
            Check();
        };
        _rows.Add(row);
        RowsPanel.Children.Add(view);
        return row;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // A new host name most likely goes on the port the site already uses.
        var row = AddRow(null, null, "", _rows.Select(r => r.PortNumber).FirstOrDefault(p => p is not null) ?? 80);
        Check();
        row.Host.Focus();
    }

    private IReadOnlyList<HttpBindingEdit> Edits() =>
        _rows.Select(r => new HttpBindingEdit(r.OldHost, r.OldPort, r.HostName, r.PortNumber ?? 0)).ToList();

    private void Check()
    {
        var problem = FindProblem();
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null && Changed();
    }

    /// <summary>
    /// False while every binding is still there, as it was and in its place: saving that would only recycle the site for nothing.
    /// </summary>
    private bool Changed()
    {
        var edits = Edits();
        return edits.Count != _original.Count
               || edits.Zip(_original).Any(p => p.First.OldHost is null
                                                || !string.Equals(p.First.Host, p.Second.Host, StringComparison.OrdinalIgnoreCase)
                                                || p.First.Port != p.Second.Port);
    }

    private string? FindProblem()
    {
        if (_rows.Count == 0) return "Keep at least one host name - the site needs an http binding.";
        var seen = new HashSet<(string, int)>();
        foreach (var row in _rows)
        {
            var host = row.HostName;
            var shown = host.Length == 0 ? "any host name" : $"'{host}'";
            if (host.Length > 0 && !IsHostName(host))
                return $"'{host}' isn't a host name: letters, digits, '-' and '.', each part 1 to 63 characters, not starting or ending with '-'.";
            if (row.PortNumber is not { } port) return $"The port of {shown} must be a number between 1 and 65535.";
            // IIS can't have the same binding twice.
            if (!seen.Add((host, port))) return $"{(host.Length == 0 ? "Any host name" : shown)} on port {port} is there twice.";
        }
        return null;
    }

    /// <summary>A DNS name: dot-separated labels of letters, digits and hyphens, at most 253 characters in all.</summary>
    private static bool IsHostName(string host) =>
        host.Length <= 253
        && host.Split('.').All(label => label.Length is > 0 and <= 63
                                        && label[0] != '-' && label[^1] != '-'
                                        && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
