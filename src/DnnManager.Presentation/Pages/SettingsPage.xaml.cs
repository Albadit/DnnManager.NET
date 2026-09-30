using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Startup;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// The settings in <c>settings.json</c>, as a page of its own: categories (with a search box) on the left, the chosen
/// one on the right. Every change - in any category - makes <b>Save</b> ready; Save checks the values, writes the
/// file and puts them to work at once (<see cref="LiveSettings"/>, the theme, the terminal, the start at sign-in) -
/// no restart. <b>Discard changes</b> puts the saved values back; <b>Close</b> goes back to the page Settings was
/// opened from.
/// </summary>
public partial class SettingsPage : UserControl
{
    // Form labels for the settings keys, so a problem reads "Site port must be…" rather than "projects.sitePort must be…".
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["projects.baseDirectory"] = "Projects folder",
        ["projects.sitePort"] = "Site port",
        ["projects.hostnameSuffix"] = "Hostname suffix",
        ["projects.dnnReleaseSources"] = "DNN repositories",
        ["sqlServer.host"] = "Server host",
        ["sqlServer.port"] = "Port",
        ["sqlServer.saPassword"] = "SA password",
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

    public SettingsPage(IOptions<AppOptions> options, OperationRunner runner, SettingsStore store, LiveSettings live,
        AppDataPaths paths, TerminalService terminal, StartupTask startup)
    {
        _options = options.Value;
        _runner = runner;
        _store = store;
        _live = live;
        _terminal = terminal;
        _startup = startup;
        InitializeComponent();

        _categories = new Dictionary<string, (FrameworkElement, string)>
        {
            ["General"] = (GeneralPanel, "general start sign in startup theme light dark system appearance terminal shell powershell command prompt git bash font family size settings file settings.json folder"),
            ["Projects"] = (ProjectsPanel, "projects folder base directory hostname suffix site port address url"),
            ["Releases"] = (ReleasesPanel, "dnn releases repositories github versions install packages keep download"),
            ["Sql"] = (SqlPanel, "sql server connection host port sa password ssms management studio remember"),
            ["Docker"] = (DockerPanel, "docker container name volume edition mssql_pid collation"),
            ["Iis"] = (IisPanel, "iis windows features " + string.Join(' ', _options.RequiredIisFeatures.Select(f => $"{f.Label} {f.Name}"))),
            ["About"] = (AboutPanel, "about version your files folders settings backups logs packages"),
        };

        SettingsFileText.Text = _store.FilePath;
        SettingsFileText.ToolTip = _store.FilePath;
        KeepDnnPackagesHint.Text = $"Saved in {paths.PackagesDirectory} and used again when a new project picks the same " +
                                   "version - no download. Off: each new project downloads its package and deletes it after installing.";
        IisFeatures.ItemsSource = _options.RequiredIisFeatures;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "DNN Manager" : $"DNN Manager {version.Major}.{version.Minor}.{version.Build}";
        Folders.ItemsSource = new KeyValuePair<string, string>[]
        {
            new("Settings", paths.Root),
            new("Backups", paths.BackupsDirectory),
            new("Logs", paths.LogsDirectory),
            new("DNN packages", paths.PackagesDirectory),
        };

        foreach (var box in new[] { BaseDirectory, SitePort, HostnameSuffix, ReleaseApis, ContainerName, ContainerIp,
                                    VolumeName, DefaultPort, Collation, MssqlPid })
            box.TextChanged += (_, _) => Edited();
        SaPassword.PasswordChanged += (_, _) => Edited();
        foreach (var box in new[] { SsmsRememberPassword, KeepDnnPackages })
        {
            box.Checked += (_, _) => Edited();
            box.Unchecked += (_, _) => Edited();
        }


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

    // ─── Categories ───────────────────────────────────────────────────────

    private void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = (Categories.SelectedItem as ListBoxItem)?.Tag as string;
        foreach (var (key, category) in _categories)
            category.Panel.Visibility = key == selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowCategory(string key) =>
        Categories.SelectedItem = Categories.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == key);

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

    // ─── Theme ────────────────────────────────────────────────────────────

    private void Theme_Checked(object sender, RoutedEventArgs e) => Edited();

    private string ChosenTheme => ThemeLight.IsChecked == true ? "light" : ThemeDark.IsChecked == true ? "dark" : "system";

    private void ShowTheme(string theme)
    {
        _loading = true;
        ThemeLight.IsChecked = theme.Equals("light", StringComparison.OrdinalIgnoreCase);
        ThemeDark.IsChecked = theme.Equals("dark", StringComparison.OrdinalIgnoreCase);
        ThemeSystem.IsChecked = ThemeLight.IsChecked != true && ThemeDark.IsChecked != true;
        _loading = false;
    }

    // ─── Terminal ─────────────────────────────────────────────────────────

    private void ShowTerminal(TerminalSettings settings)
    {
        _loading = true;
        TerminalEnabled.IsChecked = settings.Enabled;
        TerminalOptions.IsEnabled = settings.Enabled;
        // A shell that isn't installed (any more): the first one that is, as the terminal itself does.
        if (!Select(DefaultShell, settings.DefaultShell)) Select(DefaultShell, _terminal.Shells[0].Key);
        // A font or size typed into settings.json that isn't in the list is added, so it stays chosen.
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
        TerminalOptions.IsEnabled = TerminalEnabled.IsChecked == true;
        Edited();
    }

    /// <summary>The terminal's settings as the form has them.</summary>
    private TerminalSettings ChosenTerminal => new()
    {
        Enabled = TerminalEnabled.IsChecked == true,
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

        ShowTheme(saved.Appearance.Theme);
        ShowTerminal(saved.Terminal);
        _loading = true;
        if (_startsAtSignIn is { } starts) StartAtSignIn.IsChecked = starts;
        var p = saved.Projects;
        var sql = saved.SqlServer;
        var docker = saved.Docker;
        BaseDirectory.Text = p.BaseDirectory;
        SitePort.Text = p.SitePort.ToString();
        HostnameSuffix.Text = p.HostnameSuffix;
        ReleaseApis.Text = string.Join(Environment.NewLine, p.DnnReleaseSources);
        KeepDnnPackages.IsChecked = p.KeepDnnPackages;
        ContainerIp.Text = sql.Host;
        DefaultPort.Text = sql.Port.ToString();
        SaPassword.Password = sql.SaPassword;
        ContainerName.Text = docker.ContainerName;
        VolumeName.Text = docker.VolumeName;
        MssqlPid.Text = docker.Edition;
        Collation.Text = docker.Collation;
        SsmsRememberPassword.IsChecked = saved.Ssms.RememberPassword;
        _loading = false;

        _savedForm = FormSnapshot();
        ShowError(null);
        SetDirty(false);
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
            KeepDnnPackages.IsChecked == true, ContainerIp.Text.Trim(), DefaultPort.Text.Trim(), SaPassword.Password,
            ContainerName.Text.Trim(), VolumeName.Text.Trim(), MssqlPid.Text.Trim(), Collation.Text.Trim(),
            SsmsRememberPassword.IsChecked == true, ChosenTheme,
            terminal.Enabled, terminal.DefaultShell, terminal.FontFamily, terminal.FontSize);
    }

    /// <summary>The form has edits that aren't saved - leaving the page would lose them.</summary>
    public bool HasUnsavedChanges => _dirty;

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        // Save and Discard come to life with the first edit, until it's saved or discarded.
        SaveButton.IsEnabled = dirty;
        DiscardButton.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = dirty ? "You have unsaved changes." : "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "LogWarn");
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
            // Start from the file, so keys this page doesn't edit (theme, IIS features, table columns…) stay as they are.
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
            ShowError($"Could not save {_store.FilePath}: {ex.Message}");
            Toast.Show($"Could not save the settings: {ex.Message}", ToastKind.Error);
            return;
        }

        _live.Apply(settings);
        ThemeManager.Initialize(settings.Appearance.Theme);
        _terminal.Apply(settings.Terminal);
        _savedForm = FormSnapshot();
        ShowError(null);
        SetDirty(false);
        if (await ApplyStartAtSignInAsync()) Toast.Show("Settings saved - they apply from now on.", ToastKind.Success);
    }

    /// <summary>The category that edits the settings key <paramref name="key"/>, e.g. <c>sqlServer.port</c>.</summary>
    private static string? CategoryOf(string key) => key switch
    {
        "projects.dnnReleaseSources" => "Releases",
        _ when key.StartsWith("terminal.", StringComparison.Ordinal) || key.StartsWith("appearance.", StringComparison.Ordinal) => "General",
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
        if (!int.TryParse(DefaultPort.Text.Trim(), out var sqlPort))
            return ("Port must be a number between 1 and 65535.", "Sql");

        var p = settings.Projects;
        p.BaseDirectory = BaseDirectory.Text.Trim();
        p.SitePort = sitePort;
        p.HostnameSuffix = HostnameSuffix.Text.Trim().Trim('.');
        p.DnnReleaseSources = ReleaseApis.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        p.KeepDnnPackages = KeepDnnPackages.IsChecked == true;

        var sql = settings.SqlServer;
        sql.Host = ContainerIp.Text.Trim();
        sql.Port = sqlPort;
        sql.SaPassword = SaPassword.Password;

        var docker = settings.Docker;
        docker.ContainerName = ContainerName.Text.Trim();
        docker.VolumeName = VolumeName.Text.Trim();
        docker.Edition = MssqlPid.Text.Trim();
        docker.Collation = Collation.Text.Trim();

        settings.Ssms.RememberPassword = SsmsRememberPassword.IsChecked == true;
        settings.Appearance.Theme = ChosenTheme;
        settings.Terminal = ChosenTerminal;
        return (null, null);
    }


    private void BrowseBaseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Projects folder" };
        if (Directory.Exists(BaseDirectory.Text)) dialog.InitialDirectory = BaseDirectory.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) BaseDirectory.Text = dialog.FolderName;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e) => SettingsStartup.OpenInEditor(_store.FilePath);

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.GetDirectoryName(_store.FilePath)!);

    private void OpenListedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string folder }) OpenFolder(folder);
    }

    private static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Toast.Show($"{folder} doesn't exist yet - it is made when it's first needed.", ToastKind.Info);
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error($"Could not open {folder}: {ex.Message}"); }
    }

    private void Preview_Changed(object sender, TextChangedEventArgs e)
    {
        if (UrlPreview is null) return; // raised during InitializeComponent
        var suffix = HostnameSuffix.Text.Trim().Trim('.');
        var port = int.TryParse(SitePort.Text.Trim(), out var p) && p is > 0 and <= 65535 ? p : 80;
        UrlPreview.Text = $"Sites answer at http://<project>.{suffix}{(port == 80 ? "" : $":{port}")}";
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
