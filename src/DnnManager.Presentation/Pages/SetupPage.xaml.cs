using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// "New project": a new project folder from a DNN download or an imported site .zip. A name whose folder already
/// exists is refused - setting up an existing folder is what Host project is for. A DNN download gets its IIS website
/// (host name, port), its database (named like the project, on the server from Settings → Database server - tested before
/// anything is created) and - with automatic
/// setup - DNN installed with the account and website given here, so the first visit shows the new site.
/// </summary>
public partial class SetupPage : UserControl, IRefreshable
{
    private readonly OperationRunner _runner;
    private readonly IProjectRepository _repo;
    private readonly DnnReleaseCatalog _catalog;
    private readonly IDnnPackageInstaller _packages;
    private readonly AppOptions _options;
    private readonly ISecretStore _secrets;

    /// <summary>A DNN release source: its GitHub releases API URL, shown as <c>owner/repo</c>.</summary>
    public sealed record SourceOption(string Api, string Label);

    /// <summary>A version to install; <see cref="Release"/> is null for "the latest", used when the list couldn't be loaded.</summary>
    public sealed record VersionOption(DnnRelease? Release, string Label, bool Ready = true);

    // Which source's versions the list shows now - an answer for another source that arrives late is dropped.
    private string? _versionsFor;
    // Fields that follow the project's name until they are edited.
    private bool _hostEdited, _websiteEdited;
    // Set while code fills fields in, so that isn't taken for an edit.
    private bool _filling;

    public SetupPage(OperationRunner runner, IProjectRepository repo, IDnnReleaseService releases, DnnReleaseCatalog catalog,
        IDnnPackageInstaller packages, IOptions<AppOptions> options, ISecretStore secrets)
    {
        _runner = runner; _repo = repo; _catalog = catalog; _packages = packages;
        _options = options.Value; _secrets = secrets;
        InitializeComponent();
        // Saving the settings can change the database server - the hint that names it follows.
        _options.Changed += UpdateState;

        SourceCombo.ItemsSource = releases.KnownReleaseApis.Select(api => new SourceOption(api, RepositoryLabel(api))).ToList();
        SourceCombo.SelectedIndex = 0;
        LoadBackupProjects();
        LoadDefaults();
    }

    /// <summary>The settings' DNN defaults and database server, and the site address for no name yet.</summary>
    private void LoadDefaults()
    {
        _filling = true;
        var dnn = _options.DnnDefaults;
        AutomaticInstall.IsChecked = dnn.Automatic;
        ManualInstall.IsChecked = !dnn.Automatic;
        HostUserBox.Text = dnn.HostUsername;
        HostPasswordBox.Password = _secrets.Read(SecretNames.DefaultHostPassword) ?? DnnDefaultsSettings.DefaultHostPassword;
        HostEmailBox.Text = _options.DefaultHostEmail;
        WebsiteNameBox.Text = dnn.WebsiteName;
        _websiteEdited = dnn.WebsiteName.Length > 0;
        LanguageCombo.Items.Clear();
        foreach (var language in DnnAccountRules.Languages)
            LanguageCombo.Items.Add(new ComboBoxItem { Content = LanguageName(language), Tag = language });
        LanguageCombo.SelectedItem = LanguageCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == dnn.Language) ?? LanguageCombo.Items[0];
        TemplateCombo.Items.Clear();
        foreach (var template in DnnAccountRules.Templates) TemplateCombo.Items.Add(new ComboBoxItem { Content = template, Tag = template });
        TemplateCombo.SelectedItem = TemplateCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == dnn.Template) ?? TemplateCombo.Items[0];
        PortBox.Text = _options.SitePort.ToString();
        _hostEdited = false;
        _filling = false;
        FollowName();
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
        // A pre-release can be newer than the latest release - it's listed, but the latest release stays the default.
        var latest = releases.FirstOrDefault(r => !r.Prerelease) ?? releases[0];
        ShowVersions(releases.Select(r => new VersionOption(r,
                r.Version + (r == latest ? "  (latest)" : "") + (r.Prerelease ? "  (pre-release)" : "")
                + (_packages.IsKept(r) ? "  - kept, no download" : ""))).ToList(),
            $"{releases.Count} releases of {source.Label}, highest version first - the latest release is selected.",
            selected: releases.ToList().IndexOf(latest));
    }

    private void ShowVersions(IReadOnlyList<VersionOption> options, string hint, int selected = 0)
    {
        VersionCombo.ItemsSource = options;
        VersionCombo.SelectedIndex = options.Count > 0 ? selected : -1;
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
        // Nothing to pick from: the files are chosen below.
        BackupPickPanel.Visibility = projects.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FollowName();
        UpdateState();
    }

    /// <summary>The host name and website name are the project's name until they are typed in.</summary>
    private void FollowName()
    {
        if (!IsInitialized || _filling) return;
        var name = EnteredName;
        _filling = true;
        if (!_hostEdited) HostNameBox.Text = name.Length > 0 ? _options.HostnameFor(name) : "";
        if (!_websiteEdited) WebsiteNameBox.Text = name;
        _filling = false;
    }

    private void HostName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_filling) _hostEdited = HostNameBox.Text.Trim().Length > 0;
        UpdateState();
    }

    private void WebsiteName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_filling) _websiteEdited = WebsiteNameBox.Text.Trim().Length > 0;
        UpdateState();
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => UpdateState();

    private void Password_Changed(object sender, RoutedEventArgs e) => UpdateState();

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void Start_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdateState();
    }

    private void Install_Checked(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) UpdateState();
    }

    private void GeneratePassword_Click(object sender, RoutedEventArgs e)
    {
        HostPasswordBox.Password = DnnAccountRules.GeneratePassword();
        HostPasswordBox.Reveal();
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

    private bool Automatic => AutomaticInstall.IsChecked == true;

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

    // ─── The site and the account ─────────────────────────────────────────

    private string ChosenHostName => HostNameBox.Text.Trim();

    private int? ChosenPort => int.TryParse(PortBox.Text.Trim(), out var port) && port is > 0 and <= 65535 ? port : null;

    private string? IisProblem()
    {
        var host = ChosenHostName;
        if (host.Length == 0) return "Enter the host name the site answers on.";
        if (host.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-'))) return "The host name can only have letters, digits, '.' and '-'.";
        return ChosenPort is null ? "The port must be a number between 1 and 65535." : null;
    }

    private DnnAccount ChosenAccount => new(
        HostUserBox.Text.Trim(),
        HostPasswordBox.Password,
        HostEmailBox.Text.Trim(),
        WebsiteNameBox.Text.Trim().Length > 0 ? WebsiteNameBox.Text.Trim() : EnteredName,
        (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "en-US",
        (TemplateCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? DnnAccountRules.Templates[0]);

    // ─── The database ─────────────────────────────────────────────────────

    private DatabaseServerOptions Server => _options.DatabaseServer;

    private string ContainerServer => _options.ServerFor(_options.Docker.DefaultPort);

    /// <summary>Where the new project's database goes, in words - for the hint under the name.</summary>
    private string DatabaseWhere(bool importing)
    {
        var server = Server;
        if (importing || server.IsContainer) return $"on the local SQL container ({ContainerServer})";
        if (server.IsLocalDbFile) return $@"as the site's own App_Data\{DatabaseConnection.LocalDbFileName} (LocalDB)";
        return $"on {server.Server}";
    }

    /// <summary>
    /// The new project's database: named like the project, on the server from the settings. The login's password is read
    /// from the Credential Manager and held in memory only.
    /// </summary>
    private DatabaseConnection ChosenDatabase() =>
        _options.DatabaseOnServer(_options.DatabaseNameFor(EnteredName),
            Server.UsesSqlAuthentication ? _secrets.Read(SecretNames.DatabaseServerPassword) ?? "" : "");

    // ─── State and running ────────────────────────────────────────────────

    private void UpdateState()
    {
        if (!IsInitialized || RunButton is null) return;
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

        // An imported site brings its own installed DNN and goes onto the local container, as before.
        var fresh = !importing;
        PackageCard.Visibility = IisCard.Visibility = InstallCard.Visibility =
            fresh ? Visibility.Visible : Visibility.Collapsed;
        AccountCard.Visibility = fresh && Automatic ? Visibility.Visible : Visibility.Collapsed;

        var iisProblem = fresh ? IisProblem() : null;
        var port = ChosenPort;
        // An empty host name isn't an error yet - it follows the project's name, and the disabled button says enough.
        // A bad port is shown at once.
        var shownIisProblem = ChosenHostName.Length > 0 ? iisProblem
            : fresh && port is null ? "The port must be a number between 1 and 65535." : null;
        IisHint.Text = shownIisProblem ?? $"The IIS website and its app pool are named '{(name.Length > 0 ? name : "<project>")}'; the site answers at " +
                       $"http://{DnnSiteAddress.AliasFor(ChosenHostName.Length > 0 ? ChosenHostName : "<host>", port ?? 80)}";
        IisHint.SetResourceReference(TextBlock.ForegroundProperty, shownIisProblem is null ? "TextMuted" : "ErrorText");

        var accountProblems = fresh && Automatic ? DnnAccountRules.Problems(ChosenAccount) : [];
        // Only once something is typed - an empty form isn't an error yet.
        var accountStarted = HostPasswordBox.Password.Length > 0 || name.Length > 0;
        AccountError.Text = accountStarted && accountProblems.Count > 0 ? string.Join(" ", accountProblems) : "";
        AccountError.Visibility = AccountError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "en-US";
        AccountHint.Text = "The host (superuser) account signs in without being asked to change its password. Defaults in Settings → Projects → DNN defaults." +
                           (language == "en-US" ? "" : $" {LanguageName(language)}: DNN downloads its language pack while installing (needs internet).");

        NameHint.Text = "Letters, digits, '-', '_' or '.' - the name becomes the folder, the IIS site, the hostname and the database, " +
                        $"{DatabaseWhere(importing)}." + (importing ? "" : " The database server is set in Settings → Database server.");

        RunButton.Content = importing ? "Import project" : "Create project";
        RunButton.IsEnabled = valid && (importing ? problem is null
            : SourceCombo.SelectedItem is SourceOption && VersionCombo.SelectedItem is VersionOption { Ready: true } &&
              iisProblem is null && accountProblems.Count == 0);
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
            VersionCombo.SelectedItem is not VersionOption { Ready: true } version ||
            IisProblem() is not null) return;
        var automatic = Automatic;
        var account = ChosenAccount;
        if (automatic && DnnAccountRules.Problems(account).Count > 0) return;

        var setup = new SetupProjectRequest
        {
            ProjectName = name,
            ReleaseApiUrl = source.Api,
            Version = version.Release?.TagName,
            HostName = ChosenHostName,
            Port = ChosenPort,
            InstallMode = automatic ? DnnInstallMode.Automatic : DnnInstallMode.Manual,
            Account = automatic ? account : null,
            Database = ChosenDatabase()
        };
        if (await _runner.RunAsync($"Create '{name}'",
                (sp, reporter, ct) => sp.GetRequiredService<SetupProjectUseCase>().ExecuteAsync(setup, reporter, ct)))
        {
            var url = $"http://{DnnSiteAddress.AliasFor(setup.HostName!, setup.Port ?? 80)}";
            Toast.Show(automatic ? $"'{name}' is ready - DNN is installed. Sign in as '{account.UserName}'." : $"'{name}' is set up - open it to run DNN's installation wizard.",
                ToastKind.Success, "Open site", () => ProjectMenuShell(url));
            Created();
        }
    }

    private static void ProjectMenuShell(string url) => Projects.ProjectMenu.Shell(url);

    /// <summary>Clears the name after a project is created, so the page is ready for the next one.</summary>
    private void Created()
    {
        NameBox.Text = "";
        ZipBox.Text = "";
        BackupBox.Text = "";
        _hostEdited = false;
        _websiteEdited = _options.DnnDefaults.WebsiteName.Length > 0;
        FollowName();
    }

    /// <summary>"nl-NL" → "Nederlands (nl-NL)".</summary>
    private static string LanguageName(string culture)
    {
        try
        {
            var info = System.Globalization.CultureInfo.GetCultureInfo(culture);
            return $"{char.ToUpper(info.NativeName[0])}{info.NativeName[1..]} ({culture})";
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return culture;
        }
    }
}
