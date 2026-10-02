using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.KeepWarm;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages.Projects;

/// <summary>How a fact on the overview looks: a section heading, or a value in the normal, good (green), bad (red) or warning colour.</summary>
public enum FactKind { Normal, Section, Good, Bad, Warning }

/// <summary>A label and its value on a site's overview.</summary>
public sealed record ProjectFact(string Label, string Value, FactKind Kind = FactKind.Normal)
{
    public static ProjectFact Section(string title) => new(title, "", FactKind.Section);
}

/// <param name="Url">What a click opens - the alias with http or https, as the site's bindings serve it.</param>
public sealed record AliasLink(string Url);

/// <summary>One DNN portal of the site's installation, as its overview lists it.</summary>
public sealed class PortalItem : INotifyPropertyChanged
{
    private bool _showOthers;

    public required int Id { get; init; }
    public required string Name { get; init; }
    public required bool Expired { get; init; }
    public AliasLink? Primary { get; init; }
    public IReadOnlyList<AliasLink> Others { get; init; } = [];

    public string IdText => $"Portal {Id}";
    public string Status => Expired ? "Expired" : "Active";
    public bool HasPrimary => Primary is not null;
    public bool HasNoAlias => Primary is null;
    public bool HasOthers => Others.Count > 0;
    public string MoreText => ShowOthers ? "Hide the other aliases" : Others.Count == 1 ? "Show 1 more alias" : $"Show {Others.Count} more aliases";

    public bool ShowOthers
    {
        get => _showOthers;
        set
        {
            if (_showOthers == value) return;
            _showOthers = value;
            Raise();
            Raise(nameof(MoreText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// A site's overview - opened from the Projects table - in tabs. What IIS has (state, app pool, folder, bindings) and its
/// keep warm follow the site live through its row; for a DNN site how it was installed, its host accounts and the portals
/// of its installation (each with links to its addresses) are read from its database, which Test connection checks; the
/// folder's and the web.config's facts. No password is shown anywhere - the host password can only be changed.
/// </summary>
public partial class ProjectView : UserControl
{
    private readonly ProjectRow _row;
    private readonly IServiceProvider _services;
    private readonly Action<SiteAction> _control;

    /// <param name="control">Starts, stops or restarts the site - the table's row actions.</param>
    public ProjectView(ProjectRow row, IServiceProvider services, Action<SiteAction> control)
    {
        _row = row; _services = services; _control = control;
        InitializeComponent();
        DataContext = row;
        Loaded += (_, _) => Focus();
        Focusable = true;
        // The keep-warm card follows the row while the overview is shown - and lets it go when it closes.
        Loaded += (_, _) =>
        {
            _row.PropertyChanged += Row_PropertyChanged;
            ShowKeepWarm(fillValues: true);
        };
        Unloaded += (_, _) => _row.PropertyChanged -= Row_PropertyChanged;
        _ = LoadAsync();
    }

    /// <summary>Back was pressed (or Esc) - the page shows the table again.</summary>
    public event EventHandler? BackRequested;

    public ProjectRow Row => _row;

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    // The site's database as its web.config has it (null: none of its own yet), and its host accounts as read.
    private DatabaseConnection? _database;
    private IReadOnlyList<string> _hosts = [];

    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        GeneralSection.Visibility = GeneralTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        IisSection.Visibility = IisTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DnnSection.Visibility = DnnTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DatabaseSection.Visibility = DatabaseTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AdvancedSection.Visibility = AdvancedTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape) return;
        BackRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => _control(SiteAction.Start);
    private void Stop_Click(object sender, RoutedEventArgs e) => _control(SiteAction.Stop);
    private void Restart_Click(object sender, RoutedEventArgs e) => _control(SiteAction.Restart);

    private void More_Click(object sender, RoutedEventArgs e) => ProjectMenu.ShowSiteTools(MoreButton, _services, _row);
    private void OpenSite_Click(object sender, RoutedEventArgs e) => ProjectMenu.Shell(_row.Url);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_row.Path)) ProjectMenu.Shell(_row.Path);
        else Dialogs.Error($"The site's folder doesn't exist: {_row.Path}");
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && url.Length > 0) ProjectMenu.Shell(url);
    }

    // ─── Keep warm ────────────────────────────────────────────────────────

    // Set while the site's own values are filled in, so that doesn't count as an edit.
    private bool _showingKeepWarm;

    private KeepWarmService KeepWarm => _services.GetRequiredService<KeepWarmService>();
    private KeepWarmSettings KeepWarmDefaults => _services.GetRequiredService<IOptions<AppOptions>>().Value.KeepWarm;

    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Its status, or the site's bindings and app pool (the idle time-out) - what the facts are made of.
        if (e.PropertyName is nameof(ProjectRow.KeepWarm) or nameof(ProjectRow.BindingsText) or nameof(ProjectRow.AppPoolText))
            ShowKeepWarm(fillValues: false);
    }

    /// <summary>How the site is kept warm - and, when <paramref name="fillValues"/>, the site's own values in their fields.</summary>
    private void ShowKeepWarm(bool fillValues)
    {
        var defaults = KeepWarmDefaults;
        var record = KeepWarm.RecordOf(_row.Name);
        KeepWarmFacts.ItemsSource = KeepWarmFactsOf(_row.Name, KeepWarmPlan.For(defaults, record, _row.IisSite));
        if (!fillValues) return;

        _showingKeepWarm = true;
        KeepWarmInterval.Items.Clear();
        KeepWarmInterval.Items.Add(new ComboBoxItem { Content = $"Settings ({KeepWarmRules.Span(TimeSpan.FromMinutes(defaults.PingMinutes))})" });
        var minutes = KeepWarmSettings.PingIntervals.ToList();
        if (record?.PingMinutes is { } own && !minutes.Contains(own)) minutes.Add(own);
        foreach (var m in minutes.Order())
            KeepWarmInterval.Items.Add(new ComboBoxItem { Content = KeepWarmRules.Span(TimeSpan.FromMinutes(m)), Tag = m });
        KeepWarmInterval.SelectedItem = KeepWarmInterval.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as int? == record?.PingMinutes)
                                        ?? KeepWarmInterval.Items[0];
        KeepWarmPingPath.Text = record?.PingPath ?? "";
        KeepWarmWarmUpPath.Text = record?.WarmUpPath ?? "";
        ShowKeepWarmError(null);
        KeepWarmPingLabel.Text = $"Keep-alive page (empty: {defaults.PingPath})";
        KeepWarmWarmUpLabel.Text = $"Warm-up page (empty: {defaults.WarmUpPath})";
        _showingKeepWarm = false;
    }

    private static List<ProjectFact> KeepWarmFactsOf(string site, KeepWarmPlan plan)
    {
        var setting = TimeSpan.FromMinutes(plan.PingMinutes);
        // Not skipped while in use when the idle time-out is too short for two intervals (about a minute).
        var inUse = KeepWarmRules.MaySkipWhenInUse(site, plan.Interval, plan.IdleTimeout) ? ", none while the site is in use" : "";
        var whose = plan.OwnInterval ? "this site's" : "the settings'";
        var list = new List<ProjectFact>
        {
            new("IIS idle time-out", plan.IdleTimeout switch
            {
                null => "unknown - the app pool's settings couldn't be read",
                { } idle when idle <= TimeSpan.Zero => "none - IIS never shuts the site's worker process down for being idle",
                { } idle => $"{KeepWarmRules.Span(idle)} - IIS shuts the site's worker process down after that long without a request"
            }),
            new("Requests", plan.Interval < setting
                ? $"every {KeepWarmRules.Span(plan.Interval)} at most{inUse} - {KeepWarmRules.Span(setting)} ({whose}) would come too close to the idle time-out"
                : $"every {KeepWarmRules.Span(plan.Interval)} at most ({whose}){inUse}")
        };
        if (plan.Target is { } target)
        {
            list.Add(new("Keep-alive page", target.DisplayUrl(plan.PingPath) + (plan.OwnPingPath ? "" : "  (the settings')")));
            list.Add(new("Warm-up page", target.DisplayUrl(plan.WarmUpPath) + (plan.OwnWarmUpPath ? "" : "  (the settings')") +
                                         " - when the site has no worker process"));
            list.Add(new("Sent to", $"{target.ConnectHost}:{target.Port} as {target.HostHeader} - this PC, whatever DNS says"));
        }
        else
        {
            list.Add(new("Requests go to", "nowhere - the site has no http or https binding to request", FactKind.Warning));
        }
        return list;
    }

    private async void KeepWarmSwitch_Click(object sender, RoutedEventArgs e)
    {
        // The switch shows what the site does, not the click: it follows once that has changed (a question may come first).
        KeepWarmSwitch.SetCurrentValue(ToggleButton.IsCheckedProperty, _row.KeepWarmOn);
        await _services.GetRequiredService<ServerStore>().ToggleKeepWarmAsync(_row);
    }

    private void KeepWarmCheck_Click(object sender, RoutedEventArgs e) => _services.GetRequiredService<ServerStore>().CheckKeepWarm(_row);

    private void KeepWarmInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_showingKeepWarm && IsLoaded) SaveKeepWarmValues();
    }

    private void KeepWarmPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveKeepWarmValues();
        e.Handled = true;
    }

    // Leaving the field for another one or a button - not switching to another window, nor its own context menu (that is
    // keyboard focus only): a half-typed page isn't saved.
    private void KeepWarmPath_LostFocus(object sender, RoutedEventArgs e) => SaveKeepWarmValues();

    /// <summary>
    /// Saves the site's own interval and pages when they differ from what is saved - each page only when it is allowed:
    /// one that isn't keeps what was saved (and says why), and doesn't stop the others from being saved.
    /// </summary>
    private void SaveKeepWarmValues()
    {
        if (_showingKeepWarm) return;
        var record = KeepWarm.RecordOf(_row.Name);
        var minutes = (KeepWarmInterval.SelectedItem as ComboBoxItem)?.Tag as int?;
        var ping = KeepWarmPingPath.Text.Trim();
        var warmUp = KeepWarmWarmUpPath.Text.Trim();
        var pingProblem = ping.Length > 0 ? KeepWarmSettings.PathProblem(ping) : null;
        var warmUpProblem = warmUp.Length > 0 ? KeepWarmSettings.PathProblem(warmUp) : null;
        ShowKeepWarmError(pingProblem is not null ? $"Keep-alive page {pingProblem}"
            : warmUpProblem is not null ? $"Warm-up page {warmUpProblem}"
            : null);

        static string? Own(string path) => path.Length == 0 ? null : KeepWarmSettings.NormalizePath(path);
        var pingPath = pingProblem is null ? Own(ping) : record?.PingPath;
        var warmUpPath = warmUpProblem is null ? Own(warmUp) : record?.WarmUpPath;
        if (record?.PingMinutes == minutes && record?.PingPath == pingPath && record?.WarmUpPath == warmUpPath) return;
        KeepWarm.SetOwnValues(_row.Name, minutes, warmUpPath, pingPath);

        // Shown as saved - with the / a page was given - before the service has taken them over. A page that isn't
        // allowed stays as typed, under its message.
        _showingKeepWarm = true;
        if (pingProblem is null) KeepWarmPingPath.Text = pingPath ?? "";
        if (warmUpProblem is null) KeepWarmWarmUpPath.Text = warmUpPath ?? "";
        _showingKeepWarm = false;
        var saved = (record ?? new KeepWarmRecord(_row.Name, false)) with { PingMinutes = minutes, PingPath = pingPath, WarmUpPath = warmUpPath };
        KeepWarmFacts.ItemsSource = KeepWarmFactsOf(_row.Name, KeepWarmPlan.For(KeepWarmDefaults, saved, _row.IisSite));
    }

    private void ShowKeepWarmError(string? error)
    {
        KeepWarmError.Text = error ?? "";
        KeepWarmError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ─── Reading what IIS's state doesn't tell ────────────────────────────

    private async Task LoadAsync()
    {
        try { await ReadAsync(); }
        catch (Exception ex)
        {
            FactsState.Text = $"Couldn't be read: {ex.Message}";
            PortalsState.Text = "";
            DnnState.Text = "";
            DatabaseState.Text = "";
        }
    }

    private async Task ReadAsync()
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var s = _row.Project;
        var project = sp.GetRequiredService<IProjectRepository>().Build(s.Name, s.Directory);
        var container = sp.GetRequiredService<LocalSqlContainer>();
        var connection = container.ConnectionOf(project);
        _database = container.DatabaseOf(project);
        var tester = sp.GetRequiredService<ISqlConnectionTester>();
        // On the container its list says whether the database is there; one elsewhere is asked directly (briefly). A
        // LocalDB file runs in the site's own instance, which DNN Manager can't open while the site runs.
        var databaseThere = s.DatabaseName is not null && (s.DatabaseElsewhere
            ? _database is { Kind: not DatabaseKind.LocalDbFile }
            : s.SqlReachable == true && s.DatabaseExists);

        // IIS's configuration and the database - off the UI thread.
        var site = await Task.Run(() => sp.GetRequiredService<IIisManager>().GetSiteInfo(s.Name));
        PoolSettings.Text = site is null ? "-"
            : string.Join(" · ", new[] { site.ClrVersion, site.PipelineMode is { } mode ? $"{mode} pipeline" : null, site.Identity }
                .Where(v => !string.IsNullOrEmpty(v)));

        IReadOnlyList<DnnPortal> portals = [];
        if (_row.IsDnn) portals = await LoadPortalsAsync(tester, connection, databaseThere);

        var facts = databaseThere ? await tester.DescribeDatabaseAsync(connection, CancellationToken.None, timeoutSeconds: 5) : null;
        GeneralFacts.ItemsSource = GeneralFactsOf(project);
        FactsState.Visibility = Visibility.Collapsed;
        DatabaseFacts.ItemsSource = DatabaseFactsOf(connection, facts);
        DatabaseState.Visibility = Visibility.Collapsed;
        TestButton.IsEnabled = _database is not null;
        AdvancedFacts.ItemsSource = AdvancedFactsOf(sp);

        var hosts = _row.IsDnn && databaseThere && _database is not null
            ? await sp.GetRequiredService<IDatabaseProvisioner>().ListHostAccountsAsync(_database, CancellationToken.None)
            : null;
        DnnFacts.ItemsSource = DnnFactsOf(sp, portals, hosts);
        DnnState.Visibility = Visibility.Collapsed;
    }

    // ─── DNN: how it was installed, the host accounts ─────────────────────

    private List<ProjectFact> DnnFactsOf(IServiceProvider sp, IReadOnlyList<DnnPortal> portals, Domain.Result<IReadOnlyList<DnnHostAccount>>? hosts)
    {
        var s = _row.Project;
        var list = new List<ProjectFact>();
        if (!_row.IsDnn)
        {
            list.Add(new("DNN", @"none - the site's folder has no bin\DotNetNuke.dll"));
            PasswordHint.Text = "";
            return list;
        }

        var record = sp.GetRequiredService<IProjectRecords>().Find(s.Name);
        // Installed when web.config has DNN's InstallVersion - or when its database already has portals or a host.
        var installed = InstallVersion(Path.Combine(s.Directory, "web.config")) is not null ||
                        portals.Count > 0 || hosts is { Success: true, Value.Count: > 0 };
        list.Add(new("Installation", record is null
            ? installed ? "installed before DNN Manager kept track" : "not installed yet - open the site to run DNN's installation wizard"
            : record.InstallMode == DnnInstallMode.Automatic
                ? $"automatic setup by DNN Manager, {record.CreatedUtc.ToLocalTime():g}"
                : installed ? "manual, DNN's installation wizard" : "manual - DNN's installation wizard runs on the first visit",
            installed ? FactKind.Normal : FactKind.Warning));
        list.Add(new("DNN version", s.DnnVersion ?? "-"));

        _hosts = hosts is { Success: true, Value: { } accounts } ? accounts.Select(a => a.UserName).ToList()
            : record?.HostUserName is { } user ? [user] : [];
        if (hosts is { Success: true, Value: { } found })
        {
            if (found.Count == 0) list.Add(new("Host account", "none in the database", FactKind.Warning));
            foreach (var host in found)
            {
                list.Add(new(found.Count == 1 ? "Host username" : $"Host {host.UserId}", host.UserName));
                list.Add(new(found.Count == 1 ? "Host e-mail" : "  e-mail", host.Email.Length > 0 ? host.Email : "-"));
            }
        }
        else
        {
            list.Add(new("Host account", hosts is { Success: false }
                ? $"couldn't be read: {hosts.Error}"
                : _database?.Kind == DatabaseKind.LocalDbFile
                    ? (record?.HostUserName is { } name ? $"{name} - " : "") + "the LocalDB file is the site's own while it runs"
                    : "the database can't be read", FactKind.Warning));
        }

        if (portals.FirstOrDefault() is { } first)
        {
            list.Add(new("Portal name", first.Name));
            list.Add(new("Portal alias", first.Primary?.HttpAlias ?? "-"));
        }
        else if (_database?.Kind == DatabaseKind.LocalDbFile)
        {
            list.Add(new("Portal alias", _row.Url.Length > 0 ? _row.Url : "-"));
        }

        var canChange = installed && _database is not null;
        ChangePasswordButton.IsEnabled = canChange;
        PasswordHint.Text = canChange
            ? "Sets a new password the way DNN stores it and restarts the site. The current password is never shown - DNN keeps only a hash of it."
            : "Once DNN is installed and its web.config names its database, the host password can be changed here.";
        return list;
    }

    /// <summary>The InstallVersion DNN writes into web.config when it is installed; null before that.</summary>
    private static string? InstallVersion(string webConfig)
    {
        try
        {
            if (!File.Exists(webConfig)) return null;
            return System.Xml.Linq.XDocument.Load(webConfig).Root?.Element("appSettings")?.Elements("add")
                .FirstOrDefault(a => (string?)a.Attribute("key") == "InstallVersion")?.Attribute("value")?.Value;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (HostPasswordDialog.Show(_row.Name, _hosts) is not { } change) return;
        var runner = _services.GetRequiredService<OperationRunner>();
        var s = _row.Project;
        if (await runner.RunAsync($"Change host password of '{_row.Name}'", (sp, reporter, ct) =>
                sp.GetRequiredService<ChangeHostPasswordUseCase>().ExecuteAsync(s.Name, s.Directory, change.User, change.Password, reporter, ct)))
            Toast.Show($"'{change.User}' signs in to {_row.Name} with the new password.", ToastKind.Success);
    }

    // ─── Database ─────────────────────────────────────────────────────────

    private List<ProjectFact> DatabaseFactsOf(SiteSqlConnection connection, Domain.Result<DatabaseFacts>? facts)
    {
        var s = _row.Project;
        var list = new List<ProjectFact>();
        if (_database is not { } database)
        {
            list.Add(new("Database", s.DatabaseName is null
                ? "none - its web.config names no SiteSqlServer database"
                : $"[{s.DatabaseName}] on the local SQL container - web.config still has DNN's own connection until DNN is installed"));
            if (s.DatabaseName is not null && !s.DatabaseElsewhere)
                list.Add(new("SQL", _row.Sql, _row.Sql switch { "Live" => FactKind.Good, "Offline" => FactKind.Bad, _ => FactKind.Warning }));
            return list;
        }

        list.Add(new("Connection type", database.KindText));
        list.Add(new("Server", database.Server));
        list.Add(new(database.Kind == DatabaseKind.LocalDbFile ? "Database file" : "Database",
            database.Kind == DatabaseKind.LocalDbFile ? $@"App_Data\{database.Database}" : database.Database));
        list.Add(new("Authentication", database.UsesWindowsAuthentication
            ? $"Windows - the site signs in as {_services.GetRequiredService<IIisManager>().AppPoolIdentity(s.Name)}"
            : $"SQL Server - user '{database.User}'"));
        if (!s.DatabaseElsewhere)
            list.Add(new("SQL", _row.Sql, _row.Sql switch { "Live" => FactKind.Good, "Offline" => FactKind.Bad, _ => FactKind.Warning }));
        if (facts is { Success: true, Value: { } f })
        {
            list.Add(new("Size", $"{f.SizeMb:N1} MB"));
            if (f.Portals is { } count) list.Add(new("Portals", count.ToString()));
            if (f.DnnVersion is { } dbVersion)
            {
                var matches = s.DnnVersion is null || s.DnnVersion == dbVersion;
                list.Add(new("DNN version (database)", matches ? dbVersion : $"{dbVersion}  (the files are {s.DnnVersion})",
                    matches ? FactKind.Normal : FactKind.Warning));
            }
        }
        else if (facts is { Success: false })
        {
            list.Add(new("Details", $"couldn't be read: {facts.Error}", FactKind.Warning));
        }
        else if (database.Kind == DatabaseKind.LocalDbFile)
        {
            list.Add(new("Details", "the site runs the file in its own LocalDB instance - DNN Manager doesn't open it while the site runs", FactKind.Warning));
        }
        return list;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_database is not { } database) return;
        TestButton.IsEnabled = false;
        CheckList.ShowTesting();
        try
        {
            var siteLogin = database is { Kind: DatabaseKind.SqlServer, UsesWindowsAuthentication: true }
                ? _services.GetRequiredService<IIisManager>().AppPoolIdentity(_row.Name)
                : null;
            CheckList.Show(await _services.GetRequiredService<IDatabaseProvisioner>()
                .CheckAsync(database, new DatabaseCheckOptions(ForNewInstall: false, SiteLogin: siteLogin, MinimumMajorVersion: 0), CancellationToken.None));
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The portals of the site's DNN installation, from its database: each one's primary alias as a link - https
    /// when the site has an https binding for that host - and its other aliases on request.
    /// </summary>
    private async Task<IReadOnlyList<DnnPortal>> LoadPortalsAsync(ISqlConnectionTester tester, SiteSqlConnection database, bool databaseThere)
    {
        if (!databaseThere)
        {
            PortalsState.Text = _database?.Kind == DatabaseKind.LocalDbFile
                ? "The portals are in the site's LocalDB file, which the site runs in its own instance - open the site to see them."
                : _row.Project.SqlReachable == false
                    ? "The SQL Server doesn't answer - the portals are in the site's database."
                    : $"The database [{_row.Database}] isn't there - the portals are in the site's database.";
            return [];
        }

        var portals = await tester.ListPortalsAsync(database, CancellationToken.None, timeoutSeconds: 5);
        if (!portals.Success || portals.Value is not { } list)
        {
            PortalsState.Text = $"The portals couldn't be read: {portals.Error}";
            return [];
        }

        var site = _row.IisSite;
        AliasLink Link(DnnPortalAlias alias) => new($"{(site.ServesHttps(alias.Host) ? "https" : "http")}://{alias.HttpAlias}");
        Portals.ItemsSource = list.Select(p => new PortalItem
        {
            Id = p.Id,
            Name = p.Name,
            Expired = p.Expired,
            Primary = p.Primary is { } primary ? Link(primary) : null,
            Others = p.Aliases.Where(a => a != p.Primary).Select(Link).ToList()
        }).ToList();
        PortalsTitle.Text = list.Count == 1 ? "DNN portal" : $"DNN portals ({list.Count})";
        PortalsState.Text = list.Count == 0 ? "The database has no portals." : "";
        PortalsState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return list;
    }

    /// <summary>The folder's facts.</summary>
    private List<ProjectFact> GeneralFactsOf(Domain.DnnProject project)
    {
        var s = _row.Project;
        var dir = s.Directory;
        var list = new List<ProjectFact>();

        list.Add(new("Folder", dir + (s.InProjectsFolder ? "" : "  (outside the projects folder)")));
        if (Directory.Exists(dir)) list.Add(new("Created", Directory.GetCreationTime(dir).ToString("g")));
        list.Add(new("Size", _row.Size));
        list.Add(new("DNN version", s.DnnVersion ?? @"none (no bin\DotNetNuke.dll)"));
        if (GitBranch(dir) is { } branch) list.Add(new("Git branch", branch));
        if (Solutions(dir) is { Count: > 0 } solutions) list.Add(new("Solution", string.Join(", ", solutions)));
        var backups = ProjectBackups.List(project);
        list.Add(new("Backups", backups.Count == 0 ? $"none in {project.BackupDirectory}"
            : $"{backups.Count} in {project.BackupDirectory} - newest {backups[0].Created:yyyy-MM-dd HH:mm}"));
        return list;
    }

    /// <summary>The web.config's facts - never its connection string's password.</summary>
    private List<ProjectFact> AdvancedFactsOf(IServiceProvider sp)
    {
        var webConfigService = sp.GetRequiredService<IWebConfigService>();
        var webConfig = System.IO.Path.Combine(_row.Project.Directory, "web.config");
        var list = new List<ProjectFact>();
        if (!File.Exists(webConfig))
        {
            list.Add(new("web.config", "not found", FactKind.Warning));
            return list;
        }
        list.Add(new("File", webConfig));
        list.Add(new("Connection", _database is { } d ? d.Describe() : "DNN's own, until DNN is installed"));
        if (InstallVersion(webConfig) is { } installVersion) list.Add(new("InstallVersion", installVersion));
        if (webConfigService.ReadFacts(webConfig) is { Success: true, Value: { } w })
        {
            if (w.TargetFramework is { } tf) list.Add(new("Target framework", tf));
            if (w.Debug is { } debug) list.Add(new("Debug", debug ? "on" : "off"));
            if (w.CustomErrors is { } ce) list.Add(new("Custom errors", ce));
            list.Add(w.DisabledHttpsRules.Count == 0
                ? new("HTTPS redirects", "none switched off by DNN Manager")
                : new("HTTPS redirects", $"switched off for local use: {string.Join(", ", w.DisabledHttpsRules)} - " +
                                         "switch them back on before deploying", FactKind.Warning));
        }
        return list;
    }

    private static IReadOnlyList<string> Solutions(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx"))
                .Select(f => System.IO.Path.GetFileName(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    /// <summary>The checked-out branch from <c>.git\HEAD</c>; null when the folder isn't a git repository.</summary>
    private static string? GitBranch(string dir)
    {
        var head = System.IO.Path.Combine(dir, ".git", "HEAD");
        if (!File.Exists(head)) return Directory.Exists(System.IO.Path.Combine(dir, ".git")) || File.Exists(System.IO.Path.Combine(dir, ".git")) ? "(git repository)" : null;
        try
        {
            var text = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..]
                : text.Length >= 7 ? $"(detached at {text[..7]})" : "(unknown)";
        }
        catch
        {
            return "(unreadable)";
        }
    }
}