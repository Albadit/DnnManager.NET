using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Docker Desktop, its engine and the SQL Server container made from the settings - tested with Test, with the actions
/// that set up what's missing: install or start Docker Desktop, run the docker-compose.yml, show it to copy. In
/// Settings → Docker container; it works with the saved settings.
/// </summary>
public partial class DockerCard : UserControl
{
    private OperationRunner _runner = null!;
    private AppOptions _options = null!;
    private IPrerequisiteChecker _prereq = null!;
    private IDockerComposeService _compose = null!;
    private SettingsStore _store = null!;
    private LiveSettings _live = null!;
    // Which Test the shown result belongs to - an answer for an older one is dropped.
    private int _version;

    public DockerCard()
    {
        InitializeComponent();
        // Set up docker-compose waits for a test: the first time the card is shown, it tests by itself.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && _version == 0 && _runner is not null) Test(); };
    }

    /// <summary>
    /// The container was set up or the engine came up - what depends on it (the database server's test) may want to
    /// look again.
    /// </summary>
    public event EventHandler? ContainerChanged;

    public void Attach(IServiceProvider services)
    {
        _runner = services.GetRequiredService<OperationRunner>();
        _options = services.GetRequiredService<IOptions<AppOptions>>().Value;
        _prereq = services.GetRequiredService<IPrerequisiteChecker>();
        _compose = services.GetRequiredService<IDockerComposeService>();
        _store = services.GetRequiredService<SettingsStore>();
        _live = services.GetRequiredService<LiveSettings>();
    }

    private void Test_Click(object sender, RoutedEventArgs e) => Test();

    /// <summary>Tests Docker Desktop, its engine and the SQL Server container.</summary>
    public async void Test()
    {
        var version = ++_version;
        TestButton.IsEnabled = false;
        TestButton.Content = "Testing…";
        foreach (var status in new[] { DockerStatus, EngineStatus, ContainerStatus })
            status.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        SetupComposeButton.IsEnabled = false;
        SetupComposeHint.Text = "Testing Docker…";
        SetupComposeHint.Visibility = Visibility.Visible;
        try
        {
            var d = await _prereq.GetDockerStatusAsync(_options.Docker.ContainerName, CancellationToken.None);
            if (version == _version) Show(d);
        }
        catch (Exception ex)
        {
            if (version != _version) return;
            // Not known: not held back - Set up says itself what is missing.
            SetupComposeButton.IsEnabled = true;
            SetupComposeHint.Visibility = Visibility.Collapsed;
            Dialogs.Error($"Could not test Docker: {ex.Message}");
        }
        finally
        {
            if (version == _version)
            {
                TestButton.IsEnabled = true;
                TestButton.Content = "Test";
            }
        }
    }

    private void Show(DockerStatus d)
    {
        var container = _options.Docker.ContainerName;
        ShowSetUp(d);

        if (d.DesktopInstalled)
            StatusTone.Set(DockerStatus, DockerDetail, "Installed", StatusTone.Good, d.ClientVersion is { } v ? $"docker {v}" : "Docker Desktop");
        else
            StatusTone.Set(DockerStatus, DockerDetail, "Not installed", StatusTone.Bad,
                "Runs the SQL Server container. Not needed if Settings → Database server points at a SQL Server you already run.");
        InstallDockerButton.Visibility = d.DesktopInstalled ? Visibility.Collapsed : Visibility.Visible;

        if (d.EngineRunning)
            StatusTone.Set(EngineStatus, EngineDetail, "Running", StatusTone.Good, $"engine {d.EngineVersion}");
        else if (d.DesktopInstalled)
            StatusTone.Set(EngineStatus, EngineDetail, "Stopped", StatusTone.Bad, "Start Docker Desktop and wait until the engine runs.");
        else
            StatusTone.Set(EngineStatus, EngineDetail, "-", StatusTone.Muted, "");
        StartDockerButton.Visibility = d.DesktopInstalled && !d.EngineRunning ? Visibility.Visible : Visibility.Collapsed;

        if (!d.EngineRunning)
            StatusTone.Set(ContainerStatus, ContainerDetail, "-", StatusTone.Muted, $"'{container}' - needs the Docker engine.");
        else if (d.ContainerState is null)
            StatusTone.Set(ContainerStatus, ContainerDetail, "Not created", StatusTone.Bad,
                $"No container named '{container}' - create it with Set up docker-compose below.");
        else if (d.ContainerState.Equals("running", StringComparison.OrdinalIgnoreCase))
            StatusTone.Set(ContainerStatus, ContainerDetail, "Running", StatusTone.Good, $"'{container}' - {d.ContainerStatus}");
        else
            StatusTone.Set(ContainerStatus, ContainerDetail, "Stopped", StatusTone.Bad,
                $"'{container}' - {d.ContainerStatus ?? d.ContainerState}. Start it with Set up docker-compose below.");
    }

    /// <summary>
    /// Set up docker-compose only with Docker's engine running - otherwise off, with what is missing beside it (and as its
    /// tooltip, which a disabled button still shows).
    /// </summary>
    private void ShowSetUp(DockerStatus d)
    {
        var missing = d.CanSetUp ? null
            : !d.DesktopInstalled ? "Needs Docker Desktop - install it above."
            : "Needs the Docker engine running - start Docker Desktop above.";
        SetupComposeButton.IsEnabled = missing is null;
        SetupComposeButton.ToolTip = missing ?? "Set up docker-compose";
        SetupComposeHint.Text = missing ?? "";
        SetupComposeHint.Visibility = missing is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ─── Actions ──────────────────────────────────────────────────────────

    private async void InstallDocker_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("Install Docker Desktop with winget? It downloads about 600 MB, and Windows may ask to " +
                             "enable WSL 2 or to restart afterwards.", "Install Docker Desktop", "Cancel", defaultYes: true))
            return;
        await _runner.RunAsync("Install Docker Desktop",
            (sp, reporter, ct) => sp.GetRequiredService<IPrerequisiteChecker>().InstallDockerDesktopAsync(reporter, ct));
        Test();
    }

    private async void StartDocker_Click(object sender, RoutedEventArgs e)
    {
        var started = _prereq.StartDockerDesktop();
        if (!started.Success)
        {
            Dialogs.Error(started.Error!);
            return;
        }

        // The engine takes a while to come up - keep checking for up to two minutes.
        StartDockerButton.IsEnabled = false;
        StatusTone.Set(EngineStatus, EngineDetail, "Starting…", StatusTone.Warning, "Docker Desktop is starting the engine.");
        try
        {
            for (var i = 0; i < 24; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var status = await _prereq.GetDockerStatusAsync(_options.Docker.ContainerName, CancellationToken.None);
                if (!status.EngineRunning) continue;
                Test();
                ContainerChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            StatusTone.Set(EngineStatus, EngineDetail, "Stopped", StatusTone.Bad, "The engine didn't start within two minutes - check Docker Desktop.");
        }
        finally
        {
            StartDockerButton.IsEnabled = true;
        }
    }

    /// <summary>Runs the settings' docker-compose.yml (with the SA password) and waits for SQL Server, then tests again.</summary>
    private async void SetupCompose_Click(object sender, RoutedEventArgs e)
    {
        var docker = _options.Docker;
        await _runner.RunAsync("Set up docker-compose",
            (sp, reporter, ct) => sp.GetRequiredService<SetupSqlContainerUseCase>().ExecuteAsync(docker, reporter, ct, AdoptPassword));
        Test();
        ContainerChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The sa password the data volume has (an old installation's) - in the settings now, and used at once.</summary>
    public event EventHandler<string>? PasswordAdopted;

    private Domain.Result AdoptPassword(string password) => Dispatcher.Invoke(() =>
    {
        try
        {
            var saved = _store.Update(s => s.SqlServer.SaPassword = password);
            _live.Apply(saved);
            PasswordAdopted?.Invoke(this, password);
            return Domain.Result.Ok();
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            return Domain.Result.Fail(ex.Message);
        }
    });

    // ─── docker-compose.yml ───────────────────────────────────────────────

    private void ShowYaml_Click(object sender, RoutedEventArgs e)
    {
        var show = YamlPanel.Visibility != Visibility.Visible;
        YamlPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ShowYamlButton.Content = show ? "Hide docker-compose.yml" : "Show docker-compose.yml";
        if (show) YamlBox.Text = _compose.Render(_options.Docker);
    }

    private void CopyYaml_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(YamlBox.Text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            // Another program has the clipboard open - rare, and trying again works.
            Toast.Show($"Could not copy to the clipboard: {ex.Message}", ToastKind.Warning);
            return;
        }
        CopyYamlText.Text = "Copied";
        Toast.Show("docker-compose.yml copied to the clipboard.", ToastKind.Success);
        var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        reset.Tick += (_, _) => { reset.Stop(); CopyYamlText.Text = "Copy"; };
        reset.Start();
    }
}

/// <summary>Colours a status word and fills in its detail - for the status lines of the environment cards.</summary>
internal static class StatusTone
{
    public const string Good = "SuccessText", Bad = "ErrorText", Warning = "LogWarn", Muted = "TextMuted";

    public static void Set(TextBlock status, TextBlock detail, string text, string tone, string detailText)
    {
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, tone);
        detail.Text = detailText;
    }
}
