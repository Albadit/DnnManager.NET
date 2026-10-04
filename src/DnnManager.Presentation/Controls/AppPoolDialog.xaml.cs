using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Edits a site's app pool: its .NET runtime, pipeline, 32-bit mode, identity, idle time-out and start mode. A specific user
/// account is kept as it is - its password isn't known here.
/// </summary>
public partial class AppPoolDialog : Window
{
    // IIS takes an idle time-out of up to 30 days.
    private const int MaxIdleMinutes = 43200;

    private readonly IisPoolSettings _current;
    private readonly string _idleText;
    private readonly bool _ready;

    private AppPoolDialog(string site, string poolName, IisPoolSettings current)
    {
        _current = current;
        InitializeComponent();
        ThemeManager.Track(this);
        Intro.Text = $"The settings of '{poolName}', the app pool of '{site}'. Saving recycles the pool.";
        Fill(RuntimeBox, current.Runtime, ("v4.0", "v4.0"), ("No Managed Code", ""));
        Fill(PipelineBox, current.Pipeline, ("Integrated", "Integrated"), ("Classic", "Classic"));
        Enable32BitBox.IsChecked = current.Enable32Bit;
        string[] identities = ["ApplicationPoolIdentity", "NetworkService", "LocalService", "LocalSystem"];
        if (identities.Any(i => Same(i, current.Identity)))
        {
            Fill(IdentityBox, current.Identity, identities.Select(i => (i, i)).ToArray());
            IdentityHint.Text = "Another identity needs access to the site's folder (granted when saved) and, with Windows authentication, to its database.";
        }
        else
        {
            // A specific account needs its password to be set again, which this dialog doesn't ask for - so it stays.
            var text = Same(current.Identity, "SpecificUser") ? "Specific user (kept)" : $"{current.Identity} (kept)";
            Fill(IdentityBox, current.Identity, (text, current.Identity));
            IdentityBox.IsEnabled = false;
            IdentityHint.Text = "A specific user account is changed in IIS Manager.";
        }
        Fill(StartModeBox, current.StartMode, ("OnDemand", "OnDemand"), ("AlwaysRunning", "AlwaysRunning"));
        _idleText = ((long)Math.Round(current.IdleTimeout.TotalMinutes)).ToString();
        IdleBox.Text = _idleText;
        _ready = true;
        Check();
        Loaded += (_, _) => RuntimeBox.Focus();
    }

    /// <summary>The settings as they are to be, or null when cancelled.</summary>
    public static IisPoolSettings? Show(string site, string poolName, IisPoolSettings current)
    {
        var dialog = new AppPoolDialog(site, poolName, current)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Chosen() : null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Offers the <paramref name="choices"/> (text shown, value meant) and selects <paramref name="current"/> - added as a
    /// choice of its own when IIS has a value that isn't among them, so opening and saving the dialog doesn't change it.
    /// </summary>
    private static void Fill(ComboBox box, string current, params (string Text, string Value)[] choices)
    {
        foreach (var (text, value) in choices) box.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        var selected = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Same((string)i.Tag, current));
        if (selected is null)
        {
            selected = new ComboBoxItem { Content = current, Tag = current };
            box.Items.Add(selected);
        }
        box.SelectedItem = selected;
    }

    private static string Value(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>The idle time-out typed, or null when it isn't a whole number of minutes in range.</summary>
    private TimeSpan? IdleTimeout()
    {
        // Left as it was, it is the current value exactly - which can have seconds the box rounds away.
        var text = IdleBox.Text.Trim();
        if (text == _idleText) return _current.IdleTimeout;
        return int.TryParse(text, out var minutes) && minutes is >= 0 and <= MaxIdleMinutes ? TimeSpan.FromMinutes(minutes) : null;
    }

    private IisPoolSettings Chosen() => new(
        Value(RuntimeBox),
        Value(PipelineBox),
        Enable32BitBox.IsChecked == true,
        Value(IdentityBox),
        IdleTimeout() ?? _current.IdleTimeout,
        Value(StartModeBox));

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        // The boxes raise their events while they are being filled.
        if (_ready) Check();
    }

    private void Check()
    {
        var problem = IdleTimeout() is null
            ? $"The idle time-out is a whole number of minutes from 0 (never) to {MaxIdleMinutes} (30 days)."
            : null;
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        // Saving the settings as they are would recycle the pool for nothing.
        var chosen = Chosen();
        var changed = !Same(chosen.Runtime, _current.Runtime) || !Same(chosen.Pipeline, _current.Pipeline)
                      || chosen.Enable32Bit != _current.Enable32Bit || !Same(chosen.Identity, _current.Identity)
                      || chosen.IdleTimeout != _current.IdleTimeout || !Same(chosen.StartMode, _current.StartMode);
        OkButton.IsEnabled = problem is null && changed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
