using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// Edits the settings in <c>settings.json</c>, saving each change as soon as it is valid. Every service
/// reads its options once at startup, so saved changes apply after a restart (offered in a banner).
/// </summary>
public partial class SettingsPage : UserControl
{
    // Form labels for the settings keys, so a problem reads "Site port must be…" rather than "projects.sitePort must be…".
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["projects.baseDirectory"] = "Projects folder",
        ["projects.sitePort"] = "Site port",
        ["projects.hostnameSuffix"] = "Hostname suffix",
        ["projects.dnnReleaseSources"] = "DNN release sources",
        ["sqlServer.host"] = "Server host",
        ["sqlServer.port"] = "Default port",
        ["sqlServer.saPassword"] = "SA password",
        ["sqlServer.containerName"] = "Container name",
        ["sqlServer.volumeName"] = "Volume name",
        ["sqlServer.edition"] = "Edition",
        ["sqlServer.collation"] = "Collation",
    };

    // What the running app was started with - to tell whether the file has changes still to apply.
    private readonly AppOptions _running;
    private readonly OperationRunner _runner;
    private readonly SettingsStore _store;
    // Saves a moment after the last keystroke rather than on every one.
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    // Set while the form is being filled in, so that doesn't count as an edit.
    private bool _loading;

    public SettingsPage(IOptions<AppOptions> running, OperationRunner runner, SettingsStore store)
    {
        _running = running.Value;
        _runner = runner;
        _store = store;
        InitializeComponent();
        Subtitle.Text = $"Changes are saved to {_store.FilePath} as you make them.";

        foreach (var box in new[] { BaseDirectory, SitePort, HostnameSuffix, ReleaseApis, ContainerName, ContainerIp,
                                    VolumeName, DefaultPort, Collation, MssqlPid, DbNameSuffix })
            box.TextChanged += (_, _) => Edited();
        SaPassword.PasswordChanged += (_, _) => Edited();
        SsmsRememberPassword.Checked += (_, _) => Edited();
        SsmsRememberPassword.Unchecked += (_, _) => Edited();
        _saveTimer.Tick += (_, _) => SavePending();
        Unloaded += (_, _) => SavePending();

        ShowEnvironmentOverrides();
        Load();
    }

    /// <summary>Saves an edit still waiting for its timer - when leaving the page or closing the app.</summary>
    public void SavePending()
    {
        if (!_saveTimer.IsEnabled) return;
        _saveTimer.Stop();
        Save();
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
            ShowStatus(null);
            ShowError(string.Join(Environment.NewLine, [ex.Message, .. ex.Problems, "Fix the file, then open this page again."]));
            return;
        }

        _loading = true;
        var p = saved.Projects;
        var sql = saved.SqlServer;
        BaseDirectory.Text = p.BaseDirectory;
        SitePort.Text = p.SitePort.ToString();
        HostnameSuffix.Text = p.HostnameSuffix;
        ReleaseApis.Text = string.Join(Environment.NewLine, p.DnnReleaseSources);
        ContainerName.Text = sql.ContainerName;
        ContainerIp.Text = sql.Host;
        VolumeName.Text = sql.VolumeName;
        SaPassword.Password = sql.SaPassword;
        DefaultPort.Text = sql.Port.ToString();
        Collation.Text = sql.Collation;
        MssqlPid.Text = sql.Edition;
        DbNameSuffix.Text = sql.DatabaseNameSuffix;
        SsmsRememberPassword.IsChecked = saved.Ssms.RememberPassword;
        _loading = false;

        ShowError(null);
        ShowStatus(null);
        UpdateRestartBanner(saved);
    }

    private void Edited()
    {
        if (_loading || !Form.IsEnabled) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        UserSettings settings;
        try
        {
            // Start from the file, so keys this page doesn't show (theme, IIS features…) stay as they are.
            settings = _store.Read();
        }
        catch (SettingsException ex)
        {
            ShowError($"Not saved - {ex.Message}");
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
            ShowStatus("Not saved yet - fix the value above.");
            return;
        }

        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not save {_store.FilePath}: {ex.Message}");
            return;
        }

        ShowError(null);
        ShowStatus($"Saved at {DateTime.Now:HH:mm:ss}.");
        UpdateRestartBanner(settings);
    }

    /// <summary>Copies the form into <paramref name="settings"/>, or returns the first value that isn't a number where one is needed.</summary>
    private string? ApplyForm(UserSettings settings)
    {
        if (!int.TryParse(SitePort.Text.Trim(), out var sitePort))
            return "Site port must be a number between 1 and 65535.";
        if (!int.TryParse(DefaultPort.Text.Trim(), out var sqlPort))
            return "Default port must be a number between 1 and 65535.";

        var p = settings.Projects;
        p.BaseDirectory = BaseDirectory.Text.Trim();
        p.SitePort = sitePort;
        p.HostnameSuffix = HostnameSuffix.Text.Trim().Trim('.');
        p.DnnReleaseSources = ReleaseApis.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var sql = settings.SqlServer;
        sql.ContainerName = ContainerName.Text.Trim();
        sql.Host = ContainerIp.Text.Trim();
        sql.VolumeName = VolumeName.Text.Trim();
        sql.SaPassword = SaPassword.Password;
        sql.Port = sqlPort;
        sql.Collation = Collation.Text.Trim();
        sql.Edition = MssqlPid.Text.Trim();
        sql.DatabaseNameSuffix = DbNameSuffix.Text.Trim();

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

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        SavePending();
        SettingsStartup.OpenInEditor(_store.FilePath);
    }

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

    private void ShowStatus(string? status) =>
        StatusText.Text = status ?? "Changes are saved automatically.";

    // DNNMGR_* environment variables are applied on top of the file, so they win over what's saved here.
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
        o.Docker.DefaultPort, o.Docker.Collation, o.Docker.MssqlPid, o.Docker.DefaultDbNameSuffix, o.SsmsRememberPassword);

    private void Restart()
    {
        SavePending();
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
