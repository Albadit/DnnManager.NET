using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// "Setup a new DNN project" from a DNN download or an imported site .zip - or, when the folder is already
/// there, host what's in it.
/// </summary>
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
        LoadBackupProjects();
    }

    private string EnteredName => NameBox.Text.Trim();

    // The folder is already there (copied in by hand, a git checkout, an earlier run…): usually only
    // the website and database are missing, so offer that before overwriting any files.
    private bool FolderExists { get; set; }

    // The project whose backups the existing-folder options currently list.
    private string? _optionsLoadedFor;

    /// <summary>A dated backup in the "From a project backup" list.</summary>
    public sealed record BackupPick(ProjectBackup Backup, string Label);

    /// <summary>Fills the project list with the projects that have a complete backup (site zip + database).</summary>
    private void LoadBackupProjects()
    {
        var selected = BackupProjectCombo.SelectedItem as string;
        var projects = _repo.ListAllProjectDirectories()
            .Where(p => ProjectBackups.List(_repo.Build(p)).Any(b => b.IsComplete))
            .ToList();
        BackupProjectCombo.ItemsSource = projects;
        BackupProjectCombo.SelectedItem = projects.FirstOrDefault(p => p == selected);
        BackupProjectCombo.IsEnabled = projects.Count > 0;
        BackupPickHint.Text = projects.Count > 0
            ? $"A project and one of its backups ({ProjectBackups.FolderName}) - or choose the files anywhere on this PC below."
            : $"No project has a backup with site and database yet (Projects → right-click → Export) - choose the files below.";
    }

    private void BackupProject_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BackupProjectCombo.SelectedItem is not string project)
        {
            ProjectBackupCombo.ItemsSource = null;
            return;
        }
        var backups = ProjectBackups.List(_repo.Build(project))
            .Where(b => b.IsComplete)
            .Select(b => new BackupPick(b, $"{b.Created:yyyy-MM-dd HH:mm:ss}  ({SizeMb(b.SiteZip!) + SizeMb(b.Database!):N1} MB)"))
            .ToList();
        ProjectBackupCombo.ItemsSource = backups;
        ProjectBackupCombo.SelectedIndex = backups.Count > 0 ? 0 : -1; // newest first
    }

    private void ProjectBackup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectBackupCombo.SelectedItem is not BackupPick pick) return;
        ZipBox.Text = pick.Backup.SiteZip!;
        BackupBox.Text = pick.Backup.Database!;

        // A copy of an existing project needs a name of its own.
        var suggested = pick.Backup.Project + "_copy";
        if (EnteredName.Length == 0 && ProjectName.Validate(suggested).Success && !_repo.ProjectExists(suggested))
            NameBox.Text = suggested;
    }

    private static double SizeMb(string file) => File.Exists(file) ? new FileInfo(file).Length / 1024d / 1024d : 0;

    public void Refresh()
    {
        LoadBackupProjects();
        _optionsLoadedFor = null; // the operation may have created the folder or a backup
        UpdateState();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    private void ExistingOptions_ActionChanged(object? sender, EventArgs e) => UpdateState();

    private void Start_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdateState();
    }

    private void ImportField_Changed(object sender, TextChangedEventArgs e) => UpdateState();

    private void BrowseZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Site .zip", Filter = "Zip files (*.zip)|*.zip" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        ZipBox.Text = dialog.FileName;

        // Name the project after the zip when no name has been typed yet.
        var suggested = Path.GetFileNameWithoutExtension(dialog.FileName).Replace(' ', '_');
        if (EnteredName.Length == 0 && ProjectName.Validate(suggested).Success) NameBox.Text = suggested;
    }

    private void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Database .bacpac", Filter = "Database backups (*.bacpac;*.bak)|*.bacpac;*.bak" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) BackupBox.Text = dialog.FileName;
    }

    private bool Importing => !FolderExists && FromZip.IsChecked == true;

    /// <summary>What's wrong with the import fields, or null when they're usable.</summary>
    private string? ImportProblem()
    {
        var zip = ZipBox.Text.Trim();
        if (zip.Length == 0) return "Choose the site .zip.";
        if (!File.Exists(zip)) return $"File not found: {zip}";
        var backup = BackupBox.Text.Trim();
        if (backup.Length == 0) return "Choose the database .bacpac.";
        if (!File.Exists(backup)) return $"File not found: {backup}";
        return LocalSqlContainer.IsBackupFile(backup) ? null : "The database backup must be a .bacpac or .bak file.";
    }

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

        // "Start from" is for a new folder; an existing one is hosted (or re-downloaded) instead.
        StartCard.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        var importing = Importing;
        var problem = importing ? ImportProblem() : null;
        ImportCard.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
        // Nothing chosen yet isn't an error - the disabled button says enough.
        var started = ZipBox.Text.Trim().Length > 0 || BackupBox.Text.Trim().Length > 0;
        ImportError.Text = problem is not null && started ? problem : "";
        ImportError.Visibility = ImportError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var downloading = !importing && (!exists || ExistingOptions.Action == ExistingFolderAction.Redownload);
        PackageCard.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        RunButton.Content = importing ? "Import project" : downloading ? "Set up project" : "Set up existing folder";
        RunButton.IsEnabled = valid && (importing ? problem is null : !downloading || SourceCombo.SelectedItem is string);
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

        if (Importing)
        {
            if (ImportProblem() is not null) return;
            var import = new ImportProjectRequest
            {
                ProjectName = name,
                ZipPath = ZipBox.Text.Trim(),
                BackupFilePath = BackupBox.Text.Trim()
            };
            await _runner.RunAsync($"Import '{name}'",
                (sp, reporter, ct) => sp.GetRequiredService<ImportProjectUseCase>().ExecuteAsync(import, reporter, ct));
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
