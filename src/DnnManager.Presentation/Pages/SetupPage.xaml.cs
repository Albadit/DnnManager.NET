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
/// "New project": a new project folder from a DNN download or an imported site .zip. A name whose folder already
/// exists is refused - setting up an existing folder is what Host project is for.
/// </summary>
public partial class SetupPage : UserControl, IRefreshable
{
    private readonly OperationRunner _runner;
    private readonly IProjectRepository _repo;
    private readonly DnnReleaseCatalog _catalog;
    private readonly IDnnPackageInstaller _packages;

    /// <summary>A DNN release source: its GitHub releases API URL, shown as <c>owner/repo</c>.</summary>
    public sealed record SourceOption(string Api, string Label);

    /// <summary>A version to install; <see cref="Release"/> is null for "the latest", used when the list couldn't be loaded.</summary>
    public sealed record VersionOption(DnnRelease? Release, string Label, bool Ready = true);

    // Which source's versions the list shows now - an answer for another source that arrives late is dropped.
    private string? _versionsFor;

    public SetupPage(OperationRunner runner, IProjectRepository repo, IDnnReleaseService releases, DnnReleaseCatalog catalog,
        IDnnPackageInstaller packages)
    {
        _runner = runner; _repo = repo; _catalog = catalog; _packages = packages;
        InitializeComponent();

        SourceCombo.ItemsSource = releases.KnownReleaseApis.Select(api => new SourceOption(api, RepositoryLabel(api))).ToList();
        SourceCombo.SelectedIndex = 0;
        LoadBackupProjects();
    }

    /// <summary><c>https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases</c> → <c>dnnsoftware/Dnn.Platform</c>.</summary>
    private static string RepositoryLabel(string api)
    {
        if (!Uri.TryCreate(api, UriKind.Absolute, out var uri)) return api;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "repos" ? $"{parts[1]}/{parts[2]}" : api;
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadVersions(refresh: false);

    private void RefreshVersions_Click(object sender, RoutedEventArgs e) => LoadVersions(refresh: true);

    /// <summary>
    /// Fills the version list for the chosen repository - from the list loaded when the app started, or asked of
    /// GitHub again with <paramref name="refresh"/>.
    /// </summary>
    private async void LoadVersions(bool refresh)
    {
        if (SourceCombo.SelectedItem is not SourceOption source) return;
        _versionsFor = source.Api;

        var lookup = _catalog.GetAsync(source.Api, refresh);
        if (!lookup.IsCompleted)
        {
            ShowVersions([new VersionOption(null, "Loading versions…", Ready: false)],
                $"Asking GitHub for the releases of {source.Label}…");
            RefreshVersionsButton.IsEnabled = false;
        }
        var result = await lookup;
        if (_versionsFor != source.Api) return; // another repository was picked meanwhile
        RefreshVersionsButton.IsEnabled = true;

        if (!result.Success)
        {
            // Still usable: the latest release is looked up when the project is set up.
            ShowVersions([new VersionOption(null, "Latest release")],
                $"Could not load the versions ({result.Error}) - the latest release is used. Refresh to try again.");
            return;
        }
        var releases = result.Value!;
        if (releases.Count == 0)
        {
            ShowVersions([], $"{source.Label} has no release with a DNN install package.");
            return;
        }
        ShowVersions(releases.Select((r, i) => new VersionOption(r,
                r.Version + (i == 0 ? "  (latest)" : "") + (_packages.IsKept(r) ? "  - kept, no download" : ""))).ToList(),
            $"{releases.Count} releases of {source.Label}, highest version first - the latest is selected.");
    }

    private void ShowVersions(IReadOnlyList<VersionOption> options, string hint)
    {
        VersionCombo.ItemsSource = options;
        VersionCombo.SelectedIndex = options.Count > 0 ? 0 : -1;
        VersionCombo.IsEnabled = options.Count > 0 && options[0].Ready;
        VersionHint.Text = hint;
        UpdateState();
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private string EnteredName => NameBox.Text.Trim();

    /// <summary>A dated backup in the "From a project backup" list.</summary>
    public sealed record BackupPick(ProjectBackup Backup, string Label);

    /// <summary>
    /// Fills the project list with the projects that have a complete backup (site zip + database) - including
    /// projects removed since, whose backups are kept.
    /// </summary>
    private void LoadBackupProjects()
    {
        var selected = BackupProjectCombo.SelectedItem as string;
        var projects = _repo.ListProjectsWithBackups()
            .Where(p => ProjectBackups.List(_repo.Build(p)).Any(b => b.IsComplete))
            .ToList();
        BackupProjectCombo.ItemsSource = projects;
        BackupProjectCombo.SelectedItem = projects.FirstOrDefault(p => p == selected);
        BackupProjectCombo.IsEnabled = projects.Count > 0;
        BackupPickHint.Text = projects.Count > 0
            ? @"A project and one of its backups (Documents\DnnManager\backups) - or choose the files anywhere on this PC below."
            : $"No project has a backup with site and database yet (Projects → right-click → Export) - choose the files below.";
    }

    private void BackupProject_Changed(object sender, SelectionChangedEventArgs e)
    {
        // The backup list belongs to the chosen project - nothing to pick from until one is chosen.
        if (BackupProjectCombo.SelectedItem is not string project)
        {
            ProjectBackupCombo.ItemsSource = null;
            ProjectBackupCombo.IsEnabled = false;
            return;
        }
        var backups = ProjectBackups.List(_repo.Build(project))
            .Where(b => b.IsComplete)
            .Select(b => new BackupPick(b, $"{b.Created:yyyy-MM-dd HH:mm:ss}  ({SizeMb(b.SiteZip!) + SizeMb(b.Database!):N1} MB)"))
            .ToList();
        ProjectBackupCombo.ItemsSource = backups;
        ProjectBackupCombo.IsEnabled = backups.Count > 0;
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
        UpdateState();
        LoadVersions(refresh: false); // from the kept list - only the "kept, no download" marks may have changed
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

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

        var suggested = Path.GetFileNameWithoutExtension(dialog.FileName).Replace(' ', '_');
        if (EnteredName.Length == 0 && ProjectName.Validate(suggested).Success) NameBox.Text = suggested;
    }

    private void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Database .bacpac", Filter = "Database backups (*.bacpac;*.bak)|*.bacpac;*.bak" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) BackupBox.Text = dialog.FileName;
    }

    private bool Importing => FromZip.IsChecked == true;

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

        // A new project needs a folder of its own - an existing one is set up on Host project.
        var exists = valid && _repo.ProjectExists(name);
        NameError.Text = name.Length > 0 && !valid ? check.Error ?? ""
            : exists ? $"A project named '{name}' already exists. Choose another name, or set it up on Host project."
            : "";
        NameError.Visibility = NameError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        valid = valid && !exists;

        var importing = Importing;
        var problem = importing ? ImportProblem() : null;
        ImportCard.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
        // Nothing chosen yet isn't an error - the disabled button says enough.
        var started = ZipBox.Text.Trim().Length > 0 || BackupBox.Text.Trim().Length > 0;
        ImportError.Text = problem is not null && started ? problem : "";
        ImportError.Visibility = ImportError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        PackageCard.Visibility = importing ? Visibility.Collapsed : Visibility.Visible;
        RunButton.Content = importing ? "Import project" : "Set up project";
        RunButton.IsEnabled = valid && (importing ? problem is null
            : SourceCombo.SelectedItem is SourceOption && VersionCombo.SelectedItem is VersionOption { Ready: true });
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var name = EnteredName;
        if (!ProjectName.Validate(name).Success || _repo.ProjectExists(name)) return;

        if (Importing)
        {
            if (ImportProblem() is not null) return;
            var import = new ImportProjectRequest
            {
                ProjectName = name,
                ZipPath = ZipBox.Text.Trim(),
                BackupFilePath = BackupBox.Text.Trim()
            };
            if (await _runner.RunAsync($"Import '{name}'",
                    (sp, reporter, ct) => sp.GetRequiredService<ImportProjectUseCase>().ExecuteAsync(import, reporter, ct)))
                Created();
            return;
        }

        if (SourceCombo.SelectedItem is not SourceOption source ||
            VersionCombo.SelectedItem is not VersionOption { Ready: true } version) return;
        var setup = new SetupProjectRequest
        {
            ProjectName = name,
            ReleaseApiUrl = source.Api,
            Version = version.Release?.TagName
        };
        if (await _runner.RunAsync($"Set up '{name}'",
                (sp, reporter, ct) => sp.GetRequiredService<SetupProjectUseCase>().ExecuteAsync(setup, reporter, ct)))
            Created();
    }

    /// <summary>Clears the name after a project is created, so the page is ready for the next one.</summary>
    private void Created()
    {
        NameBox.Text = "";
        ZipBox.Text = "";
        BackupBox.Text = "";
    }
}
