using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// "Environment": what DNN Manager needs on this PC - Docker Desktop, its engine, the SQL Server container and the
/// connection to it, and the IIS Windows features - with what's active and the actions to set up what's missing.
/// Nothing is checked until a Test button is pressed; an action re-tests its own part afterwards.
/// </summary>
public partial class EnvironmentPage : UserControl
{
    private enum Tone { Good, Bad, Warning, Muted }

    public sealed record FeatureRow(string Label, string Name, string Status);

    private readonly OperationRunner _runner;
    private readonly IServiceProvider _services;
    private readonly AppOptions _options;
    private readonly IPrerequisiteChecker _prereq;
    private readonly IDockerComposeService _compose;
    private int _dockerVersion;
    private int _featuresVersion;

    public EnvironmentPage(OperationRunner runner, IServiceProvider services, IOptions<AppOptions> options,
        IPrerequisiteChecker prereq, IDockerComposeService compose)
    {
        _runner = runner; _services = services; _options = options.Value; _prereq = prereq; _compose = compose;
        InitializeComponent();
        FeatureList.ItemsSource = _options.RequiredIisFeatures.Select(f => new FeatureRow(f.Label, f.Name, "Not tested")).ToList();
    }

    private void DockerTest_Click(object sender, RoutedEventArgs e) => TestDocker();

    private void FeaturesTest_Click(object sender, RoutedEventArgs e) => TestFeatures();

    /// <summary>Tests Docker Desktop, its engine, the SQL Server container and the connection - at once.</summary>
    private async void TestDocker()
    {
        var version = ++_dockerVersion;
        DockerTestButton.IsEnabled = false;
        DockerTestButton.Content = "Testing…";
        foreach (var status in new[] { DockerStatus, EngineStatus, ContainerStatus, SqlStatus })
            status.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        try
        {
            var docker = _prereq.GetDockerStatusAsync(_options.Docker.ContainerName, CancellationToken.None);
            var sql = TestSqlAsync();
            var d = await docker;
            if (version == _dockerVersion) ShowDocker(d);
            var s = await sql;
            if (version == _dockerVersion) ShowSql(s);
        }
        catch (Exception ex)
        {
            if (version == _dockerVersion) Dialogs.Error($"Could not test Docker and SQL Server: {ex.Message}");
        }
        finally
        {
            if (version == _dockerVersion)
            {
                DockerTestButton.IsEnabled = true;
                DockerTestButton.Content = "Test";
            }
        }
    }

    /// <summary>Tests the IIS Windows features (one PowerShell call - takes a few seconds).</summary>
    private async void TestFeatures()
    {
        var version = ++_featuresVersion;
        FeaturesTestButton.IsEnabled = false;
        FeaturesTestButton.Content = "Testing…";
        FeaturesSummary.Text = "Testing…";
        try
        {
            var states = await _prereq.GetIisFeatureStatesAsync(CancellationToken.None);
            if (version == _featuresVersion) ShowFeatures(states);
        }
        catch (Exception ex)
        {
            if (version == _featuresVersion) Dialogs.Error($"Could not test the IIS features: {ex.Message}");
        }
        finally
        {
            if (version == _featuresVersion)
            {
                FeaturesTestButton.IsEnabled = true;
                FeaturesTestButton.Content = "Test";
            }
        }
    }

    private async Task<(string Server, Domain.Result<string> Result)> TestSqlAsync()
    {
        using var scope = _services.CreateScope();
        var local = scope.ServiceProvider.GetRequiredService<LocalSqlContainer>();
        var tester = scope.ServiceProvider.GetRequiredService<ISqlConnectionTester>();
        return (local.Server, await tester.TestAsync(local.DefaultConnection, CancellationToken.None, timeoutSeconds: 5));
    }

    // ─── Showing the results ──────────────────────────────────────────────

    private void ShowDocker(DockerStatus d)
    {
        var container = _options.Docker.ContainerName;

        if (d.DesktopInstalled)
            Set(DockerStatus, DockerDetail, "Installed", Tone.Good, d.ClientVersion is { } v ? $"docker {v}" : "Docker Desktop");
        else
            Set(DockerStatus, DockerDetail, "Not installed", Tone.Bad,
                "Runs the SQL Server container. Not needed if Settings → SQL Server points at a SQL Server you already run.");
        InstallDockerButton.Visibility = d.DesktopInstalled ? Visibility.Collapsed : Visibility.Visible;

        if (d.EngineRunning)
            Set(EngineStatus, EngineDetail, "Running", Tone.Good, $"engine {d.EngineVersion}");
        else if (d.DesktopInstalled)
            Set(EngineStatus, EngineDetail, "Stopped", Tone.Bad, "Start Docker Desktop and wait until the engine runs.");
        else
            Set(EngineStatus, EngineDetail, "—", Tone.Muted, "");
        StartDockerButton.Visibility = d.DesktopInstalled && !d.EngineRunning ? Visibility.Visible : Visibility.Collapsed;

        SetupContainerButton.Visibility = Visibility.Collapsed;
        if (!d.EngineRunning)
        {
            Set(ContainerStatus, ContainerDetail, "—", Tone.Muted, $"'{container}' - needs the Docker engine.");
        }
        else if (d.ContainerState is null)
        {
            Set(ContainerStatus, ContainerDetail, "Not created", Tone.Bad, $"No container named '{container}' yet.");
            SetupContainerButton.Content = "Set up container";
            SetupContainerButton.Visibility = Visibility.Visible;
        }
        else if (d.ContainerState.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            Set(ContainerStatus, ContainerDetail, "Running", Tone.Good, $"'{container}' - {d.ContainerStatus}");
        }
        else
        {
            Set(ContainerStatus, ContainerDetail, "Stopped", Tone.Bad, $"'{container}' - {d.ContainerStatus ?? d.ContainerState}");
            SetupContainerButton.Content = "Start container";
            SetupContainerButton.Visibility = Visibility.Visible;
        }
    }

    private void ShowSql((string Server, Domain.Result<string> Result) sql)
    {
        if (sql.Result.Success)
            Set(SqlStatus, SqlDetail, "Live", Tone.Good, $"{sql.Server} as sa - {sql.Result.Value}");
        else
            Set(SqlStatus, SqlDetail, "Offline", Tone.Bad, $"{sql.Server} as sa - {sql.Result.Error}");
    }

    private void ShowFeatures(IReadOnlyDictionary<string, bool> states)
    {
        var rows = _options.RequiredIisFeatures
            .Select(f => new FeatureRow(f.Label, f.Name,
                !states.TryGetValue(f.Name, out var on) ? "Unknown" : on ? "Enabled" : "Missing"))
            .ToList();
        FeatureList.ItemsSource = rows;

        var missing = rows.Count(r => r.Status == "Missing");
        var unknown = rows.Count(r => r.Status == "Unknown");
        FeaturesSummary.Text = unknown == rows.Count && rows.Count > 0
            ? "Couldn't read the Windows features - DNN Manager needs to run as Administrator."
            : missing == 0 ? $"All {rows.Count} Windows features are enabled."
            : $"{rows.Count - missing - unknown} of {rows.Count} Windows features enabled - {missing} missing.";
        EnableFeaturesButton.Visibility = missing > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Set(TextBlock status, TextBlock detail, string text, Tone tone, string detailText)
    {
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, tone switch
        {
            Tone.Good => "SuccessText",
            Tone.Bad => "ErrorText",
            Tone.Warning => "LogWarn",
            _ => "TextMuted"
        });
        detail.Text = detailText;
    }

    // ─── Actions ──────────────────────────────────────────────────────────

    private async void InstallDocker_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("Install Docker Desktop with winget? It downloads about 600 MB, and Windows may ask to " +
                             "enable WSL 2 or to restart afterwards.", defaultYes: true))
            return;
        await _runner.RunAsync("Install Docker Desktop",
            (sp, reporter, ct) => sp.GetRequiredService<IPrerequisiteChecker>().InstallDockerDesktopAsync(reporter, ct));
        TestDocker();
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
        Set(EngineStatus, EngineDetail, "Starting…", Tone.Warning, "Docker Desktop is starting the engine.");
        try
        {
            for (var i = 0; i < 24; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var status = await _prereq.GetDockerStatusAsync(_options.Docker.ContainerName, CancellationToken.None);
                if (!status.EngineRunning) continue;
                TestDocker();
                return;
            }
            Set(EngineStatus, EngineDetail, "Stopped", Tone.Bad, "The engine didn't start within two minutes - check Docker Desktop.");
        }
        finally
        {
            StartDockerButton.IsEnabled = true;
        }
    }

    private async void SetupContainer_Click(object sender, RoutedEventArgs e)
    {
        var docker = _options.Docker;
        var current = _compose.ReadCurrent();
        if (current is not null && !SameText(current, _compose.Render(docker)) &&
            !Dialogs.Confirm($"{_compose.ComposeFilePath} is different from the SQL Server settings. Replace it and set up the container?",
                defaultYes: true))
            return;

        await _runner.RunAsync("Set up Docker container",
            (sp, reporter, ct) => sp.GetRequiredService<SetupSqlContainerUseCase>().ExecuteAsync(docker, reporter, ct));
        if (YamlPanel.Visibility == Visibility.Visible) ShowYaml();
        TestDocker();
    }

    private async void EnableFeatures_Click(object sender, RoutedEventArgs e)
    {
        await _runner.RunAsync("Enable IIS features", (sp, reporter, ct) =>
            sp.GetRequiredService<IPrerequisiteChecker>().EnsureIisFeaturesAsync(reporter, sp.GetRequiredService<IUserPrompt>(), ct));
        TestFeatures();
    }

    private async void ResetIis_Click(object sender, RoutedEventArgs e)
    {
        // ResetIisUseCase asks for confirmation itself.
        await _runner.RunAsync("Reset IIS",
            (sp, reporter, ct) => sp.GetRequiredService<ResetIisUseCase>().ExecuteAsync(reporter, ct));
    }

    // ─── docker-compose.yml ───────────────────────────────────────────────

    private void ShowYaml_Click(object sender, RoutedEventArgs e)
    {
        var show = YamlPanel.Visibility != Visibility.Visible;
        YamlPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ShowYamlButton.Content = show ? "Hide docker-compose.yml" : "Show docker-compose.yml";
        if (show) ShowYaml();
    }

    /// <summary>The compose file the saved SQL Server settings produce, and whether the file on disk matches.</summary>
    private void ShowYaml()
    {
        var yaml = _compose.Render(_options.Docker);
        var current = _compose.ReadCurrent();
        YamlNote.Text = current is null
            ? $"From the SQL Server settings - no docker-compose.yml yet ({_compose.ComposeFilePath})."
            : SameText(current, yaml)
                ? $"From the SQL Server settings - the same as {_compose.ComposeFilePath}."
                : $"From the SQL Server settings - {_compose.ComposeFilePath} is different; setting up the container replaces it.";
        YamlBox.Text = yaml;
    }

    private static bool SameText(string a, string b) =>
        a.Replace("\r\n", "\n").TrimEnd() == b.Replace("\r\n", "\n").TrimEnd();
}
