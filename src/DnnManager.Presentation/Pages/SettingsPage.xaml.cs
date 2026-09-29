using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// Edits the settings in <c>settings.json</c>. Nothing is saved until <b>Save and restart</b>: every service reads its
/// options once at startup, so saving restarts DNN Manager to apply them. <b>Discard changes</b> puts the saved values back.
/// </summary>
public partial class SettingsPage : UserControl
{
    // Form labels for the settings keys, so a problem reads "Site port must be…" rather than "projects.sitePort must be…".
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["projects.baseDirectory"] = "Projects folder",
        ["projects.sitePort"] = "Site port",
        ["projects.hostnameSuffix"] = "Hostname suffix",
        ["projects.dnnReleaseSources"] = "DNN repositories",
        ["sqlServer.host"] = "Server host",
        ["sqlServer.port"] = "Port",
        ["sqlServer.saPassword"] = "SA password",
        ["docker.containerName"] = "Container name",
        ["docker.volumeName"] = "Volume name",
        ["docker.edition"] = "Edition",
        ["docker.collation"] = "Collation",
    };

    // What the running app was started with - to tell whether the file has changes still to apply.
    private readonly AppOptions _running;
    private readonly OperationRunner _runner;
    private readonly SettingsStore _store;
    // Set while the form is being filled in, so that doesn't count as an edit.
    private bool _loading;
    // The form has edits that aren't saved yet.
    private bool _dirty;

    public SettingsPage(IOptions<AppOptions> running, OperationRunner runner, SettingsStore store, AppDataPaths paths)
    {
        _running = running.Value;
        _runner = runner;
        _store = store;
        InitializeComponent();
        Subtitle.Text = $"Stored in {_store.FilePath}. Save applies the changes by restarting DNN Manager.";
        KeepDnnPackagesHint.Text = $"Saved in {paths.PackagesDirectory} and used again when a new project picks the same " +
                                   "version - no download. Off: each new project downloads its package and deletes it after installing.";

        foreach (var box in new[] { BaseDirectory, SitePort, HostnameSuffix, ReleaseApis, ContainerName, ContainerIp,
                                    VolumeName, DefaultPort, Collation, MssqlPid })
            box.TextChanged += (_, _) => Edited();
        SaPassword.PasswordChanged += (_, _) => Edited();
        foreach (var box in new[] { SsmsRememberPassword, KeepDnnPackages })
        {
            box.Checked += (_, _) => Edited();
            box.Unchecked += (_, _) => Edited();
        }

        ShowEnvironmentOverrides();
        Load();
    }

    private void Load()
    {
        UserSettings saved;
        try
        {
            saved = _store.Read();
        }
        catch (SettingsException ex)
        {
            // Edited outside the app since it started - say what's wrong rather than overwrite it.
            Form.IsEnabled = false;
            SetDirty(false);
            ShowError(string.Join(Environment.NewLine, [ex.Message, .. ex.Problems, "Fix the file, then open this page again."]));
            return;
        }

        _loading = true;
        var p = saved.Projects;
        var sql = saved.SqlServer;
        var docker = saved.Docker;
        BaseDirectory.Text = p.BaseDirectory;
        SitePort.Text = p.SitePort.ToString();
        HostnameSuffix.Text = p.HostnameSuffix;
        ReleaseApis.Text = string.Join(Environment.NewLine, p.DnnReleaseSources);
        KeepDnnPackages.IsChecked = p.KeepDnnPackages;
        ContainerIp.Text = sql.Host;
        DefaultPort.Text = sql.Port.ToString();
        SaPassword.Password = sql.SaPassword;
        ContainerName.Text = docker.ContainerName;
        VolumeName.Text = docker.VolumeName;
        MssqlPid.Text = docker.Edition;
        Collation.Text = docker.Collation;
        SsmsRememberPassword.IsChecked = saved.Ssms.RememberPassword;
        _loading = false;

        ShowError(null);
        SetDirty(false);
        UpdateRestartBanner(saved);
    }

    private void Edited()
    {
        if (_loading || !Form.IsEnabled) return;
        SetDirty(true);
    }

    /// <summary>The form has edits that aren't saved - leaving the page would lose them.</summary>
    public bool HasUnsavedChanges => _dirty;

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        // The Save bar appears with the first edit and goes away once saved or discarded.
        SaveBar.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = dirty;
        DiscardButton.IsEnabled = dirty;
        StatusText.Text = "You have unsaved changes.";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "LogWarn");
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Discard_Click(object sender, RoutedEventArgs e) => Load();

    /// <summary>Checks the form, saves it and restarts DNN Manager so the new settings apply.</summary>
    private void Save()
    {
        if (_runner.IsBusy)
        {
            Toast.Show($"'{_runner.Current}' is still running - save once it has finished (saving restarts DNN Manager).",
                ToastKind.Warning);
            return;
        }

        UserSettings settings;
        try
        {
            // Start from the file, so keys this page doesn't show (theme, IIS features…) stay as they are.
            settings = _store.Read();
        }
        catch (SettingsException ex)
        {
            ShowError($"Not saved - {ex.Message}");
            Toast.Show($"Settings not saved - {ex.Message}", ToastKind.Error);
            return;
        }

        var error = ApplyForm(settings);
        if (error is null)
        {
            var problem = settings.Validate().FirstOrDefault();
            if (problem is not null)
                error = $"{Labels.GetValueOrDefault(problem.Key, problem.Key)} {problem.Message}";
        }
        if (error is not null)
        {
            ShowError(error);
            Toast.Show($"Not saved: {error}", ToastKind.Warning);
            return;
        }

        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not save {_store.FilePath}: {ex.Message}");
            Toast.Show($"Could not save the settings: {ex.Message}", ToastKind.Error);
            return;
        }

        SetDirty(false);
        Restart();
    }

    /// <summary>Copies the form into <paramref name="settings"/>, or returns the first value that isn't a number where one is needed.</summary>
    private string? ApplyForm(UserSettings settings)
    {
        if (!int.TryParse(SitePort.Text.Trim(), out var sitePort))
            return "Site port must be a number between 1 and 65535.";
        if (!int.TryParse(DefaultPort.Text.Trim(), out var sqlPort))
            return "Port must be a number between 1 and 65535.";

        var p = settings.Projects;
        p.BaseDirectory = BaseDirectory.Text.Trim();
        p.SitePort = sitePort;
        p.HostnameSuffix = HostnameSuffix.Text.Trim().Trim('.');
        p.DnnReleaseSources = ReleaseApis.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        p.KeepDnnPackages = KeepDnnPackages.IsChecked == true;

        var sql = settings.SqlServer;
        sql.Host = ContainerIp.Text.Trim();
        sql.Port = sqlPort;
        sql.SaPassword = SaPassword.Password;

        var docker = settings.Docker;
        docker.ContainerName = ContainerName.Text.Trim();
        docker.VolumeName = VolumeName.Text.Trim();
        docker.Edition = MssqlPid.Text.Trim();
        docker.Collation = Collation.Text.Trim();

        settings.Ssms.RememberPassword = SsmsRememberPassword.IsChecked == true;
        return null;
    }

    private void UpdateRestartBanner(UserSettings saved) =>
        RestartBanner.Visibility = Snapshot(saved.ToAppOptions()) == Snapshot(_running) ? Visibility.Collapsed : Visibility.Visible;

    private void Restart_Click(object sender, RoutedEventArgs e) => Restart();

    private void BrowseBaseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Projects folder" };
        if (Directory.Exists(BaseDirectory.Text)) dialog.InitialDirectory = BaseDirectory.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) BaseDirectory.Text = dialog.FolderName;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e) => SettingsStartup.OpenInEditor(_store.FilePath);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(_store.FilePath)!;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error($"Could not open {folder}: {ex.Message}"); }
    }

    private void Preview_Changed(object sender, TextChangedEventArgs e)
    {
        if (UrlPreview is null) return; // raised during InitializeComponent
        var suffix = HostnameSuffix.Text.Trim().Trim('.');
        var port = int.TryParse(SitePort.Text.Trim(), out var p) && p is > 0 and <= 65535 ? p : 80;
        UrlPreview.Text = $"Sites answer at http://<project>.{suffix}{(port == 80 ? "" : $":{port}")}";
    }

    private void ShowError(string? error)
    {
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // DNNMANAGER_* environment variables are applied on top of the file, so they win over what's saved here.
    private void ShowEnvironmentOverrides()
    {
        var overrides = Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(k => k.StartsWith(Program.EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (overrides.Count == 0) return;
        EnvWarningText.Text = "These environment variables override the saved settings: " + string.Join(", ", overrides);
        EnvWarning.Visibility = Visibility.Visible;
    }

    private static string Snapshot(AppOptions o) => string.Join("|",
        o.BaseDirectory, o.SitePort, o.HostnameSuffix, string.Join(",", o.GitHubReleaseApis),
        o.Docker.ContainerName, o.Docker.ContainerIp, o.Docker.VolumeName, o.Docker.SaPassword,
        o.Docker.DefaultPort, o.Docker.Collation, o.Docker.MssqlPid, o.SsmsRememberPassword,
        o.KeepDnnPackages);

    private void Restart()
    {
        if (_runner.IsBusy)
        {
            Dialogs.Error($"'{_runner.Current}' is still running - restart once it has finished.");
            return;
        }

        // Already elevated, so the new instance inherits the admin token without another UAC prompt.
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch (Exception ex)
        {
            Dialogs.Error($"Could not restart: {ex.Message}");
            return;
        }
        System.Windows.Application.Current.Shutdown();
    }
}
