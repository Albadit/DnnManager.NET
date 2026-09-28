using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages;

/// <summary>"Clone a DNN project" from a local folder.</summary>
public partial class ClonePage : UserControl, IRefreshable
{
    private readonly OperationRunner _runner;
    private readonly AppOptions _options;
    private readonly bool _ready;

    public ClonePage(OperationRunner runner, IOptions<AppOptions> options)
    {
        _runner = runner;
        _options = options.Value;
        InitializeComponent();
        LoadLists();
        _ready = true;
        UpdateState();
    }

    public void Refresh()
    {
        LoadLists();
        UpdateState();
    }

    private void LoadLists()
    {
        var selectedLocal = LocalSourceCombo.SelectedItem as string;
        var parent = _options.BaseDirectory;
        var subs = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string?>();
        LocalSourceCombo.ItemsSource = subs;
        LocalSourceCombo.SelectedItem = subs.FirstOrDefault(s => s == selectedLocal);
        LocalHint.Text = !Directory.Exists(parent) ? $"Base folder does not exist: {parent}"
            : subs.Count == 0 ? $"No subfolders found in {parent}."
            : $"Projects in {parent}.";
    }

    private string TargetName => NameBox.Text.Trim();

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateState();
    }

    private void UpdateState()
    {
        var name = TargetName;
        var nameCheck = ProjectName.Validate(name);
        NameError.Text = name.Length > 0 && !nameCheck.Success ? nameCheck.Error ?? "" : "";
        NameError.Visibility = NameError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;


        var missing = MissingInput(nameCheck.Success);
        RunButton.IsEnabled = missing is null;
        RunHint.Text = missing ?? "";
    }

    /// <summary>What still has to be filled in before the clone can start, or null when ready.</summary>
    private string? MissingInput(bool nameValid)
    {
        if (!nameValid) return TargetName.Length == 0 ? "Enter a project name." : null;
        if (LocalSourceCombo.SelectedItem is not string) return "Choose the source project.";
        return null;
    }

    // ─── RUN ──────────────────────────────────────────────────────────────

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetName;
        if (!ProjectName.Validate(target).Success || MissingInput(true) is not null) return;

        // Everything the operation needs is read from the controls here, on the UI thread.
        var local = Path.Combine(_options.BaseDirectory, (string)LocalSourceCombo.SelectedItem);

        // Backup destination for the source DB (always auto-generated).
        var bakPath = Path.Combine(Path.GetTempPath(), $"dnnmgr_clone_{target}_{DateTime.Now:yyyyMMddHHmmss}.bak");

        await _runner.RunAsync($"Clone → '{target}'", async (services, reporter, ct) =>
        {
            var req = new CloneProjectRequest
            {
                TargetProjectName = target,
                SourceDirectory = local,
                SourceBackupServerPath = bakPath,
                CreateIisSite = true
            };
            return await services.GetRequiredService<CloneProjectUseCase>().ExecuteAsync(req, reporter, ct);
        });
    }
}
