using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Startup;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// "Troubleshoot" (the bug at the bottom of the sidebar) - opened over the page like Settings, laid out like Docker Desktop's: restart
/// DNN Manager, clean up the data it keeps in <c>Documents\DnnManager</c>, reset its settings to their defaults, or
/// reset it to factory defaults. Nothing here touches a project's IIS site, folder or database. Not while an operation
/// runs - it would be cut off.
/// </summary>
public partial class TroubleshootPage : UserControl
{
    private readonly OperationRunner _runner;
    private readonly AppDataCleaner _cleaner;
    private readonly SettingsStore _store;
    private readonly ISecretStore _secrets;
    private readonly StartupTask _startup;
    private readonly AppDataPaths _paths;
    private readonly LiveSettings _live;
    private readonly TerminalService _terminal;
    private readonly WorkspaceService _workspace;
    // Which measuring the shown sizes belong to - an older one that finishes late is dropped.
    private int _measuring;

    public TroubleshootPage(OperationRunner runner, AppDataCleaner cleaner, SettingsStore store, ISecretStore secrets,
        StartupTask startup, AppDataPaths paths, LiveSettings live, TerminalService terminal, WorkspaceService workspace)
    {
        _runner = runner; _cleaner = cleaner; _store = store; _secrets = secrets; _startup = startup; _paths = paths;
        _live = live; _terminal = terminal; _workspace = workspace;
        InitializeComponent();
        // Focused when shown, so Esc reaches it - the title bar's button that opened it doesn't take the focus.
        Focusable = true;
        FocusVisualStyle = null;
        Loaded += (_, _) => Focus();
        // Logs and packages are DNN Manager's own; project backups are the user's - never ticked for them.
        CleanLogs.IsChecked = CleanPackages.IsChecked = true;
        // Sizes change while the app runs (a new log line, a kept package) - measured again each time the page is shown.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Measure();
        };
        // Followed only while the page is shown: a page MainWindow lets go of isn't kept alive by the runner.
        Loaded += (_, _) =>
        {
            _runner.PropertyChanged += OnRunnerChanged;
            ShowBusy();
        };
        Unloaded += (_, _) => _runner.PropertyChanged -= OnRunnerChanged;
        ShowBusy();
    }

    /// <summary>Close was pressed - the window goes back to the page Troubleshoot was opened from.</summary>
    public event EventHandler? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // Esc: back to the page Troubleshoot was opened from.
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != System.Windows.Input.Key.Escape) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnRunnerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationRunner.Current)) ShowBusy();
    }

    // ─── Restart ──────────────────────────────────────────────────────────

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        AppRestart.Restart();
    }

    // ─── Clean up data ────────────────────────────────────────────────────

    private (CheckBox Box, AppDataKind Kind, string Name)[] Choices =>
    [
        (CleanLogs, AppDataKind.Logs, "Logs"),
        (CleanPackages, AppDataKind.DnnPackages, "Kept DNN packages"),

        (CleanProjectBackups, AppDataKind.ProjectBackups, "Project backups")
    ];

    /// <summary>Shows each kind of data with what it takes now - measured off the UI thread (backups can be large).</summary>
    private async void Measure()
    {
        var measuring = ++_measuring;
        foreach (var (box, _, name) in Choices) box.Content = $"{name} (measuring…)";
        var sizes = await Task.Run(() => Choices.Select(c => _cleaner.Measure(c.Kind)).ToArray());
        if (measuring != _measuring) return;
        var details = new[]
        {
            "the activity log, one file per day",
            "downloaded DNN install packages, kept to install the same version again without downloading",

            "every project's backups"
        };
        for (var i = 0; i < sizes.Length; i++)
            Choices[i].Box.Content = $"{Choices[i].Name} - {details[i]} ({ByteSize.Format(sizes[i])})";
        Selection_Changed(this, new RoutedEventArgs());
    }

    private void Selection_Changed(object sender, RoutedEventArgs e) =>
        CleanupButton.IsEnabled = !_runner.IsBusy && Choices.Any(c => c.Box.IsChecked == true);

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var chosen = Choices.Where(c => c.Box.IsChecked == true).ToList();
        if (chosen.Count == 0) return;
        var list = string.Join(Environment.NewLine, chosen.Select(c => $"• {c.Name}"));
        var backups = chosen.Any(c => c.Kind == AppDataKind.ProjectBackups)
            ? $"{Environment.NewLine}{Environment.NewLine}Project backups can't be brought back."
            : "";
        if (!Dialogs.ConfirmDanger($"Delete this data from {_paths.Root}?{Environment.NewLine}{Environment.NewLine}{list}{backups}",
                "Clean up data", "Cancel"))
            return;

        CleanupButton.IsEnabled = false;
        var results = await Task.Run(() => chosen.Select(c => _cleaner.Clean(c.Kind)).ToList());
        var freed = results.Sum(r => r.FreedBytes);
        var skipped = results.Sum(r => r.Skipped);
        Toast.Show(skipped == 0
                ? $"Cleaned up {ByteSize.Format(freed)}."
                : $"Cleaned up {ByteSize.Format(freed)} - {skipped} file(s) are in use and were left.",
            skipped == 0 ? ToastKind.Success : ToastKind.Warning);
        Measure();
    }

    // ─── Reset settings to defaults ───────────────────────────────────────

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var nl = Environment.NewLine;
        if (!Dialogs.ConfirmDanger(
                $"Put every setting back to its default, as DNN Manager is installed?{nl}{nl}" +
                $"The current settings aren't kept. The saved DNN host password and database " +
                $"server login go too, and DNN Manager stops starting at sign-in. The defaults apply at once - no restart.{nl}{nl}" +
                "Kept: your projects, the logs, the kept DNN packages and which sites are kept warm.",
                "Reset settings", "Cancel"))
            return;

        ResetSettingsButton.IsEnabled = false;
        try
        {
            _store.ResetToDefaults();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SettingsException)
        {
            Dialogs.Error($"The settings could not be reset: {ex.Message}");
            ResetSettingsButton.IsEnabled = !_runner.IsBusy;
            return;
        }
        var problems = await ForgetSecretsAndStartAsync();

        // Put the defaults to work, as Settings' Save does.
        var settings = new UserSettings();
        _live.Apply(settings);
        ThemeManager.Initialize(settings.Appearance.Theme);
        ThemeManager.ApplyLayout(settings.Appearance.UiScale, settings.Appearance.FontSize);
        Motion.Apply(settings.Appearance.Animations);
        _terminal.Apply(settings.Terminal);
        ResetSettingsButton.IsEnabled = !_runner.IsBusy;

        if (problems.Count > 0)
            Dialogs.Error("The settings are reset, except:" + nl + nl + string.Join(nl, problems.Select(p => $"• {p}")));
        else
            Toast.Show("The settings are back to their defaults.", ToastKind.Success);
    }

    /// <summary>Removes the saved passwords and starting at sign-in - what went wrong, if anything.</summary>
    private async Task<List<string>> ForgetSecretsAndStartAsync()
    {
        var problems = new List<string>();
        foreach (var secret in new[] { SecretNames.DefaultHostPassword, SecretNames.DatabaseServerPassword })
            if (_secrets.Delete(secret) is { Success: false } deleted) problems.Add(deleted.Error!);
        if (await _startup.GetTargetAsync() is not null && await _startup.DisableAsync() is { Success: false } disabled)
            problems.Add($"Starting at sign-in: {disabled.Error}");
        return problems;
    }

    // ─── Reset to factory defaults ────────────────────────────────────────

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var nl = Environment.NewLine;
        if (!Dialogs.ConfirmDanger(
                $"Reset DNN Manager to factory defaults?{nl}{nl}" +
                $"Removed: the settings, the saved passwords (the DNN host " +
                $"password and the database server login's), starting at sign-in, which sites are kept warm, the logs, the " +
                $"kept DNN packages, and the remembered workspace (the window, the open page, unsaved form values).{nl}{nl}" +
                $"Kept: your projects - their IIS sites, folders and databases - and their backups.{nl}{nl}" +
                "DNN Manager restarts afterwards.",
                "Reset to factory defaults", "Cancel"))
            return;

        ResetButton.IsEnabled = false;
        try
        {
            _store.ResetToDefaults();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SettingsException)
        {
            // Without the settings reset the rest is pointless - stop before anything else is removed.
            Dialogs.Error($"The settings could not be reset: {ex.Message}");
            ResetButton.IsEnabled = true;
            return;
        }
        var problems = await ForgetSecretsAndStartAsync();
        await Task.Run(() =>
        {
            _cleaner.Clean(AppDataKind.Logs);
            _cleaner.Clean(AppDataKind.DnnPackages);
            _cleaner.Clean(AppDataKind.KeepWarmChoices);
        });

        // Where the user was, the window, unsaved form values: the next start opens as a first one.
        _workspace.Forget();

        if (problems.Count > 0)
            Dialogs.Error("DNN Manager is reset, except:" + nl + nl + string.Join(nl, problems.Select(p => $"• {p}")));
        // The running app still has the old settings in memory - it starts again with the defaults.
        if (AppRestart.Restart()) return;
        ResetButton.IsEnabled = true;
        _workspace.Resume();
    }

    // ─── Not while an operation runs ──────────────────────────────────────

    private bool Busy()
    {
        if (!_runner.IsBusy) return false;
        Toast.Show($"'{_runner.Current}' is still running - wait until it has finished.", ToastKind.Warning);
        return true;
    }

    private void ShowBusy()
    {
        var busy = _runner.IsBusy;
        RestartButton.IsEnabled = ResetSettingsButton.IsEnabled = ResetButton.IsEnabled = !busy;
        Selection_Changed(this, new RoutedEventArgs());
        BusyHint.Text = busy ? $"'{_runner.Current}' is running - these wait until it has finished." : "";
        BusyHint.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
