using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.KeepWarm;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Startup;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// The settings, opened over the page (MainWindow.ShowModal): categories (with a search box) on the left, the chosen
/// one on the right. Every change - in any category - makes <b>Save</b> ready; Save checks the values, saves
/// them and puts them to work at once (<see cref="LiveSettings"/>, the UI scale, the terminal, the start at sign-in) -
/// no restart. <b>Discard changes</b> puts the saved values back; <b>Close</b> goes back to the page under
/// Settings.
/// </summary>
public partial class SettingsPage : UserControl
{
    // Form labels for the settings keys, so a problem reads "Site port must be…" rather than "projects.sitePort must be…".
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["appearance.uiScale"] = "UI scale",
        ["appearance.fontSize"] = "Font size",
        ["projects.baseDirectory"] = "Projects folder",
        ["projects.sitePort"] = "Site port",
        ["projects.hostnameSuffix"] = "Hostname suffix",
        ["projects.dnnReleaseSources"] = "DNN repositories",
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
    };

    private readonly AppOptions _options;
    private readonly OperationRunner _runner;
    private readonly SettingsStore _store;
    private readonly LiveSettings _live;
    private readonly TerminalService _terminal;
    private readonly StartupTask _startup;
    private readonly ISecretStore _secrets;
    // The default host password and the database server login's password as they are in the Credential Manager -
    // compared with the form to see what Save has to change.
    private string _savedHostPassword = "";
    private string _savedServerPassword = "";
    private string _savedSaPassword = "";
    // The connection type the database server fields show, and the server typed for each type - switching to LocalDB
    // and back gives the SQL Server its own server again.
    private string _serverType = SqlServerSettings.ContainerType;
    private readonly Dictionary<string, string> _serverText = new(StringComparer.OrdinalIgnoreCase);
    // Each category (the Tag of its entry in the list): its panel, and the words the search box finds it by.
    private readonly Dictionary<string, (FrameworkElement Panel, string Keywords)> _categories;
    // Set while the form is being filled in, so that doesn't count as an edit.
    private bool _loading;
    // The form has edits that aren't saved yet.
    private bool _dirty;
    // Whether DNN Manager starts at sign-in now (the scheduled task is there); null until that has been read.
    private bool? _startsAtSignIn;
    // The form as it was filled in from the saved settings - what an edit is compared with.
    private string _savedForm = "";
    private readonly IServiceProvider _services;

    public SettingsPage(IOptions<AppOptions> options, OperationRunner runner, SettingsStore store, LiveSettings live,
        AppDataPaths paths, TerminalService terminal, StartupTask startup, ISecretStore secrets, IServiceProvider services)
    {
        _options = options.Value;
        _runner = runner;
        _store = store;
        _live = live;
        _terminal = terminal;
        _startup = startup;
        _secrets = secrets;
        _services = services;
        InitializeComponent();
        // Focused when shown, so Esc reaches it - the title bar's button that opened it doesn't take the focus.
        Focusable = true;
        FocusVisualStyle = null;
        Loaded += (_, _) => Focus();

        _categories = new Dictionary<string, (FrameworkElement, string)>
        {
            ["General"] = (GeneralPanel, "general start sign in startup close closing quit exit background tray notification area keep running appearance scale zoom ui font text size bigger smaller terminal shell powershell command prompt git bash font family size"),
            ["Projects"] = (ProjectsPanel, "projects folder base directory hostname suffix site port address url dnn defaults install installation automatic manual setup wizard host account username password e-mail email website name language culture template keep warm alive keepalive warm-up idle time-out timeout interval ping cold start slow fast recycle"),
            ["Releases"] = (ReleasesPanel, "dnn releases repositories github versions install packages keep download"),
            ["Sql"] = (SqlPanel, "database server sql server express localdb file connection type local container docker host port user username sa password windows authentication login username ssms management studio remember test connection"),
            ["Docker"] = (DockerPanel, "docker container name volume edition mssql_pid collation desktop engine install start set up docker-compose compose yml test"),
            ["Iis"] = (IisPanel, "iis windows features test set up enable " + string.Join(' ', _options.RequiredIisFeatures.Select(f => $"{f.Label} {f.Name}"))),
            ["Keyboard"] = (KeyboardPanel, "keyboard shortcuts shortcut keys key bindings keybindings hotkeys accelerators command palette " +
                                           string.Join(' ', services.GetRequiredService<AppCommands>().All.Select(c => $"{c.Title} {c.Area}"))),
            ["About"] = (AboutPanel, "about version build commit release channel update upgrade install new latest license mit repository github documentation docs runtime .net framework windows architecture administrator iis docker components libraries your files folders settings backups logs packages"),
        };

        KeepDnnPackagesHint.Text = $"Saved in {paths.PackagesDirectory} and used again when a new project picks the same " +
                                   "version - no download. Off: each new project downloads its package and deletes it after installing.";
        // Test and set up what the settings describe - working with the saved settings.
        DockerCard.Attach(services);
        SqlCard.Attach(services);
        IisCard.Attach(services);
        DockerCard.ContainerChanged += (_, _) => SqlCard.Test();
        Folders.ItemsSource = new KeyValuePair<string, string>[]
        {
            new("Settings", paths.Root),
            new("Backups", paths.BackupsDirectory),
            new("Deployments", paths.DeploymentsDirectory),
            new("Logs", paths.LogsDirectory),
            new("DNN packages", paths.PackagesDirectory),
        };

        foreach (var box in new[] { BaseDirectory, SitePort, HostnameSuffix, ReleaseApis, ContainerName, ContainerIp, ContainerUser,
                                    VolumeName, DefaultPort, Collation, MssqlPid, HostUsername, HostEmail, WebsiteName,
                                    ServerName, ServerUserName, KeepWarmPingPath, KeepWarmWarmUpPath })
            box.TextChanged += (_, _) => Edited();
        SaPassword.PasswordChanged += (_, _) => Edited();
        HostPassword.PasswordChanged += (_, _) => Edited();
        ServerPassword.PasswordChanged += (_, _) => Edited();
        foreach (var language in DnnAccountRules.Languages) DnnLanguage.Items.Add(new ComboBoxItem { Content = Languages.Name(language), Tag = language });
        foreach (var template in DnnAccountRules.Templates) DnnTemplate.Items.Add(new ComboBoxItem { Content = template, Tag = template });
        foreach (var minutes in KeepWarmSettings.PingIntervals) KeepWarmInterval.Items.Add(KeepWarmIntervalItem(minutes));
        foreach (var box in new[] { SsmsRememberPassword, KeepDnnPackages, KeepRunningWhenClosed })
        {
            box.Checked += (_, _) => Edited();
            box.Unchecked += (_, _) => Edited();
        }


        foreach (var scale in AppearanceSettings.UiScales)
            UiScale.Items.Add(new ComboBoxItem { Content = scale == 100 ? "100 % (default)" : $"{scale} %", Tag = scale });
        foreach (var size in AppearanceSettings.FontSizes)
            AppFontSize.Items.Add(new ComboBoxItem { Content = size == 13 ? "13 px (default)" : $"{size} px", Tag = (double)size });
        foreach (var shell in terminal.Shells) DefaultShell.Items.Add(new ComboBoxItem { Content = shell.Name, Tag = shell.Key });
        TerminalFont.Items.Add(new ComboBoxItem { Content = "Default", Tag = "" });
        foreach (var font in terminal.InstalledFonts) TerminalFont.Items.Add(new ComboBoxItem { Content = font, Tag = font });
        foreach (var size in Enumerable.Range(9, 12).Concat([22, 24, 28])) TerminalFontSize.Items.Add(new ComboBoxItem { Content = size.ToString(), Tag = size });

        ShowEnvironmentOverrides();
        Load();
        ShowStartAtSignIn();
        Categories.SelectedIndex = 0;
    }

    /// <summary>Close was pressed - the window goes back to the page Settings was opened from.</summary>
    public event EventHandler? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // Esc: the search is emptied first; then back to the page Settings was opened from (asking about unsaved changes).
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != System.Windows.Input.Key.Escape) return;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        else CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // ─── Categories ───────────────────────────────────────────────────────

    private void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = (Categories.SelectedItem as ListBoxItem)?.Tag as string;
        foreach (var (key, category) in _categories)
            category.Panel.Visibility = key == selected ? Visibility.Visible : Visibility.Collapsed;
        if (selected == "About") ShowAbout();
        if (selected == "Keyboard") ShowShortcuts();
    }

    // Asked once while the page is open: Docker's version. The update status is the updater's, and follows it.
    private string? _docker;
    private bool _askingAbout, _followingUpdates;

    /// <summary>Shows About - then, the first time, asks Docker (and GitHub, unless it was just asked) and shows it again.</summary>
    private async void ShowAbout()
    {
        var updater = _services.GetRequiredService<AppUpdater>();
        if (!_followingUpdates)
        {
            _followingUpdates = true;
            // Checking, downloading, installing: the Update row says so as it happens - while the page is open.
            PropertyChangedEventHandler follow = (_, _) => { if (AboutPanel.Visibility == Visibility.Visible) ShowAboutInfo(); };
            updater.PropertyChanged += follow;
            Unloaded += (_, _) => updater.PropertyChanged -= follow;
        }
        ShowAboutInfo();
        _ = updater.CheckAsync(unlessWithin: TimeSpan.FromMinutes(10));
        if (_askingAbout || _docker is not null) return;
        _askingAbout = true;
        _docker = await AboutInfo.DockerAsync(_services.GetRequiredService<IPrerequisiteChecker>(), _options.Docker.ContainerName);
        _askingAbout = false;
        ShowAboutInfo();
    }

    private void ShowAboutInfo() => AboutInfoPanel.Show(AboutInfo.Sections(_services.GetRequiredService<AppUpdater>(), _docker));

    /// <summary>The category shown: General, Projects, Releases, Sql, Docker, Iis or About.</summary>
    public string? Category => (Categories.SelectedItem as ListBoxItem)?.Tag as string;

    /// <summary>The keyboard in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>The next category shown (<paramref name="by"/> 1) or the one before (-1), round the end - Ctrl+PageUp / Ctrl+PageDown.</summary>
    public void StepCategory(int by)
    {
        var shown = Categories.Items.OfType<ListBoxItem>().Where(i => i.Visibility == Visibility.Visible).ToList();
        if (shown.Count == 0) return;
        var index = shown.IndexOf(Categories.SelectedItem as ListBoxItem ?? shown[0]);
        Categories.SelectedItem = shown[((index + by) % shown.Count + shown.Count) % shown.Count];
    }

    public void ShowCategory(string key)
    {
        if (Categories.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == key) is not { } item) return;
        // Left out by the search: the search goes, so the category is in the list again.
        if (item.Visibility != Visibility.Visible) SearchBox.Text = "";
        Categories.SelectedItem = item;
    }

    // Leaves the categories that have a setting matching every word typed, and opens the first of them.
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_categories is null) return; // raised during InitializeComponent
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in Categories.Items.OfType<ListBoxItem>())
        {
            var keywords = _categories[(string)item.Tag].Keywords;
            item.Visibility = words.All(w => keywords.Contains(w, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
        }

        var shown = Categories.Items.OfType<ListBoxItem>().Where(i => i.Visibility == Visibility.Visible).ToList();
        NoMatch.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown.Count > 0 && !shown.Contains(Categories.SelectedItem)) Categories.SelectedItem = shown[0];
    }

    // ─── UI scale and font size ───────────────────────────────────────────

    private int ChosenUiScale => (UiScale.SelectedItem as ComboBoxItem)?.Tag as int? ?? 100;

    private double ChosenFontSize => (AppFontSize.SelectedItem as ComboBoxItem)?.Tag as double? ?? ThemeManager.DefaultFontSize;

    private void ShowAppearance(AppearanceSettings appearance)
    {
        _loading = true;
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
        _loading = false;
    }

    private void Appearance_Changed(object sender, SelectionChangedEventArgs e) => Edited();

    // ─── Terminal ─────────────────────────────────────────────────────────

    private void ShowTerminal(TerminalSettings settings)
    {
        _loading = true;
        // A shell that isn't installed (any more): the first one that is, as the terminal itself does.
        if (!Select(DefaultShell, settings.DefaultShell)) Select(DefaultShell, _terminal.Shells[0].Key);
        // A saved font or size that isn't in the list is added, so it stays chosen.
        if (!Select(TerminalFont, settings.FontFamily))
        {
            TerminalFont.Items.Add(new ComboBoxItem { Content = settings.FontFamily, Tag = settings.FontFamily });
            TerminalFont.SelectedIndex = TerminalFont.Items.Count - 1;
        }
        if (!Select(TerminalFontSize, settings.FontSize))
        {
            TerminalFontSize.Items.Add(new ComboBoxItem { Content = settings.FontSize.ToString(), Tag = settings.FontSize });
            TerminalFontSize.SelectedIndex = TerminalFontSize.Items.Count - 1;
        }
        _loading = false;
    }

    private static bool Select(ComboBox box, object tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag) ||
            (tag is string text && i.Tag is string other && other.Equals(text, StringComparison.OrdinalIgnoreCase)));
        return box.SelectedItem is not null;
    }

    private void Terminal_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Edited();
    }

    /// <summary>The terminal's settings as the form has them.</summary>
    private TerminalSettings ChosenTerminal => new()
    {
        DefaultShell = (DefaultShell.SelectedItem as ComboBoxItem)?.Tag as string ?? _terminal.Settings.DefaultShell,
        FontFamily = (TerminalFont.SelectedItem as ComboBoxItem)?.Tag as string ?? _terminal.Settings.FontFamily,
        FontSize = (TerminalFontSize.SelectedItem as ComboBoxItem)?.Tag as int? ?? _terminal.Settings.FontSize
    };

    // ─── Start at sign-in ─────────────────────────────────────────────────

    /// <summary>Whether the scheduled task is there - and which exe it starts, when that isn't this one.</summary>
    private async void ShowStartAtSignIn()
    {
        try
        {
            var target = await _startup.GetTargetAsync();
            _startsAtSignIn = target is not null;
            StartAtSignIn.IsChecked = target is not null;
            StartAtSignIn.IsEnabled = true;
            Edited();
            var other = target is { Length: > 0 } && Environment.ProcessPath is { } exe &&
                        !string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
            if (other)
                StartAtSignInHint.Text = $"Set up to start {target} - untick and tick it again to start this copy ({Environment.ProcessPath}) instead.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            StartAtSignInHint.Text = $"Could not read the scheduled task: {ex.Message}";
        }
    }

    private void StartAtSignIn_Click(object sender, RoutedEventArgs e) => Edited();

    /// <summary>
    /// Makes or removes the scheduled task when the box was changed - part of Save. False (the box is put back, with
    /// the reason in a toast) when Windows refused.
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
                if (enable) StartAtSignInHint.Text = "With its Administrator rights, without Windows asking for them at every sign-in (a scheduled task).";
                return true;
            }
            StartAtSignIn.IsChecked = !enable;
            Toast.Show($"The other settings are saved, but the scheduled task could not be {(enable ? "set up" : "removed")}: {result.Error}", ToastKind.Error);
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

        _savedHostPassword = _secrets.Read(SecretNames.DefaultHostPassword) ?? DnnDefaultsSettings.DefaultHostPassword;
        _savedServerPassword = _secrets.Read(SecretNames.DatabaseServerPassword) ?? "";
        _savedSaPassword = saved.SqlServer.SaPassword;
        ShowSettings(saved, _savedHostPassword, _savedServerPassword, _startsAtSignIn);

        _savedForm = FormSnapshot();
        ShowError(null);
        SetDirty(false);
    }

    /// <summary>
    /// Fills the form with <paramref name="settings"/> and the passwords that go with them; the start at sign-in box
    /// with <paramref name="startAtSignIn"/> unless that isn't known yet.
    /// </summary>
    private void ShowSettings(UserSettings settings, string hostPassword, string serverPassword, bool? startAtSignIn)
    {
        var saved = settings;
        ShowAppearance(saved.Appearance);
        ShowTerminal(saved.Terminal);
        _loading = true;
        if (startAtSignIn is { } starts) StartAtSignIn.IsChecked = starts;
        var p = saved.Projects;
        var sql = saved.SqlServer;
        var docker = saved.Docker;
        BaseDirectory.Text = p.BaseDirectory;
        SitePort.Text = p.SitePort.ToString();
        HostnameSuffix.Text = p.HostnameSuffix;
        ReleaseApis.Text = string.Join(Environment.NewLine, p.DnnReleaseSources);
        KeepDnnPackages.IsChecked = p.KeepDnnPackages;
        ContainerName.Text = docker.ContainerName;
        VolumeName.Text = docker.VolumeName;
        MssqlPid.Text = docker.Edition;
        Collation.Text = docker.Collation;
        SsmsRememberPassword.IsChecked = saved.Ssms.RememberPassword;
        KeepRunningWhenClosed.IsChecked = saved.Window.KeepRunningWhenClosed;
        var dnn = p.DnnDefaults;
        InstallAutomatic.IsChecked = dnn.Automatic;
        InstallManual.IsChecked = !dnn.Automatic;
        HostUsername.Text = dnn.HostUsername;
        HostEmail.Text = dnn.HostEmail;
        WebsiteName.Text = dnn.WebsiteName;
        Select(DnnLanguage, dnn.Language);
        Select(DnnTemplate, dnn.Template);
        ShowKeepWarm(p.KeepWarm);
        HostPassword.Password = hostPassword;
        ShowServer(sql);
        ServerPassword.Password = serverPassword;
        _loading = false;
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

    /// <summary>Every value on the form as it would be saved, in one string - to tell whether anything differs.</summary>
    private string FormSnapshot()
    {
        var terminal = ChosenTerminal;
        return string.Join('\u001f',
            BaseDirectory.Text.Trim(), SitePort.Text.Trim(), HostnameSuffix.Text.Trim().Trim('.'),
            string.Join('\n', ReleaseApis.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            KeepDnnPackages.IsChecked == true, ServerSnapshot(),
            ContainerName.Text.Trim(), VolumeName.Text.Trim(), MssqlPid.Text.Trim(), Collation.Text.Trim(),
            SsmsRememberPassword.IsChecked == true, ChosenUiScale, ChosenFontSize,
            KeepRunningWhenClosed.IsChecked == true, terminal.DefaultShell, terminal.FontFamily, terminal.FontSize,
            InstallAutomatic.IsChecked == true, HostUsername.Text.Trim(), HostPassword.Password, HostEmail.Text.Trim(), WebsiteName.Text.Trim(),
            (DnnLanguage.SelectedItem as ComboBoxItem)?.Tag, (DnnTemplate.SelectedItem as ComboBoxItem)?.Tag,
            (KeepWarmInterval.SelectedItem as ComboBoxItem)?.Tag, KeepWarmSettings.NormalizePath(KeepWarmPingPath.Text),
            KeepWarmSettings.NormalizePath(KeepWarmWarmUpPath.Text));
    }

    /// <summary>The form has edits that aren't saved - leaving the page would lose them.</summary>
    public bool HasUnsavedChanges => _dirty;

    /// <summary>A password on the form was changed and not saved - the one unsaved edit that isn't kept for the next start.</summary>
    public bool HasUnsavedPassword =>
        _dirty && (HostPassword.Password != _savedHostPassword || ServerPassword.Password != _savedServerPassword ||
                   SaPassword.Password != _savedSaPassword);

    /// <summary>The unsaved edits, by field - kept when DNN Manager restarts. Null when there are none. Never a password.</summary>
    public Dictionary<string, string>? CaptureDraft() => _dirty && Form.IsEnabled ? FormDraft.Capture(Form) : null;

    /// <summary>Puts unsaved edits back over the saved settings - they are unsaved again, for Save or Discard.</summary>
    public void RestoreDraft(IReadOnlyDictionary<string, string> draft)
    {
        if (!Form.IsEnabled) return;
        FormDraft.Restore(Form, draft);
        Edited();
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        // Save and Discard come to life with the first edit, until it's saved or discarded.
        SaveButton.IsEnabled = dirty;
        DiscardButton.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = dirty ? "You have unsaved changes." : "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "LogWarn");
        // The Docker and database server cards work with the saved settings - not with edits that aren't saved yet.
        foreach (var (card, hint) in new (FrameworkElement, FrameworkElement)[] { (DockerCard, DockerSaveFirst), (SqlCard, SqlSaveFirst) })
        {
            card.IsEnabled = !dirty;
            hint.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private void Discard_Click(object sender, RoutedEventArgs e) => Load();

    /// <summary>Checks the form, saves it and puts the new settings to work - from the next thing done, no restart.</summary>
    private async Task SaveAsync()
    {
        // Not under a running operation: it would go on with half the old settings and half the new.
        if (_runner.IsBusy)
        {
            Toast.Show($"'{_runner.Current}' is still running - save once it has finished.", ToastKind.Warning);
            return;
        }

        UserSettings settings;
        try
        {
            // Start from the file, so keys this page doesn't edit (IIS features, table columns…) stay as they are.
            settings = _store.Read();
        }
        catch (SettingsException ex)
        {
            ShowError($"Not saved - {ex.Message}");
            Toast.Show($"Settings not saved - {ex.Message}", ToastKind.Error);
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
            SearchBox.Clear();
            if (category is not null) ShowCategory(category);
            ShowError(error);
            Toast.Show($"Not saved: {error}", ToastKind.Warning);
            return;
        }

        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not save the settings to {_store.Location}: {ex.Message}");
            Toast.Show($"Could not save the settings: {ex.Message}", ToastKind.Error);
            return;
        }

        _live.Apply(settings);
        _savedSaPassword = settings.SqlServer.SaPassword;
        ThemeManager.ApplyLayout(settings.Appearance.UiScale, settings.Appearance.FontSize);
        _terminal.Apply(settings.Terminal);
        SaveSecrets(settings);
        // What another connection type's fields still show wasn't saved - show what is.
        _loading = true;
        ShowServer(settings.SqlServer);
        _loading = false;
        _savedForm = FormSnapshot();
        ShowError(null);
        SetDirty(false);
        // No toast when it worked - the Save button greying out says so; what failed says it itself.
        await ApplyStartAtSignInAsync();
    }

    /// <summary>
    /// The secrets that go with the saved settings, in the Windows Credential Manager: the default host password, and the
    /// database server login's password when SQL Server authentication is what is saved. False (with a toast) when
    /// Windows refused.
    /// </summary>
    private bool SaveSecrets(UserSettings settings)
    {
        var password = HostPassword.Password;
        if (password != _savedHostPassword)
        {
            var stored = password.Length > 0
                ? _secrets.Write(SecretNames.DefaultHostPassword, password)
                : _secrets.Delete(SecretNames.DefaultHostPassword);
            if (!stored.Success)
            {
                Toast.Show($"The other settings are saved, but the host password isn't: {stored.Error}", ToastKind.Error);
                return false;
            }
            _savedHostPassword = password;
        }
        var serverPassword = ServerPassword.Password;
        if (UsesLogin(settings.SqlServer) && serverPassword != _savedServerPassword)
        {
            var stored = serverPassword.Length > 0
                ? _secrets.Write(SecretNames.DatabaseServerPassword, serverPassword)
                : _secrets.Delete(SecretNames.DatabaseServerPassword);
            if (!stored.Success)
            {
                Toast.Show($"The other settings are saved, but the database server's password isn't: {stored.Error}", ToastKind.Error);
                return false;
            }
            _savedServerPassword = serverPassword;
        }
        return true;
    }

    /// <summary>The category that edits the settings key <paramref name="key"/>, e.g. <c>sqlServer.port</c>.</summary>
    private static string? CategoryOf(string key) => key switch
    {
        "projects.dnnReleaseSources" => "Releases",
        _ when key.StartsWith("terminal.", StringComparison.Ordinal) || key.StartsWith("appearance.", StringComparison.Ordinal) ||
               key.StartsWith("window.", StringComparison.Ordinal) => "General",
        _ when key.StartsWith("projects.", StringComparison.Ordinal) => "Projects",
        _ when key.StartsWith("sqlServer.", StringComparison.Ordinal) => "Sql",
        _ when key.StartsWith("docker.", StringComparison.Ordinal) => "Docker",
        _ when key.StartsWith("iis.", StringComparison.Ordinal) => "Iis",
        _ => null
    };

    /// <summary>
    /// Copies the form into <paramref name="settings"/>, or returns the first value that isn't a number where one is
    /// needed, with the category it is in.
    /// </summary>
    private (string? Error, string? Category) ApplyForm(UserSettings settings)
    {
        if (!int.TryParse(SitePort.Text.Trim(), out var sitePort))
            return ("Site port must be a number between 1 and 65535.", "Projects");
        var serverType = ChosenServerType;
        var sqlPort = 0;
        if (serverType == SqlServerSettings.ContainerType && !int.TryParse(DefaultPort.Text.Trim(), out sqlPort))
            return ("Port must be a number between 1 and 65535.", "Sql");

        var p = settings.Projects;
        p.BaseDirectory = BaseDirectory.Text.Trim();
        p.SitePort = sitePort;
        p.HostnameSuffix = HostnameSuffix.Text.Trim().Trim('.');
        p.DnnReleaseSources = ReleaseApis.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        p.KeepDnnPackages = KeepDnnPackages.IsChecked == true;
        p.KeepWarm = new KeepWarmSettings
        {
            PingMinutes = (KeepWarmInterval.SelectedItem as ComboBoxItem)?.Tag as int? ?? p.KeepWarm.PingMinutes,
            PingPath = KeepWarmSettings.NormalizePath(KeepWarmPingPath.Text),
            WarmUpPath = KeepWarmSettings.NormalizePath(KeepWarmWarmUpPath.Text)
        };

        // Only the chosen connection type's fields - the others keep what is saved.
        var sql = settings.SqlServer;
        sql.Type = serverType;
        if (serverType == SqlServerSettings.ContainerType)
        {
            sql.Host = ContainerIp.Text.Trim();
            sql.Port = sqlPort;
            // The container's login is the same setting as the SQL Server login (sqlServer.userName); empty is sa.
            sql.UserName = ContainerUser.Text.Trim();
            sql.SaPassword = SaPassword.Password;
        }
        else
        {
            sql.Server = ServerName.Text.Trim();
        }
        if (serverType == "sqlServer")
        {
            sql.Authentication = SqlAuth.IsChecked == true ? "sql" : "windows";
            if (SqlAuth.IsChecked == true) sql.UserName = ServerUserName.Text.Trim();
        }

        var docker = settings.Docker;
        docker.ContainerName = ContainerName.Text.Trim();
        docker.VolumeName = VolumeName.Text.Trim();
        docker.Edition = MssqlPid.Text.Trim();
        docker.Collation = Collation.Text.Trim();

        p.DnnDefaults = new DnnDefaultsSettings
        {
            InstallMode = InstallAutomatic.IsChecked == true ? "automatic" : "manual",
            HostUsername = HostUsername.Text.Trim(),
            HostEmail = HostEmail.Text.Trim(),
            WebsiteName = WebsiteName.Text.Trim(),
            Language = (DnnLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "en-US",
            Template = (DnnTemplate.SelectedItem as ComboBoxItem)?.Tag as string ?? DnnAccountRules.Templates[0]
        };
        if (HostPassword.Password.Length > 0 && DnnAccountRules.PasswordProblem(HostPassword.Password) is { } passwordProblem)
            return ($"Host password: {passwordProblem}", "Projects");

        settings.Ssms.RememberPassword = SsmsRememberPassword.IsChecked == true;
        settings.Appearance.UiScale = ChosenUiScale;
        settings.Appearance.FontSize = ChosenFontSize;
        settings.Window.KeepRunningWhenClosed = KeepRunningWhenClosed.IsChecked == true;
        settings.Terminal = ChosenTerminal;
        return (null, null);
    }


    private void BrowseBaseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Projects folder" };
        if (Directory.Exists(BaseDirectory.Text)) dialog.InitialDirectory = BaseDirectory.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) BaseDirectory.Text = dialog.FolderName;
    }

    private void OpenListedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string folder }) OpenFolder(folder);
    }

    /// <summary>Opens one of the folders with your files - made first when nothing has needed it yet (or Clean up emptied it).</summary>
    private static void OpenFolder(string folder)
    {
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast.Show($"{folder} couldn't be made: {ex.Message}", ToastKind.Error);
            return;
        }
        Shell.Open(folder);
    }

    private void Preview_Changed(object sender, TextChangedEventArgs e)
    {
        if (UrlPreview is null) return; // raised during InitializeComponent
        var suffix = HostnameSuffix.Text.Trim().Trim('.');
        var port = int.TryParse(SitePort.Text.Trim(), out var p) && p is > 0 and <= 65535 ? p : 80;
        UrlPreview.Text = $"Sites answer at http://<project>.{suffix}{(port == 80 ? "" : $":{port}")}";
        ShowDefaultsHint();
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
        DefaultPort.Text = sql.Port.ToString();
        ContainerUser.Text = sql.ContainerUserName;
        SaPassword.Password = sql.SaPassword;
        // The saved server belongs to the type it fits; the other type starts from its default.
        var savedFor = sql.Server.Trim().StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase) ? "localDbFile" : "sqlServer";
        _serverText.Clear();
        _serverText[savedFor] = sql.Server;
        _serverType = type;
        ServerName.Text = type == SqlServerSettings.ContainerType ? sql.Server : _serverText.GetValueOrDefault(type) ?? DefaultServer(type);
        WindowsAuth.IsChecked = !sql.UsesSqlAuthentication;
        SqlAuth.IsChecked = sql.UsesSqlAuthentication;
        ServerUserName.Text = sql.UserName;
        ServerPassword.Password = _savedServerPassword;
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
            "sqlServer" => string.Join('\u001e', type, ServerName.Text.Trim(), SqlAuth.IsChecked == true,
                SqlAuth.IsChecked == true ? ServerUserName.Text.Trim() : "", SqlAuth.IsChecked == true ? ServerPassword.Password : ""),
            "localDbFile" => string.Join('\u001e', type, ServerName.Text.Trim()),
            _ => string.Join('\u001e', type, ContainerIp.Text.Trim(), DefaultPort.Text.Trim(), ContainerUser.Text.Trim(), SaPassword.Password)
        };
    }

    private void ServerType_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || SqlPanel is null) return; // raised during InitializeComponent
        var type = ChosenServerType;
        if (type == _serverType) return;
        // SQL Server and LocalDB each keep the server typed for them.
        _loading = true;
        if (_serverType != SqlServerSettings.ContainerType) _serverText[_serverType] = ServerName.Text;
        if (type != SqlServerSettings.ContainerType) ServerName.Text = _serverText.GetValueOrDefault(type) ?? DefaultServer(type);
        _loading = false;
        _serverType = type;
        ShowServerFields();
        Edited();
    }

    private void Auth_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || SqlPanel is null) return;
        ShowServerFields();
        Edited();
    }

    /// <summary>The fields the chosen connection type has: the container's address and SA password, or the server and its login.</summary>
    private void ShowServerFields()
    {
        var type = ChosenServerType;
        var sqlServer = type == "sqlServer";
        ContainerFields.Visibility = type == SqlServerSettings.ContainerType ? Visibility.Visible : Visibility.Collapsed;
        ServerFields.Visibility = type == SqlServerSettings.ContainerType ? Visibility.Collapsed : Visibility.Visible;
        AuthFields.Visibility = sqlServer ? Visibility.Visible : Visibility.Collapsed;
        LoginFields.Visibility = sqlServer && SqlAuth.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ServerGroupLabel.Text = sqlServer ? "SQL Server" : "LocalDB";
        ServerLabel.Text = sqlServer ? "Server" : "LocalDB instance";
        ServerHint.Text = sqlServer
            ? @"The instance, e.g. .\SQLEXPRESS, localhost or localhost,1433."
            : $"{DatabaseConnection.LocalDbServer} is the instance every Windows user has. Each site runs its database in the LocalDB of its app pool identity.";
        AuthHint.Text = SqlAuth.IsChecked == true
            ? "The login must be able to create a database, or own it when it exists. Its password is kept in the Windows Credential Manager of your account, not with the other settings."
            : @"Each site signs in as its app pool's identity (IIS APPPOOL\<project>) - DNN Manager creates that login with your Windows account and makes it the database's owner.";
    }

    private void Install_Checked(object sender, RoutedEventArgs e) => Edited();

    // ─── Keep warm ────────────────────────────────────────────────────────

    private void KeepWarm_Changed(object sender, SelectionChangedEventArgs e) => Edited();

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

    private void Defaults_Changed(object sender, SelectionChangedEventArgs e)
    {
        ShowDefaultsHint();
        Edited();
    }

    private void ShowDefaultsHint()
    {
        if (DnnDefaultsHint is null) return; // raised during InitializeComponent
        var suffix = HostnameSuffix.Text.Trim().Trim('.');
        var language = (DnnLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "en-US";
        DnnDefaultsHint.Text = $"An empty e-mail is host@{suffix}; an empty website name is the project's name. The password is kept in the " +
                               $"Windows Credential Manager of your account, not with the other settings (empty: {DnnDefaultsSettings.DefaultHostPassword})." +
                               (language == "en-US" ? "" : $" {Languages.Name(language)}: DNN downloads its language pack while installing (needs internet).");
    }

    private void ShowError(string? error)
    {
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // DNNMANAGER_* environment variables are applied on top of the file, so they win over what's saved here.
    private void ShowEnvironmentOverrides()
    {
        var overrides = Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(k => k.StartsWith(Program.EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (overrides.Count == 0) return;
        EnvWarningText.Text = "These environment variables override the saved settings: " + string.Join(", ", overrides);
        EnvWarning.Visibility = Visibility.Visible;
    }

}
