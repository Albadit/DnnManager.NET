using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Files;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// Edits the settings in <c>appsettings.json</c>. Every service reads its options once at startup,
/// so saved changes apply after a restart (offered right away).
/// </summary>
public partial class SettingsPage : UserControl
{
    private const string EnvPrefix = "DNNMGR_";

    // What the running app was started with - to tell whether the file has changes still to apply.
    private readonly AppOptions _running;
    private readonly OperationRunner _runner;
    private readonly ISqlConnectionTester _sqlTester;
    private readonly IDockerComposeService _compose;

    public SettingsPage(IOptions<AppOptions> running, OperationRunner runner, ISqlConnectionTester sqlTester,
        IDockerComposeService compose)
    {
        _running = running.Value;
        _runner = runner;
        _sqlTester = sqlTester;
        _compose = compose;
        InitializeComponent();
        Subtitle.Text = $"Stored in {AppSettingsFile.FullPath}.";
        ShowEnvironmentOverrides();
        Show(LoadFromFile());
    }

    /// <summary>The settings as saved in the file (what the next start will use, env overrides aside).</summary>
    private static AppOptions LoadFromFile()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(BundledFiles.AppSettings, optional: true, reloadOnChange: false)
            .Build();
        var options = new AppOptions();
        config.GetSection(AppOptions.SectionName).Bind(options);
        return options;
    }

    private void Show(AppOptions o)
    {
        BaseDirectory.Text = o.BaseDirectory;
        SitePort.Text = o.SitePort.ToString();
        HostnameSuffix.Text = o.HostnameSuffix;
        ReleaseApis.Text = string.Join(Environment.NewLine, o.GitHubReleaseApis);
        ContainerName.Text = o.Docker.ContainerName;
        ContainerIp.Text = o.Docker.ContainerIp;
        VolumeName.Text = o.Docker.VolumeName;
        SaPassword.Password = o.Docker.SaPassword;
        DefaultPort.Text = o.Docker.DefaultPort.ToString();
        Collation.Text = o.Docker.Collation;
        MssqlPid.Text = o.Docker.MssqlPid;
        DbNameSuffix.Text = o.Docker.DefaultDbNameSuffix;

        ShowError(null);
        RestartBanner.Visibility = Snapshot(o) == Snapshot(_running) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Reads the form back into options, or returns the first problem found.</summary>
    private (AppOptions? Options, string? Error) Read()
    {
        var baseDir = BaseDirectory.Text.Trim();
        if (baseDir.Length == 0 || !Path.IsPathFullyQualified(baseDir))
            return (null, "Projects folder must be a full path, e.g. C:\\DNN.");
        if (!TryPort(SitePort.Text, out var sitePort))
            return (null, "Site port must be a number between 1 and 65535.");
        var suffix = HostnameSuffix.Text.Trim().Trim('.');
        if (suffix.Length == 0 || suffix.Contains(' '))
            return (null, "Hostname suffix is required and can't contain spaces.");

        var apis = ReleaseApis.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (apis.Count == 0)
            return (null, "Add at least one DNN release source.");
        var badApi = apis.FirstOrDefault(a => !Uri.TryCreate(a, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"));
        if (badApi is not null)
            return (null, $"Not a valid URL: {badApi}");

        if (!TryPort(DefaultPort.Text, out var sqlPort))
            return (null, "Default SQL port must be a number between 1 and 65535.");
        var required = new[] { (ContainerName, "Container name"), (ContainerIp, "Container IP"), (VolumeName, "Volume name"),
                               (Collation, "Collation"), (MssqlPid, "Edition") };
        foreach (var (box, label) in required)
            if (box.Text.Trim().Length == 0) return (null, $"{label} is required.");
        if (SaPassword.Password.Length == 0)
            return (null, "SA password is required.");

        return (new AppOptions
        {
            BaseDirectory = baseDir,
            SitePort = sitePort,
            HostnameSuffix = suffix,
            GitHubReleaseApis = apis,
            Docker = new DockerOptions
            {
                ContainerName = ContainerName.Text.Trim(),
                ContainerIp = ContainerIp.Text.Trim(),
                VolumeName = VolumeName.Text.Trim(),
                SaPassword = SaPassword.Password,
                DefaultPort = sqlPort,
                Collation = Collation.Text.Trim(),
                MssqlPid = MssqlPid.Text.Trim(),
                DefaultDbNameSuffix = DbNameSuffix.Text.Trim()
            }
        }, null);
    }

    private static bool TryPort(string text, out int port)
        => int.TryParse(text.Trim(), out port) && port is > 0 and <= 65535;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var (options, error) = Read();
        if (options is null) { ShowError(error); return; }

        try
        {
            AppSettingsFile.Save(options);
        }
        catch (Exception ex)
        {
            ShowError($"Could not save {AppSettingsFile.FullPath}: {ex.Message}");
            return;
        }

        Show(LoadFromFile());
        if (RestartBanner.Visibility == Visibility.Visible &&
            Dialogs.Confirm("Settings saved. Restart DNN Manager now to apply them?", defaultYes: true))
            Restart();
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => Show(LoadFromFile());

    // Tests what is in the form, saved or not, so the values can be checked before saving.
    private async void TestSql_Click(object sender, RoutedEventArgs e)
    {
        var ip = ContainerIp.Text.Trim();
        if (ip.Length == 0) { SqlResult("Container IP is required.", success: false); return; }
        if (!TryPort(DefaultPort.Text, out var port)) { SqlResult("Default port must be a number between 1 and 65535.", success: false); return; }
        if (SaPassword.Password.Length == 0) { SqlResult("SA password is required.", success: false); return; }

        var server = $"{ip},{port}";
        TestSqlButton.IsEnabled = false;
        SqlStatus.Text = $"Connecting to {server} …";
        SqlStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        try
        {
            var result = await _sqlTester.TestAsync(new SiteSqlConnection(server, "master", "sa", SaPassword.Password),
                CancellationToken.None, timeoutSeconds: 5);
            if (result.Success)
                SqlResult($"✓ Connected to {server} - {result.Value}.", success: true);
            else
                SqlResult($"✗ {result.Error}", success: false);
        }
        finally
        {
            TestSqlButton.IsEnabled = true;
        }
    }

    // ─── Docker container ─────────────────────────────────────────────────

    private void SqlField_Changed(object sender, RoutedEventArgs e)
    {
        if (YamlPanel is { Visibility: Visibility.Visible }) ShowYaml(); // raised during InitializeComponent too
    }

    private void ShowYaml_Click(object sender, RoutedEventArgs e)
    {
        var show = YamlPanel.Visibility != Visibility.Visible;
        YamlPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ShowYamlButton.Content = show ? "Hide docker-compose.yml" : "Show docker-compose.yml";
        if (show) ShowYaml();
    }

    /// <summary>Shows the compose file the values in the form produce, and whether the file on disk matches.</summary>
    private void ShowYaml()
    {
        var (options, error) = Read();
        if (options is null)
        {
            YamlNote.Text = error ?? "";
            YamlBox.Text = "";
            return;
        }

        var yaml = _compose.Render(options.Docker);
        var current = _compose.ReadCurrent();
        YamlNote.Text = current is null
            ? $"Generated from the values above - no docker-compose.yml next to the app yet ({_compose.ComposeFilePath})."
            : SameText(current, yaml)
                ? $"Generated from the values above - the same as {_compose.ComposeFilePath}."
                : $"Generated from the values above - {_compose.ComposeFilePath} is different; setting up the container replaces it.";
        YamlBox.Text = yaml;
    }

    private static bool SameText(string a, string b) =>
        a.Replace("\r\n", "\n").TrimEnd() == b.Replace("\r\n", "\n").TrimEnd();

    private async void SetupDocker_Click(object sender, RoutedEventArgs e)
    {
        var (options, error) = Read();
        if (options is null) { ShowError(error); return; }
        ShowError(null);

        var docker = options.Docker;
        var current = _compose.ReadCurrent();
        if (current is not null && !SameText(current, _compose.Render(docker)) &&
            !Dialogs.Confirm($"{_compose.ComposeFilePath} is different from these settings. Replace it and set up the container?", defaultYes: true))
            return;

        // The container is set up from the form; DNN Manager itself connects with the saved settings.
        var unsaved = Snapshot(options) != Snapshot(LoadFromFile());
        await _runner.RunAsync("Set up Docker container", async (sp, reporter, ct) =>
        {
            var result = await sp.GetRequiredService<SetupSqlContainerUseCase>().ExecuteAsync(docker, reporter, ct);
            if (result.Success && unsaved)
                reporter.Warn("These SQL Server values aren't saved yet - Save them so DNN Manager connects with them.");
            return result;
        });
        if (YamlPanel.Visibility == Visibility.Visible) ShowYaml();
    }

    private void SqlResult(string text, bool success)
    {
        SqlStatus.Text = text;
        SqlStatus.SetResourceReference(TextBlock.ForegroundProperty, success ? "SuccessText" : "ErrorText");
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => Restart();

    private void BrowseBaseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Projects folder" };
        if (Directory.Exists(BaseDirectory.Text)) dialog.InitialDirectory = BaseDirectory.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) BaseDirectory.Text = dialog.FolderName;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppSettingsFile.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error($"Could not open {AppSettingsFile.FullPath}: {ex.Message}"); }
    }

    private void Preview_Changed(object sender, TextChangedEventArgs e)
    {
        if (UrlPreview is null) return; // raised during InitializeComponent
        var suffix = HostnameSuffix.Text.Trim().Trim('.');
        var port = TryPort(SitePort.Text, out var p) ? p : 80;
        UrlPreview.Text = $"Sites answer at http://<project>.{suffix}{(port == 80 ? "" : $":{port}")}";
    }

    private void ShowError(string? error)
    {
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // DNNMGR_* environment variables are applied on top of the file, so they win over what's saved here.
    private void ShowEnvironmentOverrides()
    {
        var overrides = Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(k => k.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (overrides.Count == 0) return;
        EnvWarningText.Text = "These environment variables override the saved settings: " + string.Join(", ", overrides);
        EnvWarning.Visibility = Visibility.Visible;
    }

    private static string Snapshot(AppOptions o) => string.Join("|",
        o.BaseDirectory, o.SitePort, o.HostnameSuffix, string.Join(",", o.GitHubReleaseApis),
        o.Docker.ContainerName, o.Docker.ContainerIp, o.Docker.VolumeName, o.Docker.SaPassword,
        o.Docker.DefaultPort, o.Docker.Collation, o.Docker.MssqlPid, o.Docker.DefaultDbNameSuffix);

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
