using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Upgrades;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Presentation.Controls;

/// <summary>The DNN a project is to be upgraded to.</summary>
public sealed record UpgradeChoice(string ReleaseApiUrl, string Tag, string Version);

/// <summary>
/// Asks which newer DNN a project is upgraded to - only releases above its DNN are offered, the latest release chosen -
/// then shows the plan DNN's upgrade path makes of it: every step in between, each with what the analyser checked
/// (Compatible, Warning, Blocking, Unknown). The upgrade starts only when nothing blocks.
/// </summary>
public partial class UpgradeDnnDialog : Window
{
    private sealed record Source(string Api, string Label);
    private sealed record Option(DnnRelease Release, string Label);

    /// <summary>A line of the plan as shown.</summary>
    public sealed record PlanLine(string Kind, string Mark, string Text, string? Tip, Thickness Margin);

    private readonly IServiceProvider _services;
    private readonly DnnReleaseCatalog _catalog;
    private readonly string _site, _directory;
    private readonly Version? _current;
    private DnnUpgradePlan? _plan;
    private int _loading, _planning;

    private UpgradeDnnDialog(IServiceProvider services, string site, string directory, string current)
    {
        _services = services;
        _catalog = services.GetRequiredService<DnnReleaseCatalog>();
        _site = site;
        _directory = directory;
        _current = DnnInstall.Number(current);
        InitializeComponent();
        ThemeManager.Track(this);
        Intro.Text = $"Upgrades '{site}' from DNN {current} as DNN's suggested upgrade path says - through every version it lists on " +
                     "the way, never straight to the newest. Before each step the site and its database are backed up; after it the " +
                     "site is restarted and checked. A step that fails puts the site back to the version before it.";

        var sources = services.GetRequiredService<IDnnReleaseService>().KnownReleaseApis.Select(api => new Source(api, Label(api))).ToList();
        RepositoryBox.ItemsSource = sources;
        RepositoryPanel.Visibility = sources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            RepositoryBox.SelectedIndex = sources.Count > 0 ? 0 : -1;
            if (sources.Count == 0) Show("No DNN repository is set (Settings → DNN releases).", null);
        };
    }

    /// <summary>What to upgrade to, or null when cancelled.</summary>
    public static UpgradeChoice? Show(IServiceProvider services, string site, string directory, string current)
    {
        var dialog = new UpgradeDnnDialog(services, site, directory, current)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dialog.ShowDialog() != true || dialog.RepositoryBox.SelectedItem is not Source source || dialog.VersionBox.SelectedItem is not Option option) return null;
        return new UpgradeChoice(source.Api, option.Release.TagName, option.Release.Version);
    }

    private async void Repository_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RepositoryBox.SelectedItem is not Source source) return;
        var loading = ++_loading;
        VersionBox.ItemsSource = null;
        Show(null, "Loading the versions…");
        var result = await _catalog.GetAsync(source.Api);
        if (loading != _loading) return;
        if (!result.Success)
        {
            Show($"Could not load the versions of {source.Label}: {result.Error}", null);
            return;
        }
        // Only what is newer than the site's DNN - an upgrade never goes back - and no pre-release.
        var newer = result.Value!.Releases
            .Where(r => !r.Prerelease && DnnInstall.Number(r.Version) is { } number && (_current is null || number > _current))
            .ToList();
        if (newer.Count == 0)
        {
            Show(null, $"The site runs the newest DNN release of {source.Label} - there is nothing newer to upgrade to.");
            return;
        }
        VersionBox.ItemsSource = newer.Select(r => new Option(r, r.Version + (r == newer[0] ? "  (latest)" : ""))).ToList();
        VersionBox.SelectedIndex = 0;
        if (result.Value.SavedAt is { } savedAt)
            Show(null, $"GitHub can't be reached - the releases as of {savedAt:g}. The upgrade downloads each step's package, so it needs internet.");
    }

    /// <summary>A version chosen: the site is analysed and the plan to it shown - off the UI thread (the database and bin are read).</summary>
    private async void Version_Changed(object sender, SelectionChangedEventArgs e)
    {
        _plan = null;
        OkButton.IsEnabled = false;
        if (VersionBox.SelectedItem is not Option option || DnnInstall.Number(option.Release.Version) is not { } target) return;
        var planning = ++_planning;
        PlanList.ItemsSource = new[] { new PlanLine("Heading", "", "Analysing the site, its database, IIS and this PC…", null, new Thickness(0)) };
        Result<DnnUpgradePlan> result;
        using (var scope = _services.CreateScope())
        {
            var useCase = scope.ServiceProvider.GetRequiredService<UpgradeDnnUseCase>();
            result = await Task.Run(() => useCase.PlanAsync(_site, _directory, target, CancellationToken.None));
        }
        if (planning != _planning) return;
        if (!result.Success)
        {
            Show($"The site couldn't be analysed: {result.Error}", null);
            return;
        }
        _plan = result.Value!;
        PlanList.ItemsSource = Lines(_plan);
        var blocking = _plan.Blocking;
        Show(blocking.Count > 0 ? $"{blocking.Count} blocking problem(s) - the upgrade can't start until they're solved (see the plan)." : null,
            $"{_plan.Steps.Count} step(s): {string.Join(" → ", new[] { DnnUpgradeStep.Name(_plan.Current) }.Concat(_plan.Steps.Select(s => DnnUpgradeStep.Name(s.Step.To))))}");
    }

    private static List<PlanLine> Lines(DnnUpgradePlan plan)
    {
        var lines = new List<PlanLine>
        {
            new("Heading", "", $"DNN {DnnUpgradeStep.Name(plan.Current)} → {DnnUpgradeStep.Name(plan.Target)}: the required path", null, new Thickness(0, 0, 0, 4))
        };
        lines.AddRange(plan.Steps.Select((s, i) => new PlanLine("Note", $"Step {i + 1}", $"{s.Step}  ({Method(s.Step.Method)})", null, new Thickness(0, 1, 0, 1))));
        lines.Add(new("Heading", "", "The site", null, new Thickness(0, 12, 0, 4)));
        lines.AddRange(plan.Site.Select(Line));
        foreach (var (step, i) in plan.Steps.Select((s, i) => (s, i)))
        {
            lines.Add(new("Heading", "", $"Step {i + 1} - {step.Step}", null, new Thickness(0, 12, 0, 4)));
            lines.AddRange(step.Checks.Select(Line));
        }
        return lines;

        static PlanLine Line(UpgradeFinding f) => new(f.Severity.ToString(), f.Severity.ToString(),
            $"{f.Area}: {f.Title}" + (f.Detail is null ? "" : $" - {f.Detail}") + (f.Fix is null ? "" : $" → {f.Fix}"), f.Fix, new Thickness(0, 1, 0, 1));

        static string Method(DnnUpgradeMethod method) => method switch
        {
            DnnUpgradeMethod.LocalUpgrade => "DNN's local upgrade, from the install package",
            DnnUpgradeMethod.Manual => "by hand",
            _ => "upgrade package"
        };
    }

    private void Show(string? problem, string? hint)
    {
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (hint is not null) VersionHint.Text = hint;
        OkButton.IsEnabled = problem is null && _plan is { CanStart: true };
    }

    /// <summary><c>https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases</c> → <c>dnnsoftware/Dnn.Platform</c>.</summary>
    private static string Label(string api)
    {
        if (!Uri.TryCreate(api, UriKind.Absolute, out var uri)) return api;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "repos" ? $"{parts[1]}/{parts[2]}" : api;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
