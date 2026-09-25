using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages;

/// <summary>"Setup an existing project folder": its IIS website, its local database, or both.</summary>
public partial class ExistingFolderPage : UserControl, IRefreshable
{
    private readonly OperationRunner _runner;
    private readonly IProjectRepository _repo;
    private readonly IIisManager _iis;
    private readonly AppOptions _opts;

    public sealed record Folder(string Name, string Iis);

    public ExistingFolderPage(OperationRunner runner, IProjectRepository repo, IIisManager iis, IOptions<AppOptions> opts)
    {
        _runner = runner; _repo = repo; _iis = iis; _opts = opts.Value;
        InitializeComponent();
        Refresh();
    }

    private Folder? Selected => FolderList.SelectedItem as Folder;

    public void Refresh()
    {
        var selected = Selected?.Name;

        // Show which folders already have a website, so the ones still needing one stand out.
        var sites = _iis.GetSiteStates();
        var folders = _repo.ListAllProjectDirectories()
            .Select(n => new Folder(n, sites.TryGetValue(n, out var state) ? $"IIS: {state}" : "no IIS site"))
            .ToList();

        FolderList.ItemsSource = folders;
        FolderList.SelectedItem = folders.FirstOrDefault(f => f.Name == selected);
        if (folders.Count == 0) EmptyText.Text = $"No project folders found in {_opts.BaseDirectory}.";
        UpdateOptions();
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOptions();

    private void UpdateOptions()
    {
        if (Selected is not { } folder)
        {
            OptionsCard.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            RunButton.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        OptionsCard.Visibility = Visibility.Visible;
        RunButton.Visibility = Visibility.Visible;
        OptionsTitle.Text = $"'{folder.Name}' - what should be set up?";

        // The folder name becomes the IIS site, app pool and hostname, so it must be a valid project name.
        var check = ProjectName.Validate(folder.Name);
        NameError.Text = check.Success ? "" : $"This folder can't be used as a project: {check.Error} Rename the folder and retry.";
        NameError.Visibility = check.Success ? Visibility.Collapsed : Visibility.Visible;
        FolderOptions.IsEnabled = check.Success;
        RunButton.IsEnabled = check.Success;

        if (check.Success) FolderOptions.Load(_repo.Build(folder.Name));
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } folder || !ProjectName.Validate(folder.Name).Success) return;

        var action = FolderOptions.Action;
        var req = new HostExistingProjectRequest
        {
            ProjectName = folder.Name,
            SetupIis = action is ExistingFolderAction.IisOnly or ExistingFolderAction.IisAndDatabase,
            SetupDatabase = FolderOptions.SetupsDatabase,
            BackupFilePath = FolderOptions.SetupsDatabase ? FolderOptions.BackupFile : null
        };
        await _runner.RunAsync($"Set up existing project '{folder.Name}'",
            (sp, reporter, ct) => sp.GetRequiredService<HostExistingProjectUseCase>().ExecuteAsync(req, reporter, ct));
    }
}
