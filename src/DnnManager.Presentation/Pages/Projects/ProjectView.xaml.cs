using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Diagnostics;
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

/// <summary>
/// A site's Details - opened from the Projects table - in tabs: what was detected about it, where it really is (IIS, its
/// folder, web.config, bin, its database through web.config's connection, this PC), with what is wrong or unusual in it
/// (<see cref="ProjectDiagnostics"/>). Read again when the site changes outside DNN Manager. Information first; each tab's
/// Edit buttons change what it shows (<see cref="ProjectEdits"/>), and keep warm and the host password live here. No
/// password or key is shown anywhere.
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
            ShowKeepWarm();
        };
        Unloaded += (_, _) => _row.PropertyChanged -= Row_PropertyChanged;
        _ = LoadAsync();
    }

    /// <summary>What is read again when the site changes: only what the change can have changed.</summary>
    [Flags]
    private enum Parts
    {
        /// <summary>IIS's view of the site - its state, app pool, bindings, worker processes. One read of IIS.</summary>
        Iis = 1,
        /// <summary>The database, with web.config's connection.</summary>
        Database = 2,
        /// <summary>Everything: web.config, the folders, bin (some hundred files), IIS and the database.</summary>
        All = 7
    }

    // Asked for and not read yet - once for a row's burst of changes.
    private Parts _pending;

    /// <summary>The site changed while it is shown: what the change touches is read again.</summary>
    private void Reload(Parts parts)
    {
        var asked = _pending != 0;
        _pending |= parts;
        if (asked) return;
        Dispatcher.InvokeAsync(() =>
        {
            var what = _pending;
            _pending = 0;
            _ = LoadAsync(what);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Back was pressed (or Esc) - the page shows the table again.</summary>
    public event EventHandler? BackRequested;

    public ProjectRow Row => _row;

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    // The site's database as its web.config has it (null: none of its own yet), and its host accounts as read.
    private DatabaseConnection? _database;
    private IReadOnlyList<string> _hosts = [];

    private IEnumerable<RadioButton> Tabs => [GeneralTab, IisTab, DnnTab, DatabaseTab, AdvancedTab];

    /// <summary>The next tab (<paramref name="by"/> 1) or the one before (-1), round the end - Ctrl+PageUp / Ctrl+PageDown.</summary>
    public void StepTab(int by)
    {
        var tabs = Tabs.ToList();
        var index = tabs.FindIndex(t => t.IsChecked == true);
        var next = tabs[((index + by) % tabs.Count + tabs.Count) % tabs.Count];
        next.IsChecked = true;
        if (IsKeyboardFocusWithin) next.Focus();
    }

    /// <summary>The keyboard on the shown tab - the arrows move between them, Tab goes on to the content.</summary>
    public void FocusTabs() => Tabs.First(t => t.IsChecked == true).Focus();

    /// <summary>The tab shown, by its name: General, IIS, DNN, Database or Advanced.</summary>
    public string Tab => (string)Tabs.First(t => t.IsChecked == true).Content;

    /// <summary>Shows the tab named <paramref name="name"/>; one that isn't there leaves the tab as it is.</summary>
    public void ShowTab(string? name)
    {
        if (Tabs.FirstOrDefault(t => string.Equals((string)t.Content, name, StringComparison.OrdinalIgnoreCase)) is { } tab) tab.IsChecked = true;
    }

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

    // ─── Changing the project (ProjectEdits) ──────────────────────────────

    private void Rename_Click(object sender, RoutedEventArgs e) => ProjectEdits.Rename(_services, _row);
    private void EditBindings_Click(object sender, RoutedEventArgs e) => ProjectEdits.EditBindings(_services, _row);
    private void EditAppPool_Click(object sender, RoutedEventArgs e) => ProjectEdits.EditAppPool(_services, _row);
    private void ChangeDatabase_Click(object sender, RoutedEventArgs e) => ProjectEdits.ChangeDatabase(_services, _row);
    private void OpenSite_Click(object sender, RoutedEventArgs e) => Shell.Open(_row.Url);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_row.Path)) Shell.Open(_row.Path);
        else Dialogs.Error($"The site's folder doesn't exist: {_row.Path}");
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && url.Length > 0) Shell.Open(url);
    }

    // ─── Keep warm ────────────────────────────────────────────────────────


    private KeepWarmService KeepWarm => _services.GetRequiredService<KeepWarmService>();
    private KeepWarmSettings KeepWarmDefaults => _services.GetRequiredService<IOptions<AppOptions>>().Value.KeepWarm;

    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Its status, or the site's bindings and app pool (the idle time-out) - what the facts are made of.
        if (e.PropertyName is nameof(ProjectRow.KeepWarm) or nameof(ProjectRow.BindingsText) or nameof(ProjectRow.AppPoolText))
            ShowKeepWarm();
        // What the overview reads from the site itself: its folder, web.config or DNN changed - everything; IIS's view of
        // it (started, stopped, another pool or binding) - IIS's part; the database came or went - the database's.
        if (e.PropertyName is nameof(ProjectRow.Path) or nameof(ProjectRow.Dnn) or nameof(ProjectRow.Database))
            Reload(Parts.All);
        else if (e.PropertyName is nameof(ProjectRow.State) or nameof(ProjectRow.BindingsText) or nameof(ProjectRow.AppPoolText))
            Reload(Parts.Iis);
        else if (e.PropertyName is nameof(ProjectRow.Sql))
            Reload(Parts.Database);
        // The folder's size came (walked for this overview): shown with what was read - nothing is read again.
        else if (e.PropertyName is nameof(ProjectRow.Size) && _snapshot is { } shown)
            Show(shown);
    }

    /// <summary>How the site is kept warm - its IIS idle time-out, how often it is requested, and where.</summary>
    private void ShowKeepWarm() =>
        KeepWarmFacts.ItemsSource = KeepWarmFactsOf(_row.Name, KeepWarmPlan.For(KeepWarmDefaults, KeepWarm.RecordOf(_row.Name), _row.IisSite));

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

    private void KeepWarmSwitch_Click(object sender, RoutedEventArgs e)
    {
        // The switch shows what the site does, not the click: it follows once that has changed.
        KeepWarmSwitch.SetCurrentValue(ToggleButton.IsCheckedProperty, _row.KeepWarmOn);
        _services.GetRequiredService<ServerStore>().ToggleKeepWarm(_row);
    }

    private void KeepWarmCheck_Click(object sender, RoutedEventArgs e) => _services.GetRequiredService<ServerStore>().CheckKeepWarm(_row);

    // ─── Reading what the site really is ─────────────────────────────────

    // A newer read replaces an older one still on its way.
    private int _reading;
    // What was read last - a partial read starts from it. Null until the first full read is shown.
    private ProjectSnapshot? _snapshot;
    // The connection web.config has, for reading the database again.
    private SiteSqlConnection? _connection;
    private bool _fullReadRunning;

    private async Task LoadAsync(Parts parts = Parts.All)
    {
        // A part needs the rest read first: until then (or while a full read runs, which a newer one replaces) everything.
        if (_snapshot is null || _fullReadRunning) parts = Parts.All;
        var generation = ++_reading;
        try
        {
            if (parts == Parts.All) await ReadAsync(generation);
            else await ReadPartsAsync(generation, parts);
        }
        catch (Exception ex)
        {
            GeneralPanel.Show([new InspectorSection("Project") { Note = $"Couldn't be read: {ex.Message}" }]);
        }
    }

    /// <summary>
    /// Reads the site - this PC's part first (IIS, web.config, the folders, bin) and shows it; then its database, with
    /// web.config's connection, and shows everything again.
    /// </summary>
    private async Task ReadAsync(int generation)
    {
        if (GeneralPanel.Children.Count == 0) ShowReading();
        _fullReadRunning = true;
        try { await ReadAllAsync(generation); }
        finally { if (generation == _reading) _fullReadRunning = false; }
    }

    private async Task ReadAllAsync(int generation)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var s = _row.Project;
        var project = sp.GetRequiredService<IProjectRepository>().Build(s.Name, s.Directory);
        var container = sp.GetRequiredService<LocalSqlContainer>();
        var connection = container.SiteConnectionOf(project);
        _connection = connection;
        _database = container.DatabaseOf(project);
        var iis = sp.GetRequiredService<IIisManager>();
        var record = sp.GetRequiredService<IProjectRecords>().Find(s.Name);
        var webConfig = sp.GetRequiredService<IWebConfigService>();
        // Its folder's size, walked for this overview when the table doesn't show sizes (it comes as the row changes).
        sp.GetService<ServerStore>()?.MeasureSize(s.Name);

        var local = await Task.Run(() =>
        {
            var details = iis.GetSiteDetails(s.Name);
            var config = WebConfigInspector.Inspect(s.Directory);
            IReadOnlyList<FolderCheck> folders = Directory.Exists(s.Directory) ? FolderInspector.Inspect(s.Directory, details?.Pool?.Account) : [];
            var assemblies = AssemblyInspector.Inspect(s.Directory, config.BindingRedirects, config.ProbingPath);
            string? product = null;
            try
            {
                var dll = System.IO.Path.Combine(s.Directory, "bin", "DotNetNuke.dll");
                if (File.Exists(dll)) product = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductName;
            }
            catch (Exception) { /* no product name to show */ }
            var disabledHttps = webConfig.ReadFacts(System.IO.Path.Combine(s.Directory, "web.config")) is { Success: true, Value: { } facts }
                ? facts.DisabledHttpsRules : (IReadOnlyList<string>)[];
            var folderFacts = ProjectDiagnostics.ReadFolderFacts(project);
            return (details, config, folders, assemblies, product, disabledHttps, folderFacts);
        });
        if (generation != _reading) return;
        _disabledHttps = local.disabledHttps;

        var readDatabase = connection is not null && !s.DatabaseIsFile;
        var snapshot = new ProjectSnapshot
        {
            Row = _row, Project = project, Iis = local.details, Config = local.config, Folders = local.folders, Assemblies = local.assemblies,
            // A product name, not the address some builds put there.
            DnnProduct = string.IsNullOrWhiteSpace(local.product) || local.product.Contains("://") ? null : local.product,
            Connection = _database, Record = record, Folder = local.folderFacts,
            DatabaseRead = !readDatabase
        };
        Show(snapshot);
        if (readDatabase) await ReadDatabaseAsync(generation, snapshot);
    }

    /// <summary>Reads only <paramref name="parts"/> again, on top of what was read last.</summary>
    private async Task ReadPartsAsync(int generation, Parts parts)
    {
        var snapshot = _snapshot!;
        if (parts.HasFlag(Parts.Iis))
        {
            var iis = _services.GetRequiredService<IIisManager>();
            var name = _row.Name;
            var details = await Task.Run(() => iis.GetSiteDetails(name));
            if (generation != _reading) return;
            snapshot = snapshot with { Iis = details };
            Show(snapshot);
        }
        if (parts.HasFlag(Parts.Database) && _connection is not null && !_row.Project.DatabaseIsFile)
            await ReadDatabaseAsync(generation, snapshot);
    }

    /// <summary>The database, with web.config's connection - and its host accounts - shown with <paramref name="snapshot"/>.</summary>
    private async Task ReadDatabaseAsync(int generation, ProjectSnapshot snapshot)
    {
        var inspection = await DatabaseInspector.InspectAsync(_connection!, CancellationToken.None);
        var hosts = inspection.Problem is null && _database is not null && _row.IsDnn
            ? await _services.GetRequiredService<IDatabaseProvisioner>().ListHostAccountsAsync(_database, CancellationToken.None)
            : null;
        if (generation != _reading) return;
        Show(snapshot with { Database = inspection, DatabaseRead = true, Hosts = hosts });
    }

    // What DNN Manager switched off in web.config for local use - shown on Advanced.
    private IReadOnlyList<string> _disabledHttps = [];

    private void ShowReading()
    {
        foreach (var panel in new[] { GeneralPanel, IisPanel, DnnPanel, DatabasePanel, AdvancedPanel })
            panel.Show([new InspectorSection("Reading…") { Note = "IIS, the site's folder, web.config, bin and its database are being read." }]);
    }

    private void Show(ProjectSnapshot snapshot)
    {
        _snapshot = snapshot;
        var issues = ProjectDiagnostics.Issues(snapshot);
        GeneralPanel.Show(ProjectDiagnostics.General(snapshot, issues));
        IisPanel.Show(ProjectDiagnostics.Iis(snapshot));
        DnnPanel.Show(ProjectDiagnostics.Dnn(snapshot));
        DatabasePanel.Show(ProjectDiagnostics.Database(snapshot, issues));
        AdvancedPanel.Show(ProjectDiagnostics.Advanced(snapshot, issues, _disabledHttps));
        ShowHosts(snapshot);
    }

    // ─── DNN: the host accounts ───────────────────────────────────────────

    /// <summary>The host accounts the database has - and whether their password can be changed.</summary>
    private void ShowHosts(ProjectSnapshot s)
    {
        var list = new List<ProjectFact>();
        var hosts = s.Hosts;
        // Offered when changing the password. Only when the database can't be read, the one DNN Manager set up is offered
        // (not shown as a fact: it may have been renamed or removed since).
        _hosts = hosts is { Success: true, Value: { } accounts } ? accounts.Select(a => a.UserName).ToList()
            : s.Record?.HostUserName is { } user ? [user] : [];
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
            list.Add(new("Host account",
                !s.DatabaseRead ? "reading the database…"
                : hosts is { Success: false } ? $"couldn't be read: {hosts.Error}"
                : _database?.Kind == DatabaseKind.LocalDbFile ? "can't be read - the LocalDB file is the site's own while it runs"
                : s.Database?.Problem is { } problem ? $"can't be read - {problem}"
                : s.Row.Project.DatabaseProblem is { } why ? $"can't be read - {why}"
                : "can't be read", s.DatabaseRead ? FactKind.Warning : FactKind.Normal));
        }
        HostFacts.ItemsSource = list;

        // Installed when web.config has DNN's InstallVersion - or when its database already has portals or a host.
        var installed = s.Config.InstallVersion is not null || s.Database?.Dnn?.Portals.Count > 0 || hosts is { Success: true, Value.Count: > 0 };
        var canChange = installed && _database is not null;
        ChangePasswordButton.IsEnabled = canChange;
        PasswordHint.Text = canChange
            ? "Sets a new password the way DNN stores it and restarts the site. The current password is never shown - DNN keeps only a hash of it."
            : "Once DNN is installed and its web.config names its database, the host password can be changed here.";
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
}
