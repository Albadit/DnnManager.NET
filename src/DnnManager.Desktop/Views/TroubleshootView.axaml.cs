using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Desktop.Services;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Startup;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Desktop.Views;

/// <summary>
/// Troubleshoot - opened over the page like Settings, laid out like Docker Desktop's: restart DNN Manager, clean up the
/// data it keeps in its data folder, reset its settings to their defaults, or reset it to factory defaults. Nothing
/// here touches a project's IIS site, folder or database. Not while an operation runs - it would be cut off. The
/// Windows app's Pages/TroubleshootPage.
/// </summary>
public partial class TroubleshootView : UserControl
{
    private readonly IServiceProvider _services = Program.Services;
    private readonly OperationRunner _runner;
    private readonly AppDataCleaner _cleaner;
    private readonly SettingsStore _store;
    private readonly ISecretStore _secrets;
    private readonly StartupTask _startup;
    private readonly AppDataPaths _paths;
    private readonly LiveSettings _live;
    // Which measuring the shown sizes belong to - an older one that finishes late is dropped.
    private int _measuring;

    public TroubleshootView()
    {
        _runner = _services.GetRequiredService<OperationRunner>();
        _cleaner = _services.GetRequiredService<AppDataCleaner>();
        _store = _services.GetRequiredService<SettingsStore>();
        _secrets = _services.GetRequiredService<ISecretStore>();
        _startup = _services.GetRequiredService<StartupTask>();
        _paths = _services.GetRequiredService<AppDataPaths>();
        _live = _services.GetRequiredService<LiveSettings>();
        InitializeComponent();
        CleanupHint.Text = $"The data selected below is removed from {_paths.Root}.";
        // Logs and packages are DNN Manager's own; project backups are the user's - never ticked for them.
        CleanLogs.IsChecked = CleanPackages.IsChecked = true;
        foreach (var (box, _, _) in Choices) box.IsCheckedChanged += (_, _) => Selection_Changed();
        // Sizes change while the app runs (a new log line, a kept package) - measured again each time it is shown;
        // the runner followed only while shown, so a view MainWindow lets go of isn't kept alive by it.
        AttachedToVisualTree += (_, _) =>
        {
            _runner.PropertyChanged += OnRunnerChanged;
            ShowBusy();
            Measure();
            Focus();
        };
        DetachedFromVisualTree += (_, _) => _runner.PropertyChanged -= OnRunnerChanged;
        ShowBusy();
    }

    /// <summary>Close (or Esc) was pressed - the window goes back to the page Troubleshoot was opened from.</summary>
    public event EventHandler? CloseRequested;

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperationRunner.Current) or nameof(OperationRunner.IsBusy)) Dispatcher.UIThread.Post(ShowBusy);
    }

    // ─── Restart ──────────────────────────────────────────────────────────

    private async void Restart_Click(object? sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        await AppRestart.RestartAsync();
    }

    // ─── Clean up data ────────────────────────────────────────────────────

    private (CheckBox Box, AppDataKind Kind, string Name)[] Choices =>
    [
        (CleanLogs, AppDataKind.Logs, "Logs"),
        (CleanPackages, AppDataKind.DnnPackages, "Kept DNN packages"),
        (CleanProjectBackups, AppDataKind.ProjectBackups, "Project backups"),
        (CleanDeployments, AppDataKind.Deployments, "Deployment packages"),
        (CleanOldSettings, AppDataKind.OldSettingsFiles, "Old settings files")
    ];

    /// <summary>Shows each kind of data with what it takes now - measured off the UI thread (backups can be large).</summary>
    private async void Measure()
    {
        var measuring = ++_measuring;
        var choices = Choices;
        foreach (var (box, _, name) in choices) box.Content = $"{name} (measuring…)";
        long[] sizes;
        try
        {
            sizes = await Task.Run(() => choices.Select(c => _cleaner.Measure(c.Kind)).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (measuring == _measuring) foreach (var (box, _, name) in choices) box.Content = name;
            return;
        }
        if (measuring != _measuring) return;
        var details = new[]
        {
            "one file per day",
            "to install a version again offline",
            "every project's .zip and .bacpac",
            "made by Export for deployment",
            "left by versions before 1.8"
        };
        for (var i = 0; i < sizes.Length; i++)
            choices[i].Box.Content = $"{choices[i].Name} - {details[i]} ({ByteSize.Format(sizes[i])})";
        Selection_Changed();
    }

    private void Selection_Changed() => CleanupButton.IsEnabled = !_runner.IsBusy && Choices.Any(c => c.Box.IsChecked == true);

    private async void Cleanup_Click(object? sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var chosen = Choices.Where(c => c.Box.IsChecked == true).ToList();
        if (chosen.Count == 0) return;
        var nl = Environment.NewLine;
        var list = string.Join(nl, chosen.Select(c => $"• {c.Name}"));
        var backups = chosen.Any(c => c.Kind is AppDataKind.ProjectBackups or AppDataKind.Deployments)
            ? $"{nl}{nl}Backups and deployment packages can't be brought back."
            : "";
        if (!await Dialogs.ConfirmAsync($"Delete this data from {_paths.Root}?{nl}{nl}{list}{backups}", "Clean up data", "Cancel", danger: true))
            return;

        CleanupButton.IsEnabled = false;
        try
        {
            var results = await Task.Run(() => chosen.Select(c => _cleaner.Clean(c.Kind)).ToList());
            var freed = results.Sum(r => r.FreedBytes);
            var skipped = results.Sum(r => r.Skipped);
            Tell(skipped == 0
                ? $"Cleaned up {ByteSize.Format(freed)}."
                : $"Cleaned up {ByteSize.Format(freed)} - {skipped} file(s) are in use and were left.", skipped == 0 ? ToastKind.Success : ToastKind.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync($"The data could not be cleaned up: {ex.Message}");
        }
        Measure();
    }

    // ─── Reset settings to defaults ───────────────────────────────────────

    private async void ResetSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var nl = Environment.NewLine;
        if (!await Dialogs.ConfirmAsync(
                $"Put every setting back to its default, as DNN Manager is installed?{nl}{nl}" +
                "The current settings aren't kept. The saved DNN host password and database server login go too" +
                (Platform.Supports(PlatformFeature.StartWithSystem) ? ", and DNN Manager stops starting at sign-in" : "") +
                $". The defaults apply at once - no restart.{nl}{nl}" +
                "Kept: your projects, the logs, the kept DNN packages and which sites are kept warm.",
                "Reset settings", "Cancel", danger: true))
            return;

        ResetSettingsButton.IsEnabled = false;
        try
        {
            _store.ResetToDefaults();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SettingsException)
        {
            await Dialogs.MessageAsync($"The settings could not be reset: {ex.Message}");
            ResetSettingsButton.IsEnabled = !_runner.IsBusy;
            return;
        }
        var problems = await ForgetSecretsAndStartAsync();

        // Put the defaults to work, as Settings' Save does.
        var settings = new UserSettings();
        _live.Apply(settings);
        App.ApplyTheme(settings.Appearance.Theme);
        if (AppearanceLayout.MainWindow is { } window) AppearanceLayout.Apply(window, settings.Appearance);
        ResetSettingsButton.IsEnabled = !_runner.IsBusy;

        if (problems.Count > 0)
            await Dialogs.MessageAsync("The settings are reset, except:" + nl + nl + string.Join(nl, problems.Select(p => $"• {p}")));
        else
            Tell("The settings are back to their defaults.");
    }

    /// <summary>Removes the saved passwords and starting at sign-in - what went wrong, if anything.</summary>
    private async Task<List<string>> ForgetSecretsAndStartAsync()
    {
        var problems = new List<string>();
        foreach (var secret in new[] { SecretNames.DefaultHostPassword, SecretNames.DatabaseServerPassword })
        {
            try
            {
                if (_secrets.Delete(secret) is { Success: false } deleted) problems.Add(deleted.Error!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add(ex.Message);
            }
        }
        if (Platform.Supports(PlatformFeature.StartWithSystem))
        {
            try
            {
                if (await _startup.GetTargetAsync() is not null && await _startup.DisableAsync() is { Success: false } disabled)
                    problems.Add($"Starting at sign-in: {disabled.Error}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                problems.Add($"Starting at sign-in: {ex.Message}");
            }
        }
        return problems;
    }

    // ─── Reset to factory defaults ────────────────────────────────────────

    private async void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var nl = Environment.NewLine;
        if (!await Dialogs.ConfirmAsync(
                $"Reset DNN Manager to factory defaults?{nl}{nl}" +
                "Removed: the settings, the saved passwords (the DNN host password and the database server login's), " +
                (Platform.Supports(PlatformFeature.StartWithSystem) ? "starting at sign-in, " : "") +
                $"which sites are kept warm, the logs, the kept DNN packages and older versions' settings files.{nl}{nl}" +
                $"Kept: your projects - their IIS sites, folders and databases - their backups, and your Source Control servers.{nl}{nl}" +
                "DNN Manager restarts afterwards.",
                "Reset to factory defaults", "Cancel", danger: true))
            return;

        ResetButton.IsEnabled = false;
        try
        {
            _store.ResetToDefaults();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SettingsException)
        {
            // Without the settings reset the rest is pointless - stop before anything else is removed.
            await Dialogs.MessageAsync($"The settings could not be reset: {ex.Message}");
            ResetButton.IsEnabled = true;
            return;
        }
        var problems = await ForgetSecretsAndStartAsync();
        await Task.Run(() =>
        {
            foreach (var kind in new[] { AppDataKind.Logs, AppDataKind.DnnPackages, AppDataKind.KeepWarmChoices, AppDataKind.OldSettingsFiles })
            {
                try { _cleaner.Clean(kind); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add(ex.Message); }
            }
        });

        // Where the user was, the window, unsaved form values: the next start opens as a first one.
        var workspace = _services.GetService<WorkspaceService>();
        workspace?.Forget();

        if (problems.Count > 0)
            await Dialogs.MessageAsync("DNN Manager is reset, except:" + nl + nl + string.Join(nl, problems.Select(p => $"• {p}")));
        // The running app still has the old settings in memory - it starts again with the defaults.
        if (await AppRestart.RestartAsync()) return;
        ResetButton.IsEnabled = true;
        workspace?.Resume();
    }

    // ─── Not while an operation runs ──────────────────────────────────────

    private bool Busy()
    {
        if (!_runner.IsBusy) return false;
        Tell($"'{_runner.Current}' is still running - wait until it has finished.", ToastKind.Warning);
        return true;
    }

    private void ShowBusy()
    {
        var busy = _runner.IsBusy;
        RestartButton.IsEnabled = ResetSettingsButton.IsEnabled = ResetButton.IsEnabled = !busy;
        Selection_Changed();
        BusyHint.Text = busy ? $"'{_runner.Current ?? "An operation"}' is running - these wait until it has finished." : "";
        BusyHint.IsVisible = busy;
    }

    /// <summary>What happened - a toast, as the Windows app's.</summary>
    private static void Tell(string text, ToastKind kind = ToastKind.Success) => Toast.Show(text, kind);
}
