using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.Upgrades;
using DnnManager.Desktop.Services;
using DnnManager.Infrastructure.KeepWarm;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Startup;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IgnoreRules = DnnManager.Application.Deployment.IgnoreRules;

namespace DnnManager.Desktop.Views;

/// <summary>
/// The settings, opened over the page: categories (with a search box) on the left, the chosen one on the right. Every
/// change - in any category - makes <b>Save</b> ready; Save checks the values, saves them and puts them to work at once
/// (<see cref="LiveSettings"/>, the theme, the UI scale, the start at sign-in) - no restart. <b>Discard changes</b> puts
/// the saved values back; <b>Close</b> (and Esc) raises <see cref="CloseRequested"/>. The Windows app's
/// Pages/SettingsPage.
/// </summary>
public partial class SettingsView : UserControl
{
    // Form labels for the settings keys, so a problem reads "Site port must be…" rather than "projects.sitePort must be…".
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["appearance.uiScale"] = "UI scale",
        ["appearance.fontSize"] = "Font size",
        ["appearance.theme"] = "Theme",
        ["projects.baseDirectory"] = "Projects folder",
        ["projects.sitePort"] = "Site port",
        ["projects.hostnameSuffix"] = "Hostname suffix",
        ["projects.dnnReleaseSources"] = "DNN repositories",
        ["projects.dnnUpgradeStops"] = "Upgrade path",
        ["projects.dnnDefaults.installMode"] = "Installation",
        ["projects.dnnDefaults.hostUsername"] = "Host username:",
        ["projects.dnnDefaults.hostEmail"] = "Host e-mail",
        ["projects.dnnDefaults.websiteName"] = "Website name",
        ["projects.dnnDefaults.language"] = "Language",
        ["projects.dnnDefaults.template"] = "Site template",
        ["projects.keepWarm.pingMinutes"] = "Keep warm interval",
        ["projects.keepWarm.warmUpPath"] = "Warm-up page",
        ["projects.keepWarm.pingPath"] = "Keep-alive page",
        ["sqlServer.type"] = "Connection type",
        ["sqlServer.server"] = "Server",
        ["sqlServer.authentication"] = "Authentication",
        ["sqlServer.userName"] = "Username",
        ["sqlServer.host"] = "Server host",
        ["sqlServer.port"] = "Port",
        ["sqlServer.saPassword"] = "Password",
        ["docker.containerName"] = "Container name",
        ["docker.volumeName"] = "Volume name",
        ["docker.edition"] = "Edition",
        ["docker.collation"] = "Collation",
        ["backups.keepDays"] = "Keep backups",
    };

    private readonly IServiceProvider _services = Program.Services;
    private readonly AppOptions _options;
    private readonly OperationRunner _runner;
    private readonly SettingsStore _store;
    private readonly LiveSettings _live;
    private readonly StartupTask _startup;
    private readonly ISecretStore _secrets;
    private readonly AppDataPaths _paths;
    // The default host password and the database server login's password as they are saved - compared with the form
    // to see what Save has to change.
    private string _savedHostPassword = "";
    private string _savedServerPassword = "";
    private string _savedSaPassword = "";
    private string _savedTheme = "system";
    // The connection type the database server fields show, and the server typed for each type - switching to LocalDB
    // and back gives the SQL Server its own server again.
    private string _serverType = SqlServerSettings.ContainerType;
    private readonly Dictionary<string, string> _serverText = new(StringComparer.OrdinalIgnoreCase);
    // Each category (the Tag of its entry in the list): its panel, and the words the search box finds it by.
    private readonly Dictionary<string, (Control Panel, string Keywords)> _categories;
    // Set while the form is being filled in, so that doesn't count as an edit.
    private bool _loading;
    private bool _dirty;
    // Whether DNN Manager starts at sign-in now (the scheduled task is there); null until that has been read.
    private bool? _startsAtSignIn;
    // The form as it was filled in from the saved settings - what an edit is compared with.
    private string _savedForm = "";

    public SettingsView()
    {
        _options = _services.GetRequiredService<IOptions<AppOptions>>().Value;
        _runner = _services.GetRequiredService<OperationRunner>();
        _store = _services.GetRequiredService<SettingsStore>();
        _live = _services.GetRequiredService<LiveSettings>();
        _startup = _services.GetRequiredService<StartupTask>();
        _secrets = _services.GetRequiredService<ISecretStore>();
        _paths = _services.GetRequiredService<AppDataPaths>();
        InitializeComponent();
        // Focused when shown, so Esc reaches it.
        AttachedToVisualTree += (_, _) => Focus();
        // Closed with the theme only tried: the saved one again.
        DetachedFromVisualTree += (_, _) =>
        {
            if (ChosenTheme != _savedTheme) App.ApplyTheme(_savedTheme);
        };

        _categories = new Dictionary<string, (Control, string)>
        {
            ["General"] = (GeneralPanel, "general start sign in startup close closing quit exit background tray notification area keep running appearance theme dark light scale zoom ui font text size bigger smaller animations animation motion slide fade reduce effects terminal shell powershell command prompt git bash zsh font family size"),
            ["Projects"] = (ProjectsPanel, "projects folder base directory hostname suffix site port address url dnn defaults install installation automatic manual setup wizard host account username password e-mail email website name language culture template keep warm alive keepalive warm-up idle time-out timeout interval ping cold start slow fast recycle backups backup deployments deployment packages clone copy delete old days retention keep onedrive"),
            ["Releases"] = (ReleasesPanel, "dnn releases repositories github versions install packages keep download upgrade path stops steps"),
            ["Sql"] = (SqlPanel, "database server sql server express localdb file connection type local container docker host port user username sa password windows authentication login username ssms management studio remember test connection"),
            ["Docker"] = (DockerPanel, "docker container name volume edition mssql_pid collation desktop engine install start set up docker-compose compose yml test"),
            ["Deploy"] = (DeployPanel, "source control deploy deployment ftp sftp ftps server live staging production push pull compare open history versions snapshots rollback roll back keep days parallel files at the same time backups verify checksum sha-256 ignore ignored gitignore rules web.config app_data"),
            ["Iis"] = (IisPanel, "iis windows features test set up enable " + string.Join(' ', _options.RequiredIisFeatures.Select(f => $"{f.Label} {f.Name}"))),
            ["Keyboard"] = (KeyboardPanel, "keyboard shortcuts shortcut keys key bindings keybindings hotkeys accelerators " +
                                           string.Join(' ', (Commands?.All ?? []).Select(c => $"{c.Title} {c.Area}"))),
            ["Help"] = (HelpPanel, "help getting started guide welcome tutorial onboarding introduction beginner tour walkthrough what's new whats new changes user guide documentation docs manual f1"),
            ["About"] = (AboutPanel, "about version build commit release channel update upgrade install new latest license mit repository github documentation docs runtime .net framework windows macos linux architecture administrator iis docker components libraries your files folders settings backups logs packages key"),
        };

        KeepDnnPackagesHint.Text = "Installs the same version again without downloading it.";
        ToolTip.SetTip(KeepDnnPackagesHint, _paths.PackagesDirectory);
        ShowUpgradePath();
        // Test and set up what the settings describe - working with the saved settings.
        DockerCard.Attach(_services);
        SqlCard.Attach(_services);
        IisCard.Attach(_services);
        DockerCard.ContainerChanged += (_, _) => SqlCard.Test();
        // The data volume's own sa password, saved by Set up docker-compose: shown here too - a Save would put the old one back.
        DockerCard.PasswordAdopted += (_, password) =>
        {
            var clean = !HasUnsavedChanges;
            _savedSaPassword = password;
            SaPassword.Text = password;
            if (clean) _savedForm = FormSnapshot();
            Edited();
        };
        ShowFolders();

        foreach (var language in DnnAccountRules.Languages) DnnLanguage.Items.Add(new ComboBoxItem { Content = LanguageName(language), Tag = language });
        foreach (var template in DnnAccountRules.Templates) DnnTemplate.Items.Add(new ComboBoxItem { Content = template, Tag = template });
        foreach (var minutes in KeepWarmSettings.PingIntervals) KeepWarmInterval.Items.Add(KeepWarmIntervalItem(minutes));
        foreach (var days in BackupSettings.KeepDayChoices) BackupKeepDays.Items.Add(BackupKeepDaysItem(days));
        BackupsHint.Text = "Older ones are deleted at each start - they can't be brought back.";
        ToolTip.SetTip(BackupsHint, $"Project backups: {_paths.BackupsDirectory}\nDeployment packages: {_paths.DeploymentsDirectory}");
        foreach (var days in DeploySettings.KeepVersionsChoices)
            DeployKeepVersions.Items.Add(new ComboBoxItem { Content = days == 0 ? "For good" : days < 365 ? $"{days} days" : "1 year", Tag = days });
        foreach (var scale in AppearanceSettings.UiScales)
            UiScale.Items.Add(new ComboBoxItem { Content = scale == 100 ? "100 % (default)" : $"{scale} %", Tag = scale });
        foreach (var size in AppearanceSettings.FontSizes)
            AppFontSize.Items.Add(new ComboBoxItem { Content = size == 13 ? "13 px (default)" : $"{size} px", Tag = (double)size });
        foreach (var (key, name) in TerminalShells()) DefaultShell.Items.Add(new ComboBoxItem { Content = name, Tag = key });
        TerminalFont.Items.Add(new ComboBoxItem { Content = "Default", Tag = "" });
        foreach (var font in InstalledFonts()) TerminalFont.Items.Add(new ComboBoxItem { Content = font, Tag = font });
        foreach (var size in Enumerable.Range(9, 12).Concat([22, 24, 28])) TerminalFontSize.Items.Add(new ComboBoxItem { Content = size.ToString(), Tag = size });

        ShowWhatThisSystemHas();
        ShowEnvironmentOverrides();
        Load();
        WatchEdits();
        ShowStartAtSignIn();
        ShowHelpRows();
        Categories.SelectedIndex = 0;
    }

    // ─── Events and what MainWindow asks ──────────────────────────────────

    /// <summary>Close (or Esc) was pressed - the window goes back to the page Settings was opened from.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>A button on Help was pressed - the main window runs it: <c>guide</c>, <c>tour</c> or <c>whatsNew</c>.</summary>
    public event EventHandler<string>? HelpRequested;

    private bool _offerGuide, _offerTour, _offerWhatsNew, _offerPageHelp;

    /// <summary>Whether Help offers the getting started guide - once the main window has one to show (HelpRequested "guide").</summary>
    public bool OfferGuide { get => _offerGuide; set { _offerGuide = value; ShowHelpRows(); } }

    /// <summary>Whether Help offers the tour of the window (HelpRequested "tour").</summary>
    public bool OfferTour { get => _offerTour; set { _offerTour = value; ShowHelpRows(); } }

    /// <summary>Whether Help offers What's new - not to a user still on the version they started with (HelpRequested "whatsNew").</summary>
    public bool OfferWhatsNew { get => _offerWhatsNew; set { _offerWhatsNew = value; ShowHelpRows(); } }

    /// <summary>Whether Help says F1 explains the page - once the main window has F1.</summary>
    public bool OfferPageHelp { get => _offerPageHelp; set { _offerPageHelp = value; ShowHelpRows(); } }

    private void ShowHelpRows()
    {
        GuideRow.IsVisible = _offerGuide;
        TourRow.IsVisible = _offerTour;
        WhatsNewRow.IsVisible = _offerWhatsNew;
        PageHelpRow.IsVisible = _offerPageHelp;
        // The first row shown has the line above it too.
        var first = new[] { GuideRow, TourRow, PageHelpRow, WhatsNewRow, UserGuideRow }.First(r => r.IsVisible);
        foreach (var row in new[] { GuideRow, TourRow, PageHelpRow, WhatsNewRow, UserGuideRow })
            row.BorderThickness = row == first ? new Thickness(0, 1, 0, 1) : new Thickness(0, 0, 0, 1);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void HelpGuide_Click(object? sender, RoutedEventArgs e) => HelpRequested?.Invoke(this, "guide");

    private void HelpTour_Click(object? sender, RoutedEventArgs e) => HelpRequested?.Invoke(this, "tour");

    private void HelpWhatsNew_Click(object? sender, RoutedEventArgs e) => HelpRequested?.Invoke(this, "whatsNew");

    private void HelpUserGuide_Click(object? sender, RoutedEventArgs e) => Shell.Open($"{AboutInfo.Repository}/blob/main/.docs/user-guide.md");

    // Esc: the search is emptied first; then back to the page Settings was opened from. Ctrl+F: the search;
    // Ctrl+PageDown / Ctrl+PageUp: the next or previous category.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
            else CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (command && e.Key == Key.F)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (command && e.Key is Key.PageDown or Key.PageUp)
        {
            StepCategory(e.Key == Key.PageDown ? 1 : -1);
            e.Handled = true;
        }
    }

    // ─── Categories ───────────────────────────────────────────────────────

    private void Categories_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_categories is null) return; // raised during InitializeComponent
        var selected = (Categories.SelectedItem as ListBoxItem)?.Tag as string;
        foreach (var (key, category) in _categories) category.Panel.IsVisible = key == selected;
        FormScroller.Offset = default;
        if (selected == "About") ShowAbout();
        if (selected == "Keyboard") ShowShortcuts();
    }

    /// <summary>The category shown: General, Projects, Releases, Sql, Docker, Iis, Deploy, Keyboard, Help or About.</summary>
    public string? Category => (Categories.SelectedItem as ListBoxItem)?.Tag as string;

    /// <summary>The keyboard in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private List<ListBoxItem> ShownCategories => Categories.Items.OfType<ListBoxItem>().Where(i => i.IsVisible).ToList();

    /// <summary>The next category shown (<paramref name="by"/> 1) or the one before (-1), round the end - Ctrl+PageUp / Ctrl+PageDown.</summary>
    public void StepCategory(int by)
    {
        var shown = ShownCategories;
        if (shown.Count == 0) return;
        var index = shown.IndexOf(Categories.SelectedItem as ListBoxItem ?? shown[0]);
        Categories.SelectedItem = shown[((index + by) % shown.Count + shown.Count) % shown.Count];
    }

    public void ShowCategory(string key)
    {
        if (Categories.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == key) is not { } item) return;
        // Left out by the search: the search goes, so the category is in the list again.
        if (!item.IsVisible) SearchBox.Text = "";
        Categories.SelectedItem = item;
    }

    // Leaves the categories that have a setting matching every word typed, and opens the first of them.
    private void Search_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_categories is null) return; // raised during InitializeComponent
        var words = (SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in Categories.Items.OfType<ListBoxItem>())
        {
            var keywords = _categories[(string)item.Tag!].Keywords;
            item.IsVisible = words.All(w => keywords.Contains(w, StringComparison.OrdinalIgnoreCase)) && Offered(item);
        }
        var shown = ShownCategories;
        NoMatch.IsVisible = shown.Count == 0;
        if (shown.Count > 0 && !shown.Contains(Categories.SelectedItem)) Categories.SelectedItem = shown[0];
    }

    // IIS is only there on Windows.
    private bool Offered(ListBoxItem item) => item != IisItem || Platform.Supports(PlatformFeature.Iis);

    // ─── What only Windows has ────────────────────────────────────────────

    /// <summary>On macOS and Linux: what only Windows does is left out or says why - IIS, LocalDB, keep warm, start at sign-in.</summary>
    private void ShowWhatThisSystemHas()
    {
        if (!Platform.Supports(PlatformFeature.Iis))
        {
            IisItem.IsVisible = false;
            IisIntro.Text = Platform.WhyNot(PlatformFeature.Iis) ?? "";
            IisCard.IsVisible = false;
        }
        if (Platform.WhyNot(PlatformFeature.LocalDb) is { } noLocalDb)
        {
            ServerLocalDb.IsEnabled = false;
            LocalDbHint.Text = noLocalDb;
        }
        if (Platform.WhyNot(PlatformFeature.KeepWarm) is { } noKeepWarm)
        {
            foreach (var control in new Control[] { KeepWarmInterval, KeepWarmPingPath, KeepWarmWarmUpPath }) control.IsEnabled = false;
            KeepWarmGroup.Children.Add(new TextBlock { Classes = { "hint" }, Text = noKeepWarm });
        }
        if (Platform.WhyNot(PlatformFeature.StartWithSystem) is { } noStart)
        {
            StartAtSignIn.IsEnabled = false;
            StartAtSignInHint.Text = noStart;
        }
        if (!OperatingSystem.IsWindows()) TerminalOptions.Children.Insert(0, new TextBlock { Classes = { "hint" }, Margin = new Thickness(0, 0, 0, 4),
            Text = "Saved with your settings; the terminal itself comes to DNN Manager on " + Platform.Name + " in an update." });
    }

    // ─── About ────────────────────────────────────────────────────────────

    // Asked once while the view is open: Docker's version - and GitHub's latest release when there is no updater to follow.
    private string? _docker;
    private UpdateInfo _update = new("checking…", AboutHealth.None);
    private bool _askingAbout, _askedAbout, _followingUpdates;

    /// <summary>
    /// The update status: the app's updater's (followed while About is shown: checking, downloading, installing) when
    /// it has one, else GitHub asked here.
    /// </summary>
    private UpdateInfo Update => _services.GetService<AppUpdater>() is { } updater
        ? new UpdateInfo(updater.StatusText, updater.State switch
        {
            UpdateState.UpToDate => AboutHealth.Ok,
            UpdateState.Available or UpdateState.Downloading or UpdateState.Installing or UpdateState.Restarting => AboutHealth.Warning,
            UpdateState.Unreachable => AboutHealth.Unknown,
            UpdateState.Failed => AboutHealth.Bad,
            _ => AboutHealth.None
        }, updater.State == UpdateState.Unreachable ? updater.Problem : null)
        : _update;

    /// <summary>Shows About - then, the first time, asks Docker (and GitHub) and shows it again.</summary>
    private async void ShowAbout()
    {
        var updater = _services.GetService<AppUpdater>();
        if (updater is not null && !_followingUpdates)
        {
            _followingUpdates = true;
            System.ComponentModel.PropertyChangedEventHandler follow = (_, _) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (AboutPanel.IsVisible) ShowAboutInfo(); });
            updater.PropertyChanged += follow;
            DetachedFromVisualTree += (_, _) => updater.PropertyChanged -= follow;
        }
        ShowAboutInfo();
        if (updater is not null) _ = updater.CheckAsync(unlessWithin: TimeSpan.FromMinutes(10));
        if (_askingAbout || _askedAbout) return;
        _askingAbout = true;
        var update = updater is null ? AboutInfo.UpdateAsync() : null;
        _docker = await AboutInfo.DockerAsync(_services.GetRequiredService<IPrerequisiteChecker>(), _options.Docker.ContainerName);
        ShowAboutInfo();
        if (update is not null) _update = await update;
        _askingAbout = false;
        _askedAbout = true;
        ShowAboutInfo();
    }

    /// <summary>The sections, as the Windows app's InspectorPanel: a title, then each value with its name and dot.</summary>
    private void ShowAboutInfo()
    {
        AboutInfoPanel.Children.Clear();
        var first = true;
        foreach (var section in AboutInfo.Sections(Update, _docker))
        {
            var title = new TextBlock { Margin = new Thickness(0, first ? 12 : 22, 0, 6), TextWrapping = TextWrapping.Wrap };
            title.Inlines = [new Avalonia.Controls.Documents.Run(section.Title) { FontWeight = FontWeight.SemiBold },
                             new Avalonia.Controls.Documents.Run("  " + section.Subtitle)];
            AboutInfoPanel.Children.Add(title);
            first = false;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
            var row = 0;
            foreach (var value in section.Rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var name = new TextBlock { Text = value.Name, Classes = { "muted" }, Margin = new Thickness(0, 4, 12, 4), TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(name, row);
                grid.Children.Add(name);
                var shown = new StackPanel { Margin = new Thickness(0, 4) };
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (value.Health != AboutHealth.None)
                {
                    var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
                    dot.Bind(Shape.FillProperty, dot.GetResourceObservable(value.Health switch
                    {
                        AboutHealth.Ok => "SuccessText", AboutHealth.Warning => "LogWarn", AboutHealth.Bad => "ErrorText", _ => "TextMuted"
                    }));
                    line.Children.Add(dot);
                }
                line.Children.Add(new SelectableTextBlock { Text = value.Value, TextWrapping = TextWrapping.Wrap });
                shown.Children.Add(line);
                if (value.Note is { Length: > 0 } note) shown.Children.Add(new TextBlock { Text = note, Classes = { "hint" }, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetRow(shown, row);
                Grid.SetColumn(shown, 1);
                grid.Children.Add(shown);
                row++;
            }
            AboutInfoPanel.Children.Add(grid);
        }
    }

    /// <summary>The folders with your files, each with Open.</summary>
    private void ShowFolders()
    {
        foreach (var (name, folder) in new[]
                 {
                     ("Settings", _paths.Root), ("Backups", _paths.BackupsDirectory), ("Deployments", _paths.DeploymentsDirectory),
                     ("Logs", _paths.LogsDirectory), ("DNN packages", _paths.PackagesDirectory)
                 })
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,Auto"), Margin = new Thickness(0, 4) };
            var label = new TextBlock { Text = name, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
            var path = new TextBlock { Text = folder, Classes = { "folderPath" } };
            ToolTip.SetTip(path, folder);
            Grid.SetColumn(path, 1);
            var open = new Button { Content = "Open", Classes = { "link" }, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(open, "Open folder");
            Avalonia.Automation.AutomationProperties.SetName(open, $"Open the {name} folder");
            Grid.SetColumn(open, 2);
            open.Click += (_, _) => OpenFolder(folder);
            grid.Children.Add(label);
            grid.Children.Add(path);
            grid.Children.Add(open);
            Folders.Children.Add(grid);
        }
    }

    /// <summary>Opens one of the folders with your files - made first when nothing has needed it yet (or Clean up emptied it).</summary>
    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast.Show($"{folder} couldn't be made: {ex.Message}", ToastKind.Error);
            return;
        }
        Shell.Open(folder);
    }

    // ─── Upgrade path ─────────────────────────────────────────────────────

    private void ShowUpgradePath()
    {
        var known = DnnUpgradeStep.Name(DnnUpgradePath.Versions[^1]);
        var edits = _options.DnnUpgradeStops.Select(s => (Stop: s, Version: SettingRules.UpgradeStopVersion(s.Version))).Where(e => e.Version is not null).ToList();
        string? Part(string what, Func<DnnUpgradeStopSetting, Version, bool> which) =>
            edits.Where(e => which(e.Stop, e.Version!)).Select(e => DnnUpgradeStep.Name(e.Version!)).ToList() is { Count: > 0 } names
                ? $"{what} {string.Join(", ", names)}" : null;
        var parts = new[]
        {
            Part("added", (s, v) => !s.Removed && !DnnUpgradePath.Versions.Contains(v)),
            Part("changed", (s, v) => !s.Removed && DnnUpgradePath.Versions.Contains(v)),
            Part("taken off", (s, _) => s.Removed)
        }.OfType<string>().ToList();
        UpgradePathHint.Text = parts.Count == 0
            ? $"Upgrade DNN goes through DNN's own path, known up to DNN {known}. Edit it when DNN lists a new version, or export it to use on another PC."
            : $"Upgrade DNN goes through DNN's path, known up to DNN {known}, with your changes: {string.Join("; ", parts)}.";
        // The editor (the Windows app's DnnUpgradePathDialog) isn't here yet - the path is used as saved.
        EditUpgradePathButton.IsEnabled = false;
        ToolTip.SetTip(EditUpgradePathButton, "Editing the upgrade path comes back in an update - the saved path is used meanwhile.");
        ToolTip.SetShowOnDisabled(EditUpgradePathButton, true);
    }

    private void EditUpgradePath_Click(object? sender, RoutedEventArgs e) { }

    // ─── Start at sign-in ─────────────────────────────────────────────────

    /// <summary>Whether the scheduled task is there - and which exe it starts, when that isn't this one. Windows only.</summary>
    private async void ShowStartAtSignIn()
    {
        if (!Platform.Supports(PlatformFeature.StartWithSystem)) return;
        try
        {
            var target = await _startup.GetTargetAsync();
            _startsAtSignIn = target is not null;
            _loading = true;
            StartAtSignIn.IsChecked = target is not null;
            _loading = false;
            // Installed for this user only: a task with administrator rights for a program others can change - offered
            // only to be switched off.
            var refusal = Environment.ProcessPath is { } self ? await Task.Run(() => StartupTask.Refusal(self)) : null;
            StartAtSignIn.IsEnabled = refusal is null || target is not null;
            if (refusal is not null)
            {
                StartAtSignInHint.Text = "Needs DNN Manager installed for all users (its Setup).";
                ToolTip.SetTip(StartAtSignInHint, refusal);
            }
            Edited();
            if (refusal is not null) return;
            var other = target is { Length: > 0 } && Environment.ProcessPath is { } exe &&
                        !string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
            if (other)
                StartAtSignInHint.Text = $"Set up to start {target} - untick and tick it again to start this copy ({Environment.ProcessPath}) instead.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            _loading = false;
            StartAtSignInHint.Text = $"Could not read the scheduled task: {ex.Message}";
        }
    }

    /// <summary>
    /// Makes or removes the scheduled task when the box was changed - part of Save. False (the box is put back, with the
    /// reason said) when Windows refused.
    /// </summary>
    private async Task<bool> ApplyStartAtSignInAsync()
    {
        var enable = StartAtSignIn.IsChecked == true;
        if (_startsAtSignIn is null || enable == _startsAtSignIn) return true;
        StartAtSignIn.IsEnabled = false;
        try
        {
            var exe = Environment.ProcessPath;
            var result = !enable ? await _startup.DisableAsync()
                : exe is null ? Domain.Result.Fail("the program's own path is unknown.")
                : await _startup.EnableAsync(exe);
            if (result.Success)
            {
                _startsAtSignIn = enable;
                if (enable) StartAtSignInHint.Text = "As Administrator, without a prompt at every sign-in (a scheduled task).";
                return true;
            }
            _loading = true;
            StartAtSignIn.IsChecked = !enable;
            _loading = false;
            ShowError($"The other settings are saved, but the scheduled task could not be {(enable ? "set up" : "removed")}: {result.Error}");
            return false;
        }
        finally
        {
            StartAtSignIn.IsEnabled = true;
        }
    }

    // ─── Loading and saving ───────────────────────────────────────────────

    private void Load()
    {
        UserSettings saved;
        try
        {
            saved = _store.Read();
        }
        catch (SettingsException ex)
        {
            // Edited outside the app since it started - say what's wrong rather than overwrite it.
            Form.IsEnabled = false;
            SetDirty(false);
            ShowError(string.Join(Environment.NewLine, [ex.Message, .. ex.Problems, "Fix the file, then open Settings again."]));
            return;
        }

        _savedHostPassword = ReadSecret(SecretNames.DefaultHostPassword) ?? DnnDefaultsSettings.DefaultHostPassword;
        _savedServerPassword = ReadSecret(SecretNames.DatabaseServerPassword) ?? "";
        _savedSaPassword = saved.SqlServer.SaPassword;
        _savedTheme = (saved.Appearance.Theme ?? "system").ToLowerInvariant();
        ShowSettings(saved, _savedHostPassword, _savedServerPassword, _startsAtSignIn);
        // Discarded: the theme that was only tried goes too.
        App.ApplyTheme(_savedTheme);

        _savedForm = FormSnapshot();
        ShowError(null);
        SetDirty(false);
    }

    private string? ReadSecret(string name)
    {
        try
        {
            return _secrets.Read(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException
                                       or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Fills the form with <paramref name="saved"/> and the passwords that go with them.</summary>
    private void ShowSettings(UserSettings saved, string hostPassword, string serverPassword, bool? startAtSignIn)
    {
        _loading = true;
        ShowAppearance(saved.Appearance);
        ShowTerminal(saved.Terminal);
        if (startAtSignIn is { } starts) StartAtSignIn.IsChecked = starts;
        var p = saved.Projects;
        var docker = saved.Docker;
        BaseDirectory.Text = p.BaseDirectory;
        SitePort.Text = p.SitePort.ToString(CultureInfo.InvariantCulture);
        HostnameSuffix.Text = p.HostnameSuffix;
        ReleaseApis.Text = string.Join(Environment.NewLine, p.DnnReleaseSources);
        KeepDnnPackages.IsChecked = p.KeepDnnPackages;
        ContainerName.Text = docker.ContainerName;
        VolumeName.Text = docker.VolumeName;
        MssqlPid.Text = docker.Edition;
        Collation.Text = docker.Collation;
        SsmsRememberPassword.IsChecked = saved.Ssms.RememberPassword;
        KeepRunningWhenClosed.IsChecked = saved.Window.KeepRunningWhenClosed;
        CheckForUpdatesAtStart.IsChecked = saved.Updates.CheckAtStart;
        var dnn = p.DnnDefaults;
        InstallAutomatic.IsChecked = dnn.Automatic;
        InstallManual.IsChecked = !dnn.Automatic;
        HostUsername.Text = dnn.HostUsername;
        HostEmail.Text = dnn.HostEmail;
        WebsiteName.Text = dnn.WebsiteName;
        Select(DnnLanguage, dnn.Language);
        Select(DnnTemplate, dnn.Template);
        ShowKeepWarm(p.KeepWarm);
        ShowBackupKeepDays(saved.Backups.KeepDays);
        ShowDeploy(saved.Deploy);
        HostPassword.Text = hostPassword;
        ShowServer(saved.SqlServer);
        ServerPassword.Text = serverPassword;
        _loading = false;
        UpdatePreview();
        ShowRulesProblems();
    }

    /// <summary>Every edit on the form, and what follows each.</summary>
    private void WatchEdits()
    {
        foreach (var box in new[] { BaseDirectory, SitePort, HostnameSuffix, ReleaseApis, ContainerName, ContainerIp, ContainerUser,
                                    VolumeName, DefaultPort, Collation, MssqlPid, HostUsername, HostEmail, WebsiteName, ServerName,
                                    ServerUserName, KeepWarmPingPath, KeepWarmWarmUpPath, DeployParallel, DeployIgnoreRules,
                                    SaPassword, HostPassword, ServerPassword })
            box.TextChanged += (_, _) => Edited();
        SitePort.TextChanged += (_, _) => UpdatePreview();
        HostnameSuffix.TextChanged += (_, _) => UpdatePreview();
        DeployIgnoreRules.TextChanged += (_, _) => ShowRulesProblems();
        foreach (var box in new[] { SsmsRememberPassword, KeepDnnPackages, KeepRunningWhenClosed, CheckForUpdatesAtStart, DeployCompareOnOpen,
                                    DeployKeepBackups, DeployVerifyUploads, Animations, StartAtSignIn })
            box.IsCheckedChanged += (_, _) => Edited();
        foreach (var combo in new[] { UiScale, AppFontSize, DefaultShell, TerminalFont, TerminalFontSize, KeepWarmInterval, BackupKeepDays, DeployKeepVersions })
            combo.SelectionChanged += (_, _) => Edited();
        foreach (var combo in new[] { DnnLanguage, DnnTemplate })
            combo.SelectionChanged += (_, _) => { UpdatePreview(); Edited(); };
        // The theme is tried at once; Save keeps it.
        ThemeBox.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            App.ApplyTheme(ChosenTheme);
            Edited();
        };
        foreach (var radio in new[] { InstallAutomatic, InstallManual }) radio.IsCheckedChanged += (_, _) => Edited();
        foreach (var radio in new[] { ServerContainer, ServerSqlServer, ServerLocalDb })
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) ServerType_Checked(); };
        foreach (var radio in new[] { WindowsAuth, SqlAuth })
            radio.IsCheckedChanged += (_, _) =>
            {
                if (_loading || radio.IsChecked != true) return;
                ShowServerFields();
                Edited();
            };
    }

    /// <summary>
    /// Something on the form was touched: there is something to save when it now differs from what is saved - a box
    /// unticked and ticked again, or a value typed back, leaves nothing to save.
    /// </summary>
    private void Edited()
    {
        if (_loading || !Form.IsEnabled) return;
        var startChanged = _startsAtSignIn is { } starts && (StartAtSignIn.IsChecked == true) != starts;
        SetDirty(startChanged || FormSnapshot() != _savedForm);
    }

    private static string T(TextBox box) => (box.Text ?? "").Trim();

    private static object? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag;

    /// <summary>Every value on the form as it would be saved, in one string - to tell whether anything differs.</summary>
    private string FormSnapshot()
    {
        var terminal = ChosenTerminal;
        return string.Join('\u001f',
            T(BaseDirectory), T(SitePort), T(HostnameSuffix).Trim('.'),
            string.Join('\n', (ReleaseApis.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            KeepDnnPackages.IsChecked == true, ServerSnapshot(),
            T(ContainerName), T(VolumeName), T(MssqlPid), T(Collation),
            SsmsRememberPassword.IsChecked == true, ChosenTheme, ChosenUiScale, ChosenFontSize, Animations.IsChecked == true,
            KeepRunningWhenClosed.IsChecked == true, CheckForUpdatesAtStart.IsChecked == true, terminal.DefaultShell, terminal.FontFamily, terminal.FontSize,
            InstallAutomatic.IsChecked == true, T(HostUsername), HostPassword.Text ?? "", T(HostEmail), T(WebsiteName),
            TagOf(DnnLanguage), TagOf(DnnTemplate),
            TagOf(KeepWarmInterval), KeepWarmSettings.NormalizePath(KeepWarmPingPath.Text),
            KeepWarmSettings.NormalizePath(KeepWarmWarmUpPath.Text), TagOf(BackupKeepDays),
            DeployCompareOnOpen.IsChecked == true, TagOf(DeployKeepVersions), T(DeployParallel),
            DeployKeepBackups.IsChecked == true, DeployVerifyUploads.IsChecked == true, (DeployIgnoreRules.Text ?? "").Replace("\r\n", "\n"));
    }

    /// <summary>The form has edits that aren't saved - leaving the view would lose them.</summary>
    public bool HasUnsavedChanges => _dirty;

    /// <summary>A password on the form was changed and not saved.</summary>
    public bool HasUnsavedPassword =>
        _dirty && ((HostPassword.Text ?? "") != _savedHostPassword || (ServerPassword.Text ?? "") != _savedServerPassword ||
                   (SaPassword.Text ?? "") != _savedSaPassword);

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        // Save and Discard come to life with the first edit, until it's saved or discarded.
        SaveButton.IsEnabled = dirty;
        DiscardButton.IsVisible = dirty;
        StatusText.Text = dirty ? "You have unsaved changes." : "";
        // The Docker and database server cards work with the saved settings - not with edits that aren't saved yet.
        foreach (var (card, hint) in new (Control, Control)[] { (DockerCard, DockerSaveFirst), (SqlCard, SqlSaveFirst) })
        {
            card.IsEnabled = !dirty;
            hint.IsVisible = dirty;
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveAsync();

    private void Discard_Click(object? sender, RoutedEventArgs e) => Load();

    /// <summary>Checks the form, saves it and puts the new settings to work - from the next thing done, no restart.</summary>
    public async Task SaveAsync()
    {
        // Not under a running operation: it would go on with half the old settings and half the new.
        if (_runner.IsBusy)
        {
            Say($"'{_runner.Current}' is still running - save once it has finished.");
            return;
        }

        UserSettings settings;
        try
        {
            // Start from the file, so keys this view doesn't edit (IIS features, shortcuts, layout…) stay as they are.
            settings = _store.Read();
        }
        catch (SettingsException ex)
        {
            ShowError($"Not saved - {ex.Message}");
            return;
        }

        var (error, category) = ApplyForm(settings);
        if (error is null)
        {
            var problem = settings.Validate().FirstOrDefault();
            if (problem is not null)
            {
                error = $"{Labels.GetValueOrDefault(problem.Key, problem.Key)} {problem.Message}";
                category = CategoryOf(problem.Key);
            }
        }
        if (error is not null)
        {
            // Open the category the problem is in - it may not be the one on screen.
            SearchBox.Text = "";
            if (category is not null) ShowCategory(category);
            ShowError(error);
            Say($"Not saved: {error}");
            return;
        }

        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not save the settings to {_store.Location}: {ex.Message}");
            return;
        }

        _live.Apply(settings);
        _savedSaPassword = settings.SqlServer.SaPassword;
        _savedTheme = settings.Appearance.Theme;
        App.ApplyTheme(settings.Appearance.Theme);
        if (AppearanceLayout.MainWindow is { } window) AppearanceLayout.Apply(window, settings.Appearance);
        var secretsSaved = SaveSecrets(settings);
        // What another connection type's fields still show wasn't saved - show what is.
        _loading = true;
        ShowServer(settings.SqlServer);
        _loading = false;
        _savedForm = FormSnapshot();
        if (secretsSaved) ShowError(null);
        SetDirty(false);
        // No message when it worked - the Save button greying out says so; what failed says it itself.
        await ApplyStartAtSignInAsync();
    }

    /// <summary>
    /// The secrets that go with the saved settings: the default host password, and the database server login's
    /// password when SQL Server authentication is what is saved. False (said under the title) when the system refused.
    /// </summary>
    private bool SaveSecrets(UserSettings settings)
    {
        var password = HostPassword.Text ?? "";
        if (password != _savedHostPassword)
        {
            var stored = password.Length > 0 ? _secrets.Write(SecretNames.DefaultHostPassword, password) : _secrets.Delete(SecretNames.DefaultHostPassword);
            if (!stored.Success)
            {
                ShowError($"The other settings are saved, but the host password isn't: {stored.Error}");
                return false;
            }
            _savedHostPassword = password;
        }
        var serverPassword = ServerPassword.Text ?? "";
        if (UsesLogin(settings.SqlServer) && serverPassword != _savedServerPassword)
        {
            var stored = serverPassword.Length > 0
                ? _secrets.Write(SecretNames.DatabaseServerPassword, serverPassword)
                : _secrets.Delete(SecretNames.DatabaseServerPassword);
            if (!stored.Success)
            {
                ShowError($"The other settings are saved, but the database server's password isn't: {stored.Error}");
                return false;
            }
            _savedServerPassword = serverPassword;
        }
        return true;
    }

    /// <summary>The category that edits the settings key <paramref name="key"/>, e.g. <c>sqlServer.port</c>.</summary>
    private static string? CategoryOf(string key) => key switch
    {
        "projects.dnnReleaseSources" or "projects.dnnUpgradeStops" => "Releases",
        _ when key.StartsWith("terminal.", StringComparison.Ordinal) || key.StartsWith("appearance.", StringComparison.Ordinal) ||
               key.StartsWith("window.", StringComparison.Ordinal) => "General",
        _ when key.StartsWith("projects.", StringComparison.Ordinal) || key.StartsWith("backups.", StringComparison.Ordinal) => "Projects",
        _ when key.StartsWith("sqlServer.", StringComparison.Ordinal) => "Sql",
        _ when key.StartsWith("docker.", StringComparison.Ordinal) => "Docker",
        _ when key.StartsWith("iis.", StringComparison.Ordinal) => "Iis",
        _ when key.StartsWith("deploy.", StringComparison.Ordinal) => "Deploy",
        _ => null
    };

    /// <summary>
    /// Copies the form into <paramref name="settings"/>, or returns the first value that isn't a number where one is
    /// needed, with the category it is in.
    /// </summary>
    private (string? Error, string? Category) ApplyForm(UserSettings settings)
    {
        if (!int.TryParse(T(SitePort), out var sitePort))
            return ("Site port must be a number between 1 and 65535.", "Projects");
        var serverType = ChosenServerType;
        var sqlPort = 0;
        if (serverType == SqlServerSettings.ContainerType && !int.TryParse(T(DefaultPort), out sqlPort))
            return ("Port must be a number between 1 and 65535.", "Sql");

        var p = settings.Projects;
        p.BaseDirectory = T(BaseDirectory);
        p.SitePort = sitePort;
        p.HostnameSuffix = T(HostnameSuffix).Trim('.');
        p.DnnReleaseSources = (ReleaseApis.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        p.KeepDnnPackages = KeepDnnPackages.IsChecked == true;
        p.KeepWarm = new KeepWarmSettings
        {
            PingMinutes = TagOf(KeepWarmInterval) as int? ?? p.KeepWarm.PingMinutes,
            PingPath = KeepWarmSettings.NormalizePath(KeepWarmPingPath.Text),
            WarmUpPath = KeepWarmSettings.NormalizePath(KeepWarmWarmUpPath.Text)
        };

        // Only the chosen connection type's fields - the others keep what is saved.
        var sql = settings.SqlServer;
        sql.Type = serverType;
        if (serverType == SqlServerSettings.ContainerType)
        {
            sql.Host = T(ContainerIp);
            sql.Port = sqlPort;
            // The container's login is the same setting as the SQL Server login (sqlServer.userName); empty is sa.
            sql.UserName = T(ContainerUser);
            sql.SaPassword = SaPassword.Text ?? "";
        }
        else
        {
            sql.Server = T(ServerName);
        }
        if (serverType == "sqlServer")
        {
            sql.Authentication = SqlAuth.IsChecked == true ? "sql" : "windows";
            if (SqlAuth.IsChecked == true) sql.UserName = T(ServerUserName);
        }

        var docker = settings.Docker;
        docker.ContainerName = T(ContainerName);
        docker.VolumeName = T(VolumeName);
        docker.Edition = T(MssqlPid);
        docker.Collation = T(Collation);

        p.DnnDefaults = new DnnDefaultsSettings
        {
            InstallMode = InstallAutomatic.IsChecked == true ? "automatic" : "manual",
            HostUsername = T(HostUsername),
            HostEmail = T(HostEmail),
            WebsiteName = T(WebsiteName),
            Language = TagOf(DnnLanguage) as string ?? "en-US",
            Template = TagOf(DnnTemplate) as string ?? DnnAccountRules.Templates[0]
        };
        var hostPassword = HostPassword.Text ?? "";
        if (hostPassword.Length > 0 && DnnAccountRules.PasswordProblem(hostPassword) is { } passwordProblem)
            return ($"Host password: {passwordProblem}", "Projects");

        settings.Ssms.RememberPassword = SsmsRememberPassword.IsChecked == true;
        settings.Appearance.Theme = ChosenTheme;
        settings.Appearance.UiScale = ChosenUiScale;
        settings.Appearance.FontSize = ChosenFontSize;
        settings.Appearance.Animations = Animations.IsChecked == true;
        settings.Window.KeepRunningWhenClosed = KeepRunningWhenClosed.IsChecked == true;
        settings.Updates.CheckAtStart = CheckForUpdatesAtStart.IsChecked == true;
        settings.Terminal = ChosenTerminal;
        settings.Backups.KeepDays = TagOf(BackupKeepDays) as int? ?? settings.Backups.KeepDays;
        if (!int.TryParse(T(DeployParallel), out var parallel) || parallel is < 1 or > 16)
            return ("Files at the same time: a number from 1 to 16.", "Deploy");
        if (IgnoreRules.Parse(DeployIgnoreRules.Text ?? "").Problems is { Count: > 0 } ruleProblems)
            return ($"Ignored files: {ruleProblems[0]}", "Deploy");
        settings.Deploy = new DeploySettings
        {
            CompareOnOpen = DeployCompareOnOpen.IsChecked == true,
            KeepVersionsDays = TagOf(DeployKeepVersions) as int? ?? settings.Deploy.KeepVersionsDays,
            Parallel = parallel,
            KeepBackups = DeployKeepBackups.IsChecked == true,
            VerifyUploads = DeployVerifyUploads.IsChecked == true,
            // As written, with Windows' line endings made plain - a rule is a line.
            IgnoreRules = (DeployIgnoreRules.Text ?? "").Replace("\r\n", "\n")
        };
        return (null, null);
    }

    private async void BrowseBaseDir_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var options = new FolderPickerOpenOptions { Title = "Projects folder" };
        if (Directory.Exists(T(BaseDirectory))) options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(T(BaseDirectory));
        var folders = await storage.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) BaseDirectory.Text = path;
    }

    private void UpdatePreview()
    {
        if (UrlPreview is null) return;
        var suffix = T(HostnameSuffix).Trim('.');
        var port = int.TryParse(T(SitePort), out var p) && p is > 0 and <= 65535 ? p : 80;
        UrlPreview.Text = $"Sites answer at http://<project>.{suffix}{(port == 80 ? "" : $":{port}")}";
        var language = TagOf(DnnLanguage) as string ?? "en-US";
        DnnDefaultsHint.Text = $"Empty e-mail: host@{suffix}. Empty password: New project asks." +
                               (language == "en-US" ? "" : $" {LanguageName(language)} needs internet while installing.");
    }

    // ─── Appearance and terminal ──────────────────────────────────────────

    private string ChosenTheme => TagOf(ThemeBox) as string ?? "system";

    private int ChosenUiScale => TagOf(UiScale) as int? ?? 100;

    private double ChosenFontSize => TagOf(AppFontSize) as double? ?? 13;

    private void ShowAppearance(AppearanceSettings appearance)
    {
        Select(ThemeBox, (appearance.Theme ?? "system").ToLowerInvariant());
        if (ThemeBox.SelectedItem is null) ThemeBox.SelectedIndex = 0;
        // A saved value that isn't in the list is added, so it stays chosen.
        if (!Select(UiScale, appearance.UiScale))
        {
            UiScale.Items.Add(new ComboBoxItem { Content = $"{appearance.UiScale} %", Tag = appearance.UiScale });
            UiScale.SelectedIndex = UiScale.Items.Count - 1;
        }
        if (!Select(AppFontSize, appearance.FontSize))
        {
            AppFontSize.Items.Add(new ComboBoxItem { Content = $"{appearance.FontSize} px", Tag = appearance.FontSize });
            AppFontSize.SelectedIndex = AppFontSize.Items.Count - 1;
        }
        Animations.IsChecked = appearance.Animations;
    }

    private void ShowTerminal(TerminalSettings settings)
    {
        // A shell that isn't installed (any more): the first one that is, as the terminal itself does.
        if (!Select(DefaultShell, settings.DefaultShell) && DefaultShell.Items.Count > 0) DefaultShell.SelectedIndex = 0;
        // A saved font or size that isn't in the list is added, so it stays chosen.
        if (!Select(TerminalFont, settings.FontFamily))
        {
            TerminalFont.Items.Add(new ComboBoxItem { Content = settings.FontFamily, Tag = settings.FontFamily });
            TerminalFont.SelectedIndex = TerminalFont.Items.Count - 1;
        }
        if (!Select(TerminalFontSize, settings.FontSize))
        {
            TerminalFontSize.Items.Add(new ComboBoxItem { Content = settings.FontSize.ToString(CultureInfo.InvariantCulture), Tag = settings.FontSize });
            TerminalFontSize.SelectedIndex = TerminalFontSize.Items.Count - 1;
        }
    }

    private static bool Select(ComboBox box, object tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag) ||
            (tag is string text && i.Tag is string other && other.Equals(text, StringComparison.OrdinalIgnoreCase)));
        return box.SelectedItem is not null;
    }

    /// <summary>The terminal's settings as the form has them.</summary>
    private TerminalSettings ChosenTerminal => new()
    {
        DefaultShell = TagOf(DefaultShell) as string ?? new TerminalSettings().DefaultShell,
        FontFamily = TagOf(TerminalFont) as string ?? "",
        FontSize = TagOf(TerminalFontSize) as int? ?? 13
    };

    /// <summary>
    /// The shells there are on this computer, by the keys terminal.defaultShell has - the Windows app's TerminalService
    /// looks for the same ones on Windows; on macOS and Linux the system's own.
    /// </summary>
    private static IEnumerable<(string Key, string Name)> TerminalShells()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ("powershell", "PowerShell");
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (File.Exists(System.IO.Path.Combine(programFiles, @"PowerShell\7\pwsh.exe")) || OnPath("pwsh.exe")) yield return ("pwsh", "PowerShell 7");
            yield return ("cmd", "Command Prompt");
            if (new[]
                {
                    System.IO.Path.Combine(programFiles, @"Git\bin\bash.exe"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Git\bin\bash.exe"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\bin\bash.exe")
                }.Any(File.Exists))
                yield return ("gitbash", "Git Bash");
            yield break;
        }
        foreach (var (key, name) in new[] { ("zsh", "zsh"), ("bash", "bash"), ("fish", "fish"), ("sh", "sh") })
            if (File.Exists($"/bin/{key}") || File.Exists($"/usr/bin/{key}") || File.Exists($"/usr/local/bin/{key}") || File.Exists($"/opt/homebrew/bin/{key}"))
                yield return (key, name);
        if (OnPath("pwsh")) yield return ("pwsh", "PowerShell 7");
    }

    private static bool OnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(folder =>
        {
            try { return System.IO.Path.IsPathFullyQualified(folder) && File.Exists(System.IO.Path.Combine(folder, exe)); }
            catch (ArgumentException) { return false; }
        });

    /// <summary>The fonts on this computer, by name.</summary>
    private static IEnumerable<string> InstalledFonts()
    {
        try
        {
            return FontManager.Current.SystemFonts.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    // ─── Database server ──────────────────────────────────────────────────

    /// <summary>"container", "sqlServer" or "localDbFile", as the connection type radios have it.</summary>
    private string ChosenServerType =>
        (new[] { ServerContainer, ServerSqlServer, ServerLocalDb }.FirstOrDefault(r => r.IsChecked == true)?.Tag as string)
        ?? SqlServerSettings.ContainerType;

    private static bool UsesLogin(SqlServerSettings sql) =>
        sql.Type.Equals("sqlServer", StringComparison.OrdinalIgnoreCase) && sql.UsesSqlAuthentication;

    /// <summary>The server's default for a connection type that has none saved.</summary>
    private static string DefaultServer(string type) => type == "localDbFile" ? DatabaseConnection.LocalDbServer : @".\SQLEXPRESS";

    /// <summary>Fills the database server fields with <paramref name="sql"/> - on loading, and after saving.</summary>
    private void ShowServer(SqlServerSettings sql)
    {
        var type = SqlServerSettings.Types.FirstOrDefault(t => t.Equals(sql.Type, StringComparison.OrdinalIgnoreCase)) ?? SqlServerSettings.ContainerType;
        ServerContainer.IsChecked = type == SqlServerSettings.ContainerType;
        ServerSqlServer.IsChecked = type == "sqlServer";
        ServerLocalDb.IsChecked = type == "localDbFile";
        ContainerIp.Text = sql.Host;
        DefaultPort.Text = sql.Port.ToString(CultureInfo.InvariantCulture);
        ContainerUser.Text = sql.ContainerUserName;
        SaPassword.Text = sql.SaPassword;
        // The saved server belongs to the type it fits; the other type starts from its default.
        var savedFor = sql.Server.Trim().StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase) ? "localDbFile" : "sqlServer";
        _serverText.Clear();
        _serverText[savedFor] = sql.Server;
        _serverType = type;
        ServerName.Text = type == SqlServerSettings.ContainerType ? sql.Server : _serverText.GetValueOrDefault(type) ?? DefaultServer(type);
        WindowsAuth.IsChecked = !sql.UsesSqlAuthentication;
        SqlAuth.IsChecked = sql.UsesSqlAuthentication;
        ServerUserName.Text = sql.UserName;
        ServerPassword.Text = _savedServerPassword;
        ShowServerFields();
    }

    /// <summary>
    /// The chosen connection type and only its fields, for telling whether anything changed - trying another type and
    /// going back leaves nothing to save.
    /// </summary>
    private string ServerSnapshot()
    {
        var type = ChosenServerType;
        return type switch
        {
            "sqlServer" => string.Join('\u001e', type, T(ServerName), SqlAuth.IsChecked == true,
                SqlAuth.IsChecked == true ? T(ServerUserName) : "", SqlAuth.IsChecked == true ? ServerPassword.Text ?? "" : ""),
            "localDbFile" => string.Join('\u001e', type, T(ServerName)),
            _ => string.Join('\u001e', type, T(ContainerIp), T(DefaultPort), T(ContainerUser), SaPassword.Text ?? "")
        };
    }

    private void ServerType_Checked()
    {
        if (_loading) return;
        var type = ChosenServerType;
        if (type == _serverType) return;
        // SQL Server and LocalDB each keep the server typed for them.
        _loading = true;
        if (_serverType != SqlServerSettings.ContainerType) _serverText[_serverType] = ServerName.Text ?? "";
        if (type != SqlServerSettings.ContainerType) ServerName.Text = _serverText.GetValueOrDefault(type) ?? DefaultServer(type);
        _loading = false;
        _serverType = type;
        ShowServerFields();
        Edited();
    }

    /// <summary>The fields the chosen connection type has: the container's address and SA password, or the server and its login.</summary>
    private void ShowServerFields()
    {
        var type = ChosenServerType;
        var sqlServer = type == "sqlServer";
        ContainerFields.IsVisible = type == SqlServerSettings.ContainerType;
        ServerFields.IsVisible = type != SqlServerSettings.ContainerType;
        AuthFields.IsVisible = sqlServer;
        LoginFields.IsVisible = sqlServer && SqlAuth.IsChecked == true;
        ServerGroupLabel.Text = sqlServer ? "SQL Server" : "LocalDB";
        ServerLabel.Text = sqlServer ? "Server" : "LocalDB instance";
        ServerHint.Text = sqlServer
            ? @"The instance, e.g. .\SQLEXPRESS, localhost or localhost,1433."
            : $"{DatabaseConnection.LocalDbServer} is the instance every Windows user has. Each site runs its database in the LocalDB of its app pool identity.";
        AuthHint.Text = SqlAuth.IsChecked == true
            ? OperatingSystem.IsWindows()
                ? "The login must be able to create a database, or own it when it exists. Its password is kept in the Windows Credential Manager of your account, not with the other settings."
                : "The login must be able to create a database, or own it when it exists. Its password is kept encrypted for your account only, not with the other settings."
            : @"Each site signs in as its app pool's identity (IIS APPPOOL\<project>) - DNN Manager creates that login with your Windows account and makes it the database's owner.";
    }

    // ─── Keep warm, backups, Source Control ───────────────────────────────

    private static ComboBoxItem KeepWarmIntervalItem(int minutes) => new()
    {
        Content = KeepWarmRules.Span(TimeSpan.FromMinutes(minutes)) + (minutes == new KeepWarmSettings().PingMinutes ? " (default)" : ""),
        Tag = minutes
    };

    private void ShowKeepWarm(KeepWarmSettings keepWarm)
    {
        // A saved value the list doesn't have is offered too.
        if (KeepWarmInterval.Items.OfType<ComboBoxItem>().All(i => i.Tag as int? != keepWarm.PingMinutes))
        {
            var index = KeepWarmInterval.Items.OfType<ComboBoxItem>().TakeWhile(i => (int)i.Tag! < keepWarm.PingMinutes).Count();
            KeepWarmInterval.Items.Insert(index, KeepWarmIntervalItem(keepWarm.PingMinutes));
        }
        KeepWarmInterval.SelectedItem = KeepWarmInterval.Items.OfType<ComboBoxItem>().First(i => i.Tag as int? == keepWarm.PingMinutes);
        KeepWarmPingPath.Text = keepWarm.PingPath;
        KeepWarmWarmUpPath.Text = keepWarm.WarmUpPath;
    }

    private void ShowDeploy(DeploySettings deploy)
    {
        DeployCompareOnOpen.IsChecked = deploy.CompareOnOpen;
        // A number of days saved by hand that isn't one of the choices is offered too, so it isn't lost by opening Settings.
        if (DeployKeepVersions.Items.OfType<ComboBoxItem>().All(i => i.Tag as int? != deploy.KeepVersionsDays))
            DeployKeepVersions.Items.Insert(0, new ComboBoxItem { Content = $"{deploy.KeepVersionsDays} days", Tag = deploy.KeepVersionsDays });
        DeployKeepVersions.SelectedItem = DeployKeepVersions.Items.OfType<ComboBoxItem>().First(i => i.Tag as int? == deploy.KeepVersionsDays);
        DeployParallel.Text = deploy.Parallel.ToString(CultureInfo.InvariantCulture);
        DeployKeepBackups.IsChecked = deploy.KeepBackups;
        DeployVerifyUploads.IsChecked = deploy.VerifyUploads;
        DeployIgnoreRules.Text = deploy.IgnoreRules.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private void DeployRulesDefaults_Click(object? sender, RoutedEventArgs e) =>
        DeployIgnoreRules.Text = IgnoreRules.Defaults.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

    /// <summary>A line that isn't a pattern, said under the rules as they are typed.</summary>
    private void ShowRulesProblems()
    {
        var problems = IgnoreRules.Parse(DeployIgnoreRules.Text ?? "").Problems;
        DeployRulesProblem.Text = string.Join(Environment.NewLine, problems);
        DeployRulesProblem.IsVisible = problems.Count > 0;
    }

    private static ComboBoxItem BackupKeepDaysItem(int days) => new()
    {
        Content = days switch
        {
            0 => "Keep them for good (default)",
            7 => "Delete them after a week",
            30 => "Delete them after a month",
            365 => "Delete them after a year",
            _ when days % 30 == 0 => $"Delete them after {days / 30} months",
            _ => $"Delete them after {days} days"
        },
        Tag = days
    };

    private void ShowBackupKeepDays(int days)
    {
        // A saved value the list doesn't have is offered too.
        if (BackupKeepDays.Items.OfType<ComboBoxItem>().All(i => i.Tag as int? != days))
        {
            var index = BackupKeepDays.Items.OfType<ComboBoxItem>().TakeWhile(i => (int)i.Tag! < days).Count();
            BackupKeepDays.Items.Insert(index, BackupKeepDaysItem(days));
        }
        BackupKeepDays.SelectedItem = BackupKeepDays.Items.OfType<ComboBoxItem>().First(i => i.Tag as int? == days);
    }

    // ─── Saying things ────────────────────────────────────────────────────

    /// <summary>"Deutsch (de-DE)" - the language's own name and its code; the code alone when the system doesn't know it.</summary>
    private static string LanguageName(string culture)
    {
        try
        {
            var info = CultureInfo.GetCultureInfo(culture);
            return info.NativeName.Length == 0 ? culture : $"{char.ToUpper(info.NativeName[0], CultureInfo.InvariantCulture)}{info.NativeName[1..]} ({culture})";
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }

    private void ShowError(string? error)
    {
        ErrorText.Text = error ?? "";
        ErrorText.IsVisible = error is not null;
    }

    private static void Say(string text, ToastKind kind = ToastKind.Warning) => Toast.Show(text, kind);

    // DNNMANAGER_* environment variables are applied on top of the saved settings, so they win over what's saved here.
    // DNNMANAGER_DATA_DIR (where the data is) and DNNMANAGER_NO_ELEVATION aren't settings.
    private void ShowEnvironmentOverrides()
    {
        var overrides = Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(k => k.StartsWith(LiveSettings.EnvironmentPrefix, StringComparison.OrdinalIgnoreCase) &&
                        !k.Equals("DNNMANAGER_DATA_DIR", StringComparison.OrdinalIgnoreCase) &&
                        !k.Equals("DNNMANAGER_NO_ELEVATION", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (overrides.Count == 0) return;
        EnvWarningText.Text = "These environment variables override the saved settings: " + string.Join(", ", overrides);
        EnvWarning.IsVisible = true;
    }
}
