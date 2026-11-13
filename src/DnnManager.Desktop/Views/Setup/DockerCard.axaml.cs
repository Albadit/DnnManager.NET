using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Desktop.Services;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static DnnManager.Desktop.Views.Setup.SetupText;

namespace DnnManager.Desktop.Views.Setup;

/// <summary>
/// Docker Desktop, its engine and the SQL Server container made from the settings - tested with Test, with the actions
/// that set up what's missing: install or start Docker Desktop, run the docker-compose.yml, show it to copy. The Windows
/// app's DockerCard (Settings → Docker container); it works with the saved settings - <see cref="Attach"/> gives it the
/// app's services.
/// </summary>
public partial class DockerCard : UserControl
{
    private OperationRunner? _runner;
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
        // Set up docker-compose waits for a test: the first time the card is shown (laid out), it tests by itself.
        EffectiveViewportChanged += (_, _) =>
        {
            if (_version == 0 && _runner is not null && IsEffectivelyVisible) Test();
        };
    }

    /// <summary>
    /// The container was set up or the engine came up - what depends on it (the database server's test) may want to
    /// look again.
    /// </summary>
    public event EventHandler? ContainerChanged;

    /// <summary>The sa password the data volume has (an old installation's) - in the settings now, and used at once.</summary>
    public event EventHandler<string>? PasswordAdopted;

    public void Attach(IServiceProvider services)
    {
        _runner = services.GetRequiredService<OperationRunner>();
        _options = services.GetRequiredService<IOptions<AppOptions>>().Value;
        _prereq = services.GetRequiredService<IPrerequisiteChecker>();
        _compose = services.GetRequiredService<IDockerComposeService>();
        _store = services.GetRequiredService<SettingsStore>();
        _live = services.GetRequiredService<LiveSettings>();
    }

    private void Test_Click(object? sender, RoutedEventArgs e) => Test();

    /// <summary>Tests Docker Desktop, its engine and the SQL Server container.</summary>
    public async void Test()
    {
        if (_runner is null) return;
        var version = ++_version;
        TestButton.IsEnabled = false;
        TestButton.Content = "Testing…";
        // The words stay until the answer is there - muted meanwhile.
        foreach (var status in new[] { DockerStatus, EngineStatus, ContainerStatus })
            foreach (var tone in new[] { "good", "badTone", "warnTone" }) status.Classes.Set(tone, false);
        SetupComposeButton.IsEnabled = false;
        SetupComposeHint.Text = "Testing Docker…";
        SetupComposeHint.IsVisible = true;
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
            SetupComposeHint.IsVisible = false;
            await Dialogs.MessageAsync($"Could not test Docker: {ex.Message}");
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
            SetStatus(DockerStatus, DockerDetail, "Installed", Tone.Good, d.ClientVersion is { } v ? $"docker {v}" : "Docker Desktop");
        else
            SetStatus(DockerStatus, DockerDetail, "Not installed", Tone.Bad,
                "Runs the SQL Server container. Not needed if Settings → Database server points at a SQL Server you already run.");
        InstallDockerButton.IsVisible = !d.DesktopInstalled;

        if (d.EngineRunning)
            SetStatus(EngineStatus, EngineDetail, "Running", Tone.Good, $"engine {d.EngineVersion}");
        else if (d.DesktopInstalled)
            SetStatus(EngineStatus, EngineDetail, "Stopped", Tone.Bad, "Start Docker Desktop and wait until the engine runs.");
        else
            SetStatus(EngineStatus, EngineDetail, "-", Tone.Muted, "");
        StartDockerButton.IsVisible = d.DesktopInstalled && !d.EngineRunning;

        if (!d.EngineRunning)
            SetStatus(ContainerStatus, ContainerDetail, "-", Tone.Muted, $"'{container}' - needs the Docker engine.");
        else if (d.ContainerState is null)
            SetStatus(ContainerStatus, ContainerDetail, "Not created", Tone.Bad,
                $"No container named '{container}' - create it with Set up docker-compose below.");
        else if (d.ContainerState.Equals("running", StringComparison.OrdinalIgnoreCase))
            SetStatus(ContainerStatus, ContainerDetail, "Running", Tone.Good, $"'{container}' - {d.ContainerStatus}");
        else
            SetStatus(ContainerStatus, ContainerDetail, "Stopped", Tone.Bad,
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
        ToolTip.SetTip(SetupComposeButton, missing ?? "Set up docker-compose");
        SetupComposeHint.Text = missing ?? "";
        SetupComposeHint.IsVisible = missing is not null;
    }

    // ─── Actions ──────────────────────────────────────────────────────────

    private async void InstallDocker_Click(object? sender, RoutedEventArgs e)
    {
        if (_runner is null) return;
        if (!await Dialogs.ConfirmAsync("Install Docker Desktop with winget? It downloads about 600 MB, and Windows may ask to " +
                                        "enable WSL 2 or to restart afterwards.", "Install Docker Desktop", "Cancel"))
            return;
        await _runner.RunAsync("Install Docker Desktop",
            (sp, reporter, ct) => sp.GetRequiredService<IPrerequisiteChecker>().InstallDockerDesktopAsync(reporter, ct));
        Test();
    }

    private async void StartDocker_Click(object? sender, RoutedEventArgs e)
    {
        var started = _prereq.StartDockerDesktop();
        if (!started.Success)
        {
            await Dialogs.MessageAsync(started.Error!);
            return;
        }

        // The engine takes a while to come up - keep checking for up to two minutes.
        StartDockerButton.IsEnabled = false;
        SetStatus(EngineStatus, EngineDetail, "Starting…", Tone.Warning, "Docker Desktop is starting the engine.");
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
            SetStatus(EngineStatus, EngineDetail, "Stopped", Tone.Bad, "The engine didn't start within two minutes - check Docker Desktop.");
        }
        catch (Exception ex)
        {
            SetStatus(EngineStatus, EngineDetail, "Unknown", Tone.Bad, $"Could not ask Docker: {ex.Message}");
        }
        finally
        {
            StartDockerButton.IsEnabled = true;
        }
    }

    /// <summary>Runs the settings' docker-compose.yml (with the SA password) and waits for SQL Server, then tests again.</summary>
    private async void SetupCompose_Click(object? sender, RoutedEventArgs e)
    {
        if (_runner is null) return;
        var docker = _options.Docker;
        await _runner.RunAsync("Set up docker-compose",
            (sp, reporter, ct) => sp.GetRequiredService<SetupSqlContainerUseCase>().ExecuteAsync(docker, reporter, ct, AdoptPassword));
        Test();
        ContainerChanged?.Invoke(this, EventArgs.Empty);
    }

    // Asked from the operation's thread: saved and applied on the UI thread.
    private Domain.Result AdoptPassword(string password) => Dispatcher.UIThread.Invoke(() =>
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

    private void ShowYaml_Click(object? sender, RoutedEventArgs e)
    {
        var show = !YamlPanel.IsVisible;
        YamlPanel.IsVisible = show;
        ShowYamlButton.Content = show ? "Hide docker-compose.yml" : "Show docker-compose.yml";
        if (show) YamlBox.Text = _compose.Render(_options.Docker);
    }

    private async void CopyYaml_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(YamlBox.Text ?? "");
        }
        catch (Exception ex)
        {
            // Another program has the clipboard open - rare, and trying again works.
            Say($"Could not copy to the clipboard: {ex.Message}");
            return;
        }
        CopyYamlText.Text = "Copied";
        Say("docker-compose.yml copied to the clipboard.");
        var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        reset.Tick += (_, _) => { reset.Stop(); CopyYamlText.Text = "Copy"; };
        reset.Start();
    }

    private void Say(string text) => (TopLevel.GetTopLevel(this) as MainWindow)?.Say(text);
}
