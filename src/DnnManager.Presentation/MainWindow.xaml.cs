using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Files;
using DnnManager.Presentation.Pages;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ActivityLog _log;
    private readonly OperationRunner _runner;

    // One entry per sidebar item. Pages are rebuilt on every visit so their lists (folders, saved
    // connections, backups…) are always fresh - the same as re-entering a TUI menu.
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Projects"]      = typeof(ProjectsPage),
        ["Setup"]         = typeof(SetupPage),
        ["Existing"]      = typeof(ExistingFolderPage),
        ["Clone"]         = typeof(ClonePage),
        ["Connections"]   = typeof(ConnectionsPage),
        ["Prerequisites"] = typeof(PrerequisitesPage),
        ["Settings"]      = typeof(SettingsPage),
    };

    public MainWindow(IServiceProvider services, ActivityLog log, OperationRunner runner, IOptions<AppOptions> options)
    {
        _services = services; _log = log; _runner = runner;
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
        BaseDirText.Text = options.Value.BaseDirectory;
        BaseDirText.ToolTip = options.Value.BaseDirectory;

        LogList.Attach(_log.Entries);
        ThemeManager.Track(this);
        ThemeManager.Changed += (_, _) => UpdateThemeButton();
        UpdateThemeButton();
        _runner.PropertyChanged += OnRunnerChanged;

        // On short screens (e.g. 768px laptops) the default height would push the window off screen.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += (_, _) => NavProjects.IsChecked = true;
        Closing += OnClosing;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key } || !Pages.TryGetValue(key, out var type)) return;
        PageHost.Content = ActivatorUtilities.CreateInstance(_services, type);
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        var busy = _runner.IsBusy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RunningText.Text = busy ? $"{_runner.Current}…" : "";

        // Let the page refresh whatever the operation changed (new folder, removed site…).
        if (!busy && PageHost.Content is IRefreshable page) page.Refresh();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _runner.Cancel();

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        // Remembered in appsettings.json; failing to save only means the next start uses the old theme.
        try { AppSettingsFile.SaveTheme(ThemeManager.Current.ToString()); }
        catch (Exception ex) { _log.Fail($"Could not save the theme to {AppSettingsFile.FullPath}: {ex.Message}"); }
    }

    // The button shows what a click switches to: a moon in light mode, a sun in dark mode.
    private void UpdateThemeButton()
    {
        var dark = ThemeManager.Current == AppTheme.Dark;
        ThemeGlyph.Text = dark ? "\uE706" : "\uE708";
        ThemeButton.ToolTip = dark ? "Switch to light theme" : "Switch to dark theme";
    }

    // Height the log had when it was hidden (it may have been resized with the splitter), restored on show.
    private GridLength _logHeight = new(230);

    private bool LogOpen => LogList.Visibility == Visibility.Visible;

    private void ToggleLog_Click(object sender, RoutedEventArgs e) => SetLogOpen(!LogOpen);

    /// <summary>
    /// Hides or shows the activity log. Hidden, only its header bar stays - with the running
    /// operation, its progress and Cancel - so nothing that's going on gets lost.
    /// </summary>
    private void SetLogOpen(bool open)
    {
        if (open == LogOpen) return;
        if (open)
        {
            LogList.Visibility = Visibility.Visible;
            LogSplitter.Visibility = Visibility.Visible;
            SplitterRow.Height = new GridLength(5);
            LogRow.MinHeight = 90;
            LogRow.Height = _logHeight;
            LogList.ScrollToEnd();
        }
        else
        {
            _logHeight = LogRow.Height;
            LogList.Visibility = Visibility.Collapsed;
            LogSplitter.Visibility = Visibility.Collapsed;
            SplitterRow.Height = new GridLength(0);
            LogRow.MinHeight = 0;
            LogRow.Height = GridLength.Auto;
        }
        // Closed, the splitter is gone - a line on top separates the bar from the page instead. Copy / Clear
        // act on a log you can't see then, so they hide with it.
        LogHeader.BorderThickness = open ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
        CopyLogButton.Visibility = ClearLogButton.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ToggleLogGlyph.Text = open ? "\uE70D" : "\uE70E"; // chevron down = hide, up = show
        ToggleLogButton.ToolTip = open ? "Hide activity" : "Show activity";
    }

    // Copies the selected part of the log, or - with nothing selected - the whole log with timestamps.
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = LogList.Selection.IsEmpty ? _log.ToText() : LogList.Selection.Text.TrimEnd();
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_runner.IsBusy) return;
        if (!Dialogs.Confirm($"'{_runner.Current}' is still running. Quit anyway?"))
        {
            e.Cancel = true;
            return;
        }
        _runner.Cancel();
    }
}

/// <summary>A page that reloads its data after an operation finishes.</summary>
public interface IRefreshable
{
    void Refresh();
}
