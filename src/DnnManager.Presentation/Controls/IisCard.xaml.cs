using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The IIS Windows features DNN Manager needs (iis.requiredFeatures) as a table with their state - tested with Test -
/// and Set up IIS to enable the missing ones; Edit… adds or removes features, saved at once. In Settings → IIS.
/// </summary>
public partial class IisCard : UserControl
{
    public sealed record FeatureRow(string Label, string Name, string Status);

    private OperationRunner _runner = null!;
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

    private void ShowUntested()
    {
        FeatureList.ItemsSource = _options.RequiredIisFeatures.Select(f => new FeatureRow(f.Label, f.Name, "Not tested")).ToList();
        Summary.Text = "Windows features: not tested yet - press Test.";
    }

    /// <summary>Adds or removes features (<see cref="IisFeaturesDialog"/>) - saved and used at once, like the keyboard shortcuts.</summary>
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (IisFeaturesDialog.Show(_options.RequiredIisFeatures) is not { } features) return;
        try
        {
            _store.Update(s => s.Iis.RequiredFeatures = features);
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"The Windows features could not be saved: {ex.Message}");
            return;
        }
        _options.RequiredIisFeatures = features;
        ++_version; // a Test still running was about the old list
        TestButton.IsEnabled = true;
        TestButton.Content = "Test";
        ShowUntested();
        Toast.Show($"{features.Count} Windows features saved.", ToastKind.Success);
    }

    private void Test_Click(object sender, RoutedEventArgs e) => Test();

    /// <summary>Tests the IIS Windows features (one PowerShell call - takes a few seconds).</summary>
    public async void Test()
    {
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
            if (version == _version) Dialogs.Error($"Could not test the IIS features: {ex.Message}");
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
        var rows = _options.RequiredIisFeatures
            .Select(f => new FeatureRow(f.Label, f.Name,
                !states.TryGetValue(f.Name, out var on) ? "Unknown" : on ? "Enabled" : "Missing"))
            .ToList();
        FeatureList.ItemsSource = rows;

        var missing = rows.Count(r => r.Status == "Missing");
        var unknown = rows.Count(r => r.Status == "Unknown");
        Summary.Text = unknown == rows.Count && rows.Count > 0
            ? "Couldn't read the Windows features - DNN Manager needs administrator rights."
            : missing == 0 ? $"All {rows.Count} Windows features are enabled."
            : $"{rows.Count - missing - unknown} of {rows.Count} Windows features enabled - {missing} missing.";
    }

    /// <summary>Checks the IIS Windows features, enables the missing ones (after a prompt), then tests again.</summary>
    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        await _runner.RunAsync("Set up IIS", (sp, reporter, ct) =>
            sp.GetRequiredService<IPrerequisiteChecker>().EnsureIisFeaturesAsync(reporter, sp.GetRequiredService<IUserPrompt>(), ct));
        Test();
    }
}
