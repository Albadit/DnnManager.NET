using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Presentation.Pages;

/// <summary>"Setup a new DNN project" - or, when the folder is already there, host what's in it.</summary>
public partial class SetupPage : UserControl, IRefreshable
{
    private readonly OperationRunner _runner;
    private readonly IProjectRepository _repo;

    public SetupPage(OperationRunner runner, IProjectRepository repo, IDnnReleaseService releases)
    {
        _runner = runner; _repo = repo;
        InitializeComponent();

        SourceCombo.ItemsSource = releases.KnownReleaseApis;
        SourceCombo.SelectedIndex = 0;
    }

    private string EnteredName => NameBox.Text.Trim();

    // The folder is already there (copied in by hand, a git checkout, an earlier run…): usually only
    // the website and database are missing, so offer that before overwriting any files.
    private bool FolderExists { get; set; }

    // The project whose backups the existing-folder options currently list.
    private string? _optionsLoadedFor;

    public void Refresh()
    {
        _optionsLoadedFor = null; // the operation may have created the folder or a backup
        UpdateState();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    private void ExistingOptions_ActionChanged(object? sender, EventArgs e) => UpdateState();

    private void UpdateState()
    {
        var name = EnteredName;
        var check = ProjectName.Validate(name);
        var valid = check.Success;

        NameError.Text = name.Length > 0 && !valid ? check.Error ?? "" : "";
        NameError.Visibility = NameError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var exists = valid && _repo.ProjectExists(name);
        if (exists && !string.Equals(_optionsLoadedFor, name, StringComparison.OrdinalIgnoreCase))
        {
            ExistingOptions.Load(_repo.Build(name));
            _optionsLoadedFor = name;
        }
        FolderExists = exists;

        ExistingCard.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        ExistingTitle.Text = $"Folder '{name}' already exists - what do you want to do?";

        var downloading = !exists || ExistingOptions.Action == ExistingFolderAction.Redownload;
        PackageCard.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        RunButton.Content = downloading ? "Set up project" : "Set up existing folder";
        RunButton.IsEnabled = valid && (!downloading || SourceCombo.SelectedItem is string);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var name = EnteredName;
        if (!ProjectName.Validate(name).Success) return;

        if (FolderExists && ExistingOptions.Action != ExistingFolderAction.Redownload)
        {
            var action = ExistingOptions.Action;
            var req = new HostExistingProjectRequest
            {
                ProjectName = name,
                SetupIis = action is ExistingFolderAction.IisOnly or ExistingFolderAction.IisAndDatabase,
                SetupDatabase = ExistingOptions.SetupsDatabase,
                BackupFilePath = ExistingOptions.SetupsDatabase ? ExistingOptions.BackupFile : null
            };
            await _runner.RunAsync($"Set up existing project '{name}'",
                (sp, reporter, ct) => sp.GetRequiredService<HostExistingProjectUseCase>().ExecuteAsync(req, reporter, ct));
            return;
        }

        if (SourceCombo.SelectedItem is not string api) return;
        var version = VersionBox.Text.Trim();
        var setup = new SetupProjectRequest
        {
            ProjectName = name,
            ReleaseApiUrl = api,
            Version = version.Length == 0 ? null : version,
            AllowOverwrite = FolderExists
        };
        await _runner.RunAsync($"Set up '{name}'",
            (sp, reporter, ct) => sp.GetRequiredService<SetupProjectUseCase>().ExecuteAsync(setup, reporter, ct));
    }
}
