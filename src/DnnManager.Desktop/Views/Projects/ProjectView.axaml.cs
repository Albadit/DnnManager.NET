using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Diagnostics;
using DnnManager.Infrastructure.KeepWarm;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop.Views.Projects;

/// <summary>How a fact on the overview looks: a section heading, or a value in the normal, good (green), bad (red) or warning colour.</summary>
public enum FactKind { Normal, Section, Good, Bad, Warning }

/// <summary>A label and its value on a site's overview.</summary>
public sealed record ProjectFact(string Label, string Value, FactKind Kind = FactKind.Normal)
{
    public static ProjectFact Section(string title) => new(title, "", FactKind.Section);
}

/// <summary>
/// A site's Details - opened from the Projects table - in tabs (the Windows app's ProjectView): what was detected about
/// it, where it really is (IIS, its folder, web.config, bin, its database through web.config's connection, this PC), with
/// what is wrong or unusual in it (<see cref="ProjectDiagnostics"/>). Read again when the site changes outside DNN Manager.
/// Each tab's Edit buttons change what it shows (<see cref="ProjectEdits"/>); keep warm and the host password live here.
/// No password or key is shown anywhere.
/// </summary>
public partial class ProjectView : UserControl
{
    /// <summary>The icon font is there (Windows) - see MainWindow.HasIconFont.</summary>
    public static bool HasIconFont { get; } = OperatingSystem.IsWindows();
    public static bool NoIconFont { get; } = !OperatingSystem.IsWindows();

    private readonly ProjectRow _row = null!;
    private readonly IServiceProvider _services = null!;
    private readonly Action<SiteAction> _control = null!;

    /// <summary>For the previewer only.</summary>
    public ProjectView() => InitializeComponent();

    /// <param name="control">Starts, stops or restarts the site - the table's row actions.</param>
    public ProjectView(ProjectRow row, IServiceProvider services, Action<SiteAction> control)
    {
        _row = row; _services = services; _control = control;
        InitializeComponent();
        DataContext = row;
        KeepWarmSwitch.IsChecked = row.KeepWarmOn;
        AttachedToVisualTree += (_, _) =>
        {
            _row.PropertyChanged -= Row_PropertyChanged;
            _row.PropertyChanged += Row_PropertyChanged;
            ShowKeepWarm();
            Focus();
        };
        DetachedFromVisualTree += (_, _) => _row.PropertyChanged -= Row_PropertyChanged;
        _ = LoadAsync();
    }

    /// <summary>What is read again when the site changes: only what the change can have changed.</summary>
    [Flags]
    private enum Parts
    {
        Iis = 1,
        Database = 2,
        All = 7
    }

    private Parts _pending;

    private void Reload(Parts parts)
    {
        var asked = _pending != 0;
        _pending |= parts;
        if (asked) return;
        Dispatcher.UIThread.Post(() =>
        {
            var what = _pending;
            _pending = 0;
            _ = LoadAsync(what);
        }, DispatcherPriority.Background);
    }

    /// <summary>Back was pressed (or Esc) - the page shows the table again.</summary>
    public event EventHandler? BackRequested;

    public ProjectRow Row => _row;

    private void Back_Click(object? sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

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
        next.Focus();
    }

    /// <summary>The keyboard on the shown tab.</summary>
    public void FocusTabs() => Tabs.First(t => t.IsChecked == true).Focus();

    /// <summary>The tab shown, by its name: General, IIS, DNN, Database or Advanced.</summary>
    public string Tab => (string)Tabs.First(t => t.IsChecked == true).Content!;

    /// <summary>Shows the tab named <paramref name="name"/>; one that isn't there leaves the tab as it is.</summary>
    public void ShowTab(string? name)
    {
        if (Tabs.FirstOrDefault(t => string.Equals((string?)t.Content, name, StringComparison.OrdinalIgnoreCase)) is { } tab) tab.IsChecked = true;
    }

    private void Section_Checked(object? sender, RoutedEventArgs e)
    {
        if (GeneralSection is null) return;
        GeneralSection.IsVisible = GeneralTab.IsChecked == true;
        IisSection.IsVisible = IisTab.IsChecked == true;
        DnnSection.IsVisible = DnnTab.IsChecked == true;
        DatabaseSection.IsVisible = DatabaseTab.IsChecked == true;
        AdvancedSection.IsVisible = AdvancedTab.IsChecked == true;
        ShowVisiblePanel();
    }

    /// <summary>Source Control - the window's page for the projects' servers.</summary>
    private void Deploy_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window) window.Show("SourceControl");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Escape)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (command && e.Key is Key.PageDown or Key.PageUp)
        {
            StepTab(e.Key == Key.PageDown ? 1 : -1);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None && _row.CanStart)
        {
            _control(SiteAction.Start);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.Shift && _row.CanStop)
        {
            _control(SiteAction.Stop);
            e.Handled = true;
        }
        else if (command && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.R && _row.CanRestart)
        {
            _control(SiteAction.Restart);
            e.Handled = true;
        }
    }

    private void Start_Click(object? sender, RoutedEventArgs e) => _control(SiteAction.Start);
    private void Stop_Click(object? sender, RoutedEventArgs e) => _control(SiteAction.Stop);
    private void Restart_Click(object? sender, RoutedEventArgs e) => _control(SiteAction.Restart);

    private void More_Click(object? sender, RoutedEventArgs e) => ProjectMenu.ShowSiteTools(MoreButton, _services, _row);

    // ─── Changing the project (ProjectEdits) ──────────────────────────────

    private void Rename_Click(object? sender, RoutedEventArgs e) => ProjectEdits.Rename(_services, _row);
    private void EditBindings_Click(object? sender, RoutedEventArgs e) => ProjectEdits.EditBindings(_services, _row);
    private void EditAppPool_Click(object? sender, RoutedEventArgs e) => ProjectEdits.EditAppPool(_services, _row);
    private void ChangeDatabase_Click(object? sender, RoutedEventArgs e) => ProjectEdits.ChangeDatabase(_services, _row);
    private void UpgradeDnn_Click(object? sender, RoutedEventArgs e) => ProjectEdits.UpgradeDnn(_services, _row);

    private void OpenSite_Click(object? sender, RoutedEventArgs e)
    {
        if (_row.HasUrl) ProjectShell.Open(_row.Url);
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_row.Path)) ProjectShell.Open(_row.Path);
        else ProjectShell.Error($"The site's folder doesn't exist: {_row.Path}");
    }

    // ─── Keep warm ────────────────────────────────────────────────────────

    private KeepWarmSettings KeepWarmDefaults => _services.GetRequiredService<IOptions<AppOptions>>().Value.KeepWarm;

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectRow.KeepWarmOn) or nameof(ProjectRow.KeepWarm)) KeepWarmSwitch.IsChecked = _row.KeepWarmOn;
        if (e.PropertyName is nameof(ProjectRow.KeepWarm) or nameof(ProjectRow.BindingsText) or nameof(ProjectRow.AppPoolText))
            ShowKeepWarm();
        if (e.PropertyName is nameof(ProjectRow.Path) or nameof(ProjectRow.Dnn) or nameof(ProjectRow.Database))
            Reload(Parts.All);
        else if (e.PropertyName is nameof(ProjectRow.State) or nameof(ProjectRow.BindingsText) or nameof(ProjectRow.AppPoolText))
            Reload(Parts.Iis);
        else if (e.PropertyName is nameof(ProjectRow.Sql))
            Reload(Parts.Database);
        else if (e.PropertyName is nameof(ProjectRow.Size) && _snapshot is { } shown)
            Show(shown);
    }

    /// <summary>How the site is kept warm - its IIS idle time-out, how often it is requested, and where.</summary>
    private void ShowKeepWarm() => ShowFacts(KeepWarmFacts, KeepWarmFactsOf(_row.Name, KeepWarmPlan.For(KeepWarmDefaults, _row.IisSite)));

    private static List<ProjectFact> KeepWarmFactsOf(string site, KeepWarmPlan plan)
    {
        var setting = TimeSpan.FromMinutes(plan.PingMinutes);
        var inUse = KeepWarmRules.MaySkipWhenInUse(site, plan.Interval, plan.IdleTimeout) ? ", none while the site is in use" : "";
        var list = new List<ProjectFact>
        {
            new("IIS idle time-out", plan.IdleTimeout switch
            {
                null => "unknown - the app pool's settings couldn't be read",
                { } idle when idle <= TimeSpan.Zero => "none - IIS never shuts the site's worker process down for being idle",
                { } idle => $"{KeepWarmRules.Span(idle)} - IIS shuts the site's worker process down after that long without a request"
            }),
            new("Requests", plan.Interval < setting
                ? $"every {KeepWarmRules.Span(plan.Interval)} at most{inUse} - the settings' {KeepWarmRules.Span(setting)} would come too close to the idle time-out"
                : $"every {KeepWarmRules.Span(plan.Interval)} at most{inUse}")
        };
        if (plan.Target is { } target)
        {
            list.Add(new("Keep-alive page", target.DisplayUrl(plan.PingPath)));
            list.Add(new("Warm-up page", target.DisplayUrl(plan.WarmUpPath) + " - when the site has no worker process"));
            list.Add(new("Sent to", $"{target.ConnectHost}:{target.Port} as {target.HostHeader} - this PC, whatever DNS says"));
        }
        else
        {
            list.Add(new("Requests go to", "nowhere - the site has no http or https binding to request", FactKind.Warning));
        }
        return list;
    }

    /// <summary>The facts as label and value rows (FactTemplate): a section heading, or a value that can be selected.</summary>
    private static void ShowFacts(Panel panel, IEnumerable<ProjectFact> facts)
    {
        panel.Children.Clear();
        foreach (var fact in facts)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("210,16,*"), Margin = new Thickness(0, 3) };
            var label = new TextBlock { Text = fact.Label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
            row.Children.Add(label);
            if (fact.Kind == FactKind.Section)
            {
                row.Margin = new Thickness(0, 12, 0, 4);
                Grid.SetColumnSpan(label, 3);
                label.FontWeight = FontWeight.SemiBold;
                label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("TextPrimary"));
            }
            else
            {
                label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("TextMuted"));
                var value = new SelectableTextBlock { Text = fact.Value, Classes = { "value" } };
                Avalonia.Automation.AutomationProperties.SetName(value, fact.Label);
                if (fact.Kind is FactKind.Good or FactKind.Bad) value.FontWeight = FontWeight.SemiBold;
                if (fact.Kind != FactKind.Normal)
                    value.Bind(TextBlock.ForegroundProperty, value.GetResourceObservable(fact.Kind switch
                    {
                        FactKind.Good => "SuccessText", FactKind.Bad => "ErrorText", _ => "LogWarn"
                    }));
                Grid.SetColumn(value, 2);
                row.Children.Add(value);
            }
            panel.Children.Add(row);
        }
    }

    private void KeepWarmSwitch_Click(object? sender, RoutedEventArgs e)
    {
        // The switch shows what the site does, not the click: it follows once that has changed.
        KeepWarmSwitch.IsChecked = _row.KeepWarmOn;
        ProjectsServices.Store.ToggleKeepWarm(_row);
        KeepWarmSwitch.IsChecked = _row.KeepWarmOn;
    }

    private void KeepWarmCheck_Click(object? sender, RoutedEventArgs e) => ProjectsServices.Store.CheckKeepWarm(_row);

    // ─── Reading what the site really is ─────────────────────────────────

    private int _reading;
    private ProjectSnapshot? _snapshot;
    private SiteSqlConnection? _connection;
    private bool _fullReadRunning;

    private async Task LoadAsync(Parts parts = Parts.All)
    {
        if (_snapshot is null || _fullReadRunning) parts = Parts.All;
        var generation = ++_reading;
        try
        {
            if (parts == Parts.All) await ReadAsync(generation);
            else await ReadPartsAsync(generation, parts);
        }
        catch (Exception ex)
        {
            _stale.Remove(GeneralPanel);
            GeneralPanel.Show([new InspectorSection("Project") { Note = $"Couldn't be read: {ex.Message}" }]);
        }
    }

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
        ProjectsServices.Store.MeasureSize(s.Name);

        var local = await Task.Run(() =>
        {
            var details = iis.GetSiteDetails(s.Name);
            var config = WebConfigInspector.Inspect(s.Directory);
            IReadOnlyList<FolderCheck> folders = Directory.Exists(s.Directory) ? FolderInspector.Inspect(s.Directory, details?.Pool?.Account) : [];
            var assemblies = AssemblyInspector.Inspect(s.Directory, config.BindingRedirects, config.CodeBases, config.ProbingPath);
            string? product = null;
            try
            {
                var dll = Path.Combine(s.Directory, "bin", "DotNetNuke.dll");
                if (File.Exists(dll)) product = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductName;
            }
            catch (Exception) { /* no product name to show */ }
            var disabledHttps = webConfig.ReadFacts(Path.Combine(s.Directory, "web.config")) is { Success: true, Value: { } facts }
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
            DnnProduct = string.IsNullOrWhiteSpace(local.product) || local.product.Contains("://") ? null : local.product,
            Connection = _database, Record = record, Folder = local.folderFacts,
            DatabaseRead = !readDatabase
        };
        Show(snapshot);
        if (readDatabase) await ReadDatabaseAsync(generation, snapshot);
    }

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

    private async Task ReadDatabaseAsync(int generation, ProjectSnapshot snapshot)
    {
        var (connection, database, isDnn) = (_connection!, _database, _row.IsDnn);
        var provisioner = _services.GetRequiredService<IDatabaseProvisioner>();
        var (inspection, hosts) = await Task.Run(async () =>
        {
            var found = await DatabaseInspector.InspectAsync(connection, CancellationToken.None);
            var accounts = found.Problem is null && database is not null && isDnn
                ? await provisioner.ListHostAccountsAsync(database, CancellationToken.None)
                : null;
            return (found, accounts);
        });
        if (generation != _reading) return;
        Show(snapshot with { Database = inspection, DatabaseRead = true, Hosts = hosts });
    }

    private IReadOnlyList<string> _disabledHttps = [];

    private void ShowReading()
    {
        foreach (var panel in new[] { GeneralPanel, IisPanel, DnnPanel, DatabasePanel, AdvancedPanel })
            panel.Show([new InspectorSection("Reading…") { Note = "IIS, the site's folder, web.config, bin and its database are being read." }]);
    }

    private readonly HashSet<InspectorPanel> _stale = [];
    private IReadOnlyList<DetectedIssue> _issues = [];

    /// <summary>Shows <paramref name="snapshot"/> - on the tab that is shown; the others when they are chosen.</summary>
    private void Show(ProjectSnapshot snapshot)
    {
        _snapshot = snapshot;
        _issues = ProjectDiagnostics.Issues(snapshot);
        _stale.UnionWith([GeneralPanel, IisPanel, DnnPanel, DatabasePanel, AdvancedPanel]);
        ShowVisiblePanel();
        ShowHosts(snapshot);
    }

    private void ShowVisiblePanel()
    {
        if (_snapshot is not { } snapshot) return;
        var panel = GeneralTab.IsChecked == true ? GeneralPanel : IisTab.IsChecked == true ? IisPanel : DnnTab.IsChecked == true ? DnnPanel
            : DatabaseTab.IsChecked == true ? DatabasePanel : AdvancedPanel;
        if (!_stale.Remove(panel)) return;
        panel.Show(panel == GeneralPanel ? ProjectDiagnostics.General(snapshot, _issues)
            : panel == IisPanel ? ProjectDiagnostics.Iis(snapshot)
            : panel == DnnPanel ? ProjectDiagnostics.Dnn(snapshot)
            : panel == DatabasePanel ? ProjectDiagnostics.Database(snapshot, _issues)
            : ProjectDiagnostics.Advanced(snapshot, _issues, _disabledHttps));
    }

    // ─── DNN: the host accounts ───────────────────────────────────────────

    private void ShowHosts(ProjectSnapshot s)
    {
        var list = new List<ProjectFact>();
        var hosts = s.Hosts;
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
        ShowFacts(HostFacts, list);

        var installed = s.Config.InstallVersion is not null || s.Database?.Dnn?.Portals.Count > 0 || hosts is { Success: true, Value.Count: > 0 };
        var canChange = installed && _database is not null;
        ChangePasswordButton.IsEnabled = canChange;
        PasswordHint.Text = canChange
            ? "Sets a new password the way DNN stores it and restarts the site. The current password is never shown - DNN keeps only a hash of it."
            : "Once DNN is installed and its web.config names its database, the host password can be changed here.";
    }

    private async void ChangePassword_Click(object? sender, RoutedEventArgs e)
    {
        if (await HostPasswordDialog.ShowAsync(_row.Name, _hosts) is not { } change) return;
        var runner = _services.GetRequiredService<OperationRunner>();
        var s = _row.Project;
        if (await runner.RunAsync($"Change host password of '{_row.Name}'", (sp, reporter, ct) =>
                sp.GetRequiredService<ChangeHostPasswordUseCase>().ExecuteAsync(s.Name, s.Directory, change.User, change.Password, reporter, ct)))
            ProjectNotice.Show($"'{change.User}' signs in to {_row.Name} with the new password.", NoticeKind.Success);
    }
}
