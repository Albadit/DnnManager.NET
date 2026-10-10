using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// "New project": a new project folder from a DNN download or an imported site .zip. A name whose folder already
/// exists is refused - setting up an existing folder is what Host project is for. A DNN download gets its IIS website
/// (host name, port), its database (named like the project, filled in from Settings → Database server and changeable
/// here for this project only - tested before anything is created) and - with automatic
/// setup - DNN installed with the account and website given here, so the first visit shows the new site. Whether the
/// host name and the database are free is checked as they are typed: <b>Create project</b> waits until both are.
/// </summary>
public partial class SetupPage : UserControl, IRefreshable
{
    private readonly OperationRunner _runner;
    private readonly IProjectRepository _repo;
    private readonly DnnReleaseCatalog _catalog;
    private readonly IDnnPackageInstaller _packages;
    private readonly AppOptions _options;
    private readonly ISecretStore _secrets;
    private readonly ServerStore _store;
    private readonly IDatabaseProvisioner _databases;

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
        IDnnPackageInstaller packages, IOptions<AppOptions> options, ISecretStore secrets, ServerStore store, IDatabaseProvisioner databases)
    {
        _runner = runner; _repo = repo; _catalog = catalog; _packages = packages;
        _options = options.Value; _secrets = secrets; _store = store; _databases = databases;
        InitializeComponent();
        _dbAsk.Tick += (_, _) => { _dbAsk.Stop(); AskDatabase(); };
        // Saving the settings can change the database server: the database follows it - unless it was changed here.
        // Followed only while the page is shown: a page MainWindow lets go of isn't kept alive by the settings. So are
        // the IIS sites - one added meanwhile may have the host name typed here.
        Loaded += (_, _) =>
        {
            _options.Changed += OnOptionsChanged;
            _store.Projects.CollectionChanged += OnSitesChanged;
            // Back on the page: the database is asked about again (it was let go when the page was left).
            UpdateState();
        };
        Unloaded += (_, _) =>
        {
            _options.Changed -= OnOptionsChanged;
            _store.Projects.CollectionChanged -= OnSitesChanged;
            ForgetDatabaseAnswer();
        };

        SourceCombo.ItemsSource = releases.KnownReleaseApis.Select(api => new SourceOption(api, RepositoryLabel(api))).ToList();
        SourceCombo.SelectedIndex = 0;
        LoadBackupProjects();
        LoadDefaults();
    }

    private void OnOptionsChanged()
    {
        if (!_dbEdited) LoadDatabaseDefaults();
        UpdateState();
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
            LanguageCombo.Items.Add(new ComboBoxItem { Content = Languages.Name(language), Tag = language });
        LanguageCombo.SelectedItem = LanguageCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == dnn.Language) ?? LanguageCombo.Items[0];
        TemplateCombo.Items.Clear();
        foreach (var template in DnnAccountRules.Templates) TemplateCombo.Items.Add(new ComboBoxItem { Content = template, Tag = template });
        TemplateCombo.SelectedItem = TemplateCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == dnn.Template) ?? TemplateCombo.Items[0];
        PortBox.Text = _options.SitePort.ToString();
        _hostEdited = false;
        _filling = false;
        LoadDatabaseDefaults();
        FollowName();
    }

    /// <summary><c>https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases</c> → <c>dnnsoftware/Dnn.Platform</c>.</summary>
    private static string RepositoryLabel(string api)
    {
        if (!Uri.TryCreate(api, UriKind.Absolute, out var uri)) return api;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "repos" ? $"{parts[1]}/{parts[2]}" : api;
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadVersions();

    /// <summary>
    /// Fills the version list for the chosen repository - from the list loaded when the app started; asked of GitHub
    /// again when that couldn't reach it.
    /// </summary>
    private async void LoadVersions()
    {
        if (SourceCombo.SelectedItem is not SourceOption source) return;
        _versionsFor = source.Api;

        var lookup = _catalog.GetAsync(source.Api);
        if (!lookup.IsCompleted)
            ShowVersions([new VersionOption(null, "Loading versions…", Ready: false)],
                $"Asking GitHub for the releases of {source.Label}…");
        var result = await lookup;
        if (_versionsFor != source.Api) return; // another repository was picked meanwhile

        if (!result.Success)
        {
            // Still usable: the latest release is looked up when the project is set up.
            ShowVersions([new VersionOption(null, "Latest release")],
                $"Could not load the versions ({result.Error}) - the latest release is used. GitHub is asked again when you come back to this page." +
                (_options.KeepDnnPackages ? "" : " To set up projects without internet, turn on Settings → DNN releases → Keep downloaded DNN install packages while online."));
            return;
        }
        var releases = result.Value!.Releases;
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
            result.Value.SavedAt is { } savedAt
                ? $"GitHub can't be reached - the releases of {source.Label} as of {savedAt:g}. Without internet only a version marked \"kept, no download\" can be set up. GitHub is asked again when you come back to this page."
                : $"{releases.Count} releases of {source.Label}, highest version first - the latest release is selected.",
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
        // Shown again: the database may have been made or removed meanwhile - asked anew.
        ForgetDatabaseAnswer();
        UpdateState();
        LoadVersions(); // from the kept list - only the "kept, no download" marks may have changed; GitHub again if it failed
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
        if (!_dbNameEdited) DbNameBox.Text = name.Length > 0 ? _options.DatabaseNameFor(name) : "";
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

    // A host name of the user's own - not one under the host name suffix (mysite.dnndev.me).
    private bool IsCustomDomain(string host) =>
        host.Length > 0 && !host.EndsWith("." + _options.HostnameSuffix, StringComparison.OrdinalIgnoreCase) &&
        !host.Equals(_options.HostnameSuffix, StringComparison.OrdinalIgnoreCase);

    private int? ChosenPort => int.TryParse(PortBox.Text.Trim(), out var port) && port is > 0 and <= 65535 ? port : null;

    private string? IisProblem()
    {
        var host = ChosenHostName;
        if (host.Length == 0) return "Enter the host name the site answers on.";
        if (host.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-'))) return "The host name can only have letters, digits, '.' and '-'.";
        if (ChosenPort is not { } port) return "The port must be a number between 1 and 65535.";
        // From the sites the window shows - IIS isn't asked at every key. The setup asks IIS itself again.
        if (_store.IsLoaded &&
            IisHostNames.SiteUsing(_store.Projects.Select(r => KeyValuePair.Create(r.Name, r.IisSite)), host, port, EnteredName) is { } other)
            return $"http://{DnnSiteAddress.AliasFor(host, port)} is already the address of the IIS site '{other}' - choose another host name or port.";
        return null;
    }

    /// <summary>
    /// The folder of the IIS site named <paramref name="name"/> - from the sites the window shows; the setup asks IIS itself
    /// again. A new project's folder doesn't exist yet, so a site of that name serves another one. Null when there is none.
    /// </summary>
    private string? SiteServingAnotherFolder(string name)
    {
        if (!_store.IsLoaded) return null;
        var row = _store.Projects.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row is null) return null;
        var folder = _repo.Build(name).ProjectDirectory;
        return string.Equals(row.Path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? null
            : row.Path.Length > 0 ? row.Path : "another folder";
    }

    private void OnSitesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

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

    private const string ContainerType = SqlServerSettings.ContainerType, SqlServerType = "sqlServer", LocalDbType = "localDbFile";

    // The connection type shown; whether a database field was changed here (then the settings don't overwrite it), and
    // whether the database name was typed (then it no longer follows the project's name).
    private string _dbType = ContainerType;
    private bool _dbEdited, _dbNameEdited;

    /// <summary>
    /// The database as Settings → Database server has it - its connection type and that type's server and login (the
    /// SQL Server login's password from the Credential Manager, held in memory only). Changed here, for this project only.
    /// </summary>
    private void LoadDatabaseDefaults()
    {
        var server = Server;
        FillDatabase(server.IsContainer ? ContainerType : server.IsLocalDbFile ? LocalDbType : SqlServerType);
        _dbEdited = false;
    }

    /// <summary>Shows <paramref name="type"/> with the settings' server and login for it.</summary>
    private void FillDatabase(string type)
    {
        var server = Server;
        var filling = _filling;
        _filling = true;
        _dbType = type;
        DbContainer.IsChecked = type == ContainerType;
        DbSqlServer.IsChecked = type == SqlServerType;
        DbLocalDb.IsChecked = type == LocalDbType;
        switch (type)
        {
            case ContainerType:
                DbServerBox.Text = ContainerServer;
                DbUserBox.Text = _options.Docker.SqlUser;
                DbPasswordBox.Password = _options.Docker.SaPassword;
                break;
            case LocalDbType:
                DbServerBox.Text = server.IsLocalDbFile && server.Server.Length > 0 ? server.Server : DatabaseConnection.LocalDbServer;
                break;
            default:
                var saved = server.Type.Equals(SqlServerType, StringComparison.OrdinalIgnoreCase);
                DbServerBox.Text = saved && server.Server.Length > 0 ? server.Server : @".\SQLEXPRESS";
                DbWindowsAuth.IsChecked = !(saved && server.UsesSqlAuthentication);
                DbSqlAuth.IsChecked = saved && server.UsesSqlAuthentication;
                DbUserBox.Text = saved ? server.UserName : "";
                DbPasswordBox.Password = saved && server.UsesSqlAuthentication ? _secrets.Read(SecretNames.DatabaseServerPassword) ?? "" : "";
                break;
        }
        _filling = filling;
    }

    private void DbType_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _filling) return;
        var type = DbSqlServer.IsChecked == true ? SqlServerType : DbLocalDb.IsChecked == true ? LocalDbType : ContainerType;
        if (type == _dbType) return;
        // Another type: its server and login as the settings have them.
        FillDatabase(type);
        _dbEdited = true;
        UpdateState();
    }

    private void DbAuth_Checked(object sender, RoutedEventArgs e) => DbEdited();

    private void DbField_Changed(object sender, TextChangedEventArgs e) => DbEdited();

    private void DbPassword_Changed(object sender, RoutedEventArgs e) => DbEdited();

    private void DbName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_filling) _dbNameEdited = DbNameBox.Text.Trim().Length > 0;
        DbEdited();
    }

    private void DbEdited()
    {
        if (!IsInitialized) return;
        if (!_filling) _dbEdited = true;
        UpdateState();
    }

    private void ResetDatabase_Click(object sender, RoutedEventArgs e)
    {
        LoadDatabaseDefaults();
        _dbNameEdited = false;
        FollowName();
        UpdateState();
    }

    private bool DbSqlLogin => _dbType == ContainerType || (_dbType == SqlServerType && DbSqlAuth.IsChecked == true);

    /// <summary>What's wrong with the database fields, or null when they can be tested.</summary>
    private string? DatabaseProblem()
    {
        if (DbServerBox.Text.Trim().Length == 0) return _dbType == LocalDbType ? "Enter the LocalDB instance." : "Enter the server.";
        if (_dbType != LocalDbType)
        {
            var name = DbNameBox.Text.Trim();
            if (name.Length == 0) return "Enter the database name.";
            if (name.Length > 128 || name.Contains(']')) return "The database name can have at most 128 characters, and no ']'.";
        }
        if (DbSqlLogin && DbUserBox.Text.Trim().Length == 0) return "Enter the username.";
        if (DbSqlLogin && DbPasswordBox.Password.Length == 0) return "Enter the password.";
        return null;
    }

    /// <summary>The new project's database, as the Database card has it.</summary>
    private DatabaseConnection ChosenDatabase()
    {
        var server = DbServerBox.Text.Trim();
        var database = DbNameBox.Text.Trim();
        return _dbType switch
        {
            ContainerType => new DatabaseConnection(DatabaseKind.Container, server, database, SqlAuthentication.Sql, DbUserBox.Text.Trim(), DbPasswordBox.Password),
            LocalDbType => new DatabaseConnection(DatabaseKind.LocalDbFile, server, DatabaseConnection.LocalDbFileName, SqlAuthentication.Windows),
            _ => DbSqlAuth.IsChecked == true
                ? new DatabaseConnection(DatabaseKind.SqlServer, server, database, SqlAuthentication.Sql, DbUserBox.Text.Trim(), DbPasswordBox.Password)
                : new DatabaseConnection(DatabaseKind.SqlServer, server, database, SqlAuthentication.Windows)
        };
    }

    // ─── Is the database free? ────────────────────────────────────────────

    private enum DbAnswer { None, Asking, Free, Exists, NoAnswer }

    // Asked of the server once the database fields have been still for a moment: a database that already exists, or a
    // server that doesn't answer, is said here - not after DNN has been downloaded. The answer is about the connection
    // it was asked with (_dbAsked); a server that couldn't be asked (not running, the login refused) is asked again every
    // 10 seconds while the page shows.
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(10);
    private readonly DispatcherTimer _dbAsk = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private DatabaseConnection? _dbAsked;
    private DbAnswer _dbAnswer;
    private string? _dbAnswerDetail;
    private CancellationTokenSource? _dbAskCancel;

    /// <summary>Asks about <paramref name="connection"/> a moment from now, unless that is what was asked already.</summary>
    private void FollowDatabase(DatabaseConnection connection)
    {
        if (connection == _dbAsked) return;
        ForgetDatabaseAnswer();
        _dbAsked = connection;
        _dbAnswer = DbAnswer.Asking;
        _dbAsk.Start();
    }

    private void ForgetDatabaseAnswer()
    {
        _dbAsk.Stop();
        _dbAskCancel?.Cancel();
        _dbAskCancel = null;
        _dbAsked = null;
        _dbAnswer = DbAnswer.None;
        _dbAnswerDetail = null;
    }

    private async void AskDatabase()
    {
        if (_dbAsked is not { } asked || !IsLoaded) return;
        var cancel = new CancellationTokenSource();
        _dbAskCancel = cancel;
        Result<bool> exists;
        try
        {
            exists = await Task.Run(() => _databases.DatabaseExistsAsync(asked, cancel.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // Another connection typed meanwhile - this answer is about the old one.
        if (cancel.IsCancellationRequested || asked != _dbAsked) return;
        (_dbAnswer, _dbAnswerDetail) = !exists.Success ? (DbAnswer.NoAnswer, exists.Error) : exists.Value ? (DbAnswer.Exists, null) : (DbAnswer.Free, null);
        UpdateState();
        if (_dbAnswer != DbAnswer.NoAnswer) return;
        // The container may be starting - asked again while nothing changes (a change cancels this wait).
        try
        {
            await Task.Delay(AskAgainAfter, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_dbAnswer == DbAnswer.NoAnswer && IsLoaded) AskDatabase();
    }

    /// <summary>
    /// Whether the database lets the project be created: a LocalDB file is the site's own; another is free - or, for a
    /// manual install (which may go into a database that is there), exists.
    /// </summary>
    private bool DatabaseFree => _dbType == LocalDbType || _dbAnswer == DbAnswer.Free || (_dbAnswer == DbAnswer.Exists && !Automatic);

    /// <summary>The card's fields for the connection type, its hint and its problem.</summary>
    private void ShowDatabase(bool started)
    {
        DbServerLabel.Text = _dbType == LocalDbType ? "LocalDB instance" : "Server";
        DbNamePanel.Visibility = _dbType == LocalDbType ? Visibility.Collapsed : Visibility.Visible;
        DbAuthPanel.Visibility = _dbType == SqlServerType ? Visibility.Visible : Visibility.Collapsed;
        DbUserPanel.Visibility = DbPasswordPanel.Visibility = DbSqlLogin ? Visibility.Visible : Visibility.Collapsed;
        ResetDatabaseButton.Visibility = _dbEdited || _dbNameEdited ? Visibility.Visible : Visibility.Collapsed;

        var problem = DatabaseProblem();
        var database = $"[{DbNameBox.Text.Trim()}] on {DbServerBox.Text.Trim()}";
        // An empty name isn't an error yet - the database name follows it.
        DatabaseError.Text = problem is not null ? (started || _dbEdited ? problem : "")
            : _dbAnswer == DbAnswer.Exists && Automatic ? $"Database {database} already exists - choose another name, or remove that database first."
            : _dbAnswer == DbAnswer.NoAnswer
                ? _dbAnswerDetail?.Contains("Login failed", StringComparison.OrdinalIgnoreCase) == true
                    ? $"SQL Server at {DbServerBox.Text.Trim()} refuses the login."
                    : $"Can't reach SQL Server at {DbServerBox.Text.Trim()}."
            : "";
        // One line here; SqlClient's own words on hover (and asked again every few seconds).
        DatabaseError.ToolTip = _dbAnswer == DbAnswer.NoAnswer && problem is null
            ? $"{_dbAnswerDetail}\n\nAsked again every {AskAgainAfter.TotalSeconds:0} seconds."
            : null;
        DatabaseError.Visibility = DatabaseError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var answer = problem is not null ? "" : _dbAnswer switch
        {
            DbAnswer.Asking => $"Checking whether database {database} is free… ",
            DbAnswer.Free => $"Database {database} is free. ",
            DbAnswer.Exists when !Automatic => $"Database {database} already exists - the setup asks whether to drop it or install into it. ",
            _ => ""
        };
        DatabaseHint.Text = (answer + (_dbType == LocalDbType
                ? $@"The site's own App_Data\{DatabaseConnection.LocalDbFileName}. "
                : "") +
            (_dbEdited ? "For this project only." : "")).Trim();
        DatabaseHint.Visibility = DatabaseHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── State and running ────────────────────────────────────────────────

    private void UpdateState()
    {
        if (!IsInitialized || RunButton is null) return;
        var name = EnteredName;
        var check = ProjectName.Validate(name);
        var valid = check.Success;

        // A new project needs a folder of its own - an existing one is set up on Host project.
        var exists = valid && _repo.ProjectExists(name);
        // Nor an IIS site of that name serving another folder: making the project's site would replace it.
        var siteElsewhere = valid && !exists ? SiteServingAnotherFolder(name) : null;
        NameError.Text = name.Length > 0 && !valid ? check.Error ?? ""
            : exists ? $"A project named '{name}' already exists. Choose another name, or set it up on Host project."
            : siteElsewhere is not null ? $"IIS already has a site named '{name}', serving {siteElsewhere} - choose another name."
            : "";
        NameError.Visibility = NameError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        valid = valid && !exists && siteElsewhere is null;

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
                       $"http://{DnnSiteAddress.AliasFor(ChosenHostName.Length > 0 ? ChosenHostName : "<host>", port ?? 80)} - " +
                       "DNN Manager adds the host name to this PC's hosts file, so it opens without internet too." +
                       (IsCustomDomain(ChosenHostName) ? " A custom domain then opens this site on this PC, not what the internet has at that name." : "");
        IisHint.SetResourceReference(TextBlock.ForegroundProperty, shownIisProblem is null ? "TextMuted" : "ErrorText");

        DatabaseCard.Visibility = fresh ? Visibility.Visible : Visibility.Collapsed;
        var databaseProblem = fresh ? DatabaseProblem() : null;
        if (fresh && databaseProblem is null && _dbType != LocalDbType) FollowDatabase(ChosenDatabase());
        else ForgetDatabaseAnswer();
        if (fresh) ShowDatabase(name.Length > 0);

        var accountProblems = fresh && Automatic ? DnnAccountRules.Problems(ChosenAccount) : [];
        // Only once something is typed - an empty form isn't an error yet.
        var accountStarted = HostPasswordBox.Password.Length > 0 || name.Length > 0;
        AccountError.Text = accountStarted && accountProblems.Count > 0 ? string.Join(" ", accountProblems) : "";
        AccountError.Visibility = AccountError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "en-US";
        AccountHint.Text = "The host (superuser) account signs in without being asked to change its password. Defaults in Settings → Projects → DNN defaults." +
                           (language == "en-US" ? "" : $" {Languages.Name(language)}: DNN downloads its language pack while installing (needs internet).");

        NameHint.Text = "Letters, digits, '-', '_' or '.' - the name becomes the folder, the IIS site, the hostname and the database" +
                        (importing ? $", on the local SQL container ({ContainerServer})." : " (see Database below).");

        RunButton.Content = importing ? "Import project" : "Create project";
        var versionReady = SourceCombo.SelectedItem is SourceOption && VersionCombo.SelectedItem is VersionOption { Ready: true };
        RunButton.IsEnabled = valid && (importing ? problem is null
            : versionReady && iisProblem is null && databaseProblem is null && DatabaseFree && accountProblems.Count == 0);

        // What is still missing, next to the button - in the order of the cards above.
        var needed = new List<string>();
        if (!valid) needed.Add("a project name");
        if (importing && problem is not null) needed.Add("the site's .zip and its database backup");
        if (!importing && !versionReady) needed.Add("a DNN version");
        if (!importing && iisProblem is not null) needed.Add("a host name and port");
        if (!importing && (databaseProblem is not null || !DatabaseFree)) needed.Add("a database");
        if (!importing && accountProblems.Count > 0) needed.Add("the host account");
        StillNeeded.Text = RunButton.IsEnabled || needed.Count == 0 ? "" : "Still needed: " + string.Join(", ", needed) + ".";
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
            {
                Toast.Show($"'{name}' is imported - it is on the Projects page.", ToastKind.Success);
                Created();
            }
            return;
        }

        if (SourceCombo.SelectedItem is not SourceOption source ||
            VersionCombo.SelectedItem is not VersionOption { Ready: true } version ||
            IisProblem() is not null || DatabaseProblem() is not null || !DatabaseFree) return;
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
                ToastKind.Success, "Open site", () => Shell.Open(url));
            Created();
        }
    }


    /// <summary>Clears the name after a project is created, so the page is ready for the next one.</summary>
    private void Created()
    {
        NameBox.Text = "";
        ZipBox.Text = "";
        BackupBox.Text = "";
        _hostEdited = false;
        _websiteEdited = _options.DnnDefaults.WebsiteName.Length > 0;
        // The next project's database is named like it again - on the server chosen here, which stays.
        _dbNameEdited = false;
        FollowName();
    }
}
