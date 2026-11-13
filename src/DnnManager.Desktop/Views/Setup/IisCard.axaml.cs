using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Desktop.Services;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop.Views.Setup;

/// <summary>A row of the IIS card's table: the feature, its state ("Not tested", "Enabled", "Missing", "Unknown") and its Windows name.</summary>
public sealed record FeatureRow(string Label, string Name, string Status, bool First)
{
    public bool IsEnabled => Status == "Enabled";
    public bool IsMissing => Status == "Missing";
    public Thickness Divider => First ? new Thickness(0) : new Thickness(0, 1, 0, 0);
}

/// <summary>
/// The IIS Windows features DNN Manager needs (iis.requiredFeatures) as a table with their state - tested with Test -
/// and Set up IIS to enable the missing ones; Edit… adds or removes features, saved at once. The Windows app's IisCard
/// (Settings → IIS); <see cref="Attach"/> gives it the app's services.
/// </summary>
public partial class IisCard : UserControl
{
    private OperationRunner? _runner;
    private AppOptions _options = null!;
    private IPrerequisiteChecker _prereq = null!;
    private SettingsStore _store = null!;
    // Which Test the shown result belongs to - an answer for an older one is dropped.
    private int _version;

    public IisCard() => InitializeComponent();

    public void Attach(IServiceProvider services)
    {
        _runner = services.GetRequiredService<OperationRunner>();
        _options = services.GetRequiredService<IOptions<AppOptions>>().Value;
        _prereq = services.GetRequiredService<IPrerequisiteChecker>();
        _store = services.GetRequiredService<SettingsStore>();
        ShowUntested();
    }

    private static List<FeatureRow> Rows(IEnumerable<(string Label, string Name, string Status)> rows) =>
        rows.Select((r, i) => new FeatureRow(r.Label, r.Name, r.Status, i == 0)).ToList();

    private void ShowUntested()
    {
        FeatureList.ItemsSource = Rows(_options.RequiredIisFeatures.Select(f => (f.Label, f.Name, "Not tested")));
        Summary.Text = "Windows features: not tested yet - press Test.";
    }

    /// <summary>Adds or removes features (<see cref="IisFeaturesDialog"/>) - saved and used at once.</summary>
    private async void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if (_runner is null) return;
        if (await IisFeaturesDialog.ShowAsync(_options.RequiredIisFeatures) is not { } features) return;
        try
        {
            _store.Update(s => s.Iis.RequiredFeatures = features);
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync($"The Windows features could not be saved: {ex.Message}");
            return;
        }
        _options.RequiredIisFeatures = features;
        ++_version; // a Test still running was about the old list
        TestButton.IsEnabled = true;
        TestButton.Content = "Test";
        ShowUntested();
        (TopLevel.GetTopLevel(this) as MainWindow)?.Say($"{features.Count} Windows features saved.");
    }

    private void Test_Click(object? sender, RoutedEventArgs e) => Test();

    /// <summary>Tests the IIS Windows features (one PowerShell call - takes a few seconds).</summary>
    public async void Test()
    {
        if (_runner is null) return;
        var version = ++_version;
        TestButton.IsEnabled = false;
        TestButton.Content = "Testing…";
        Summary.Text = "Testing…";
        try
        {
            var states = await _prereq.GetIisFeatureStatesAsync(CancellationToken.None);
            if (version == _version) Show(states);
        }
        catch (Exception ex)
        {
            if (version == _version) await Dialogs.MessageAsync($"Could not test the IIS features: {ex.Message}");
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

    private void Show(IReadOnlyDictionary<string, bool> states)
    {
        var rows = Rows(_options.RequiredIisFeatures
            .Select(f => (f.Label, f.Name, !states.TryGetValue(f.Name, out var on) ? "Unknown" : on ? "Enabled" : "Missing")));
        FeatureList.ItemsSource = rows;

        var missing = rows.Count(r => r.Status == "Missing");
        var unknown = rows.Count(r => r.Status == "Unknown");
        Summary.Text = unknown == rows.Count && rows.Count > 0
            ? "Couldn't read the Windows features - DNN Manager needs administrator rights."
            : missing == 0 ? $"All {rows.Count} Windows features are enabled."
            : $"{rows.Count - missing - unknown} of {rows.Count} Windows features enabled - {missing} missing.";
    }

    /// <summary>Checks the IIS Windows features, enables the missing ones (after a prompt), then tests again.</summary>
    private async void Setup_Click(object? sender, RoutedEventArgs e)
    {
        if (_runner is null) return;
        await _runner.RunAsync("Set up IIS", (sp, reporter, ct) =>
            sp.GetRequiredService<IPrerequisiteChecker>().EnsureIisFeaturesAsync(reporter, sp.GetRequiredService<IUserPrompt>(), ct));
        Test();
    }
}
