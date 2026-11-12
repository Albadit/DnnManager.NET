using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DnnManager.Application;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Desktop.Views;

/// <summary>
/// The window: the activity bar, the view it shows - Source Control, Settings, and on macOS and Linux what only Windows
/// has, saying so - with Output below and the status bar.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _services = Program.Services;
    private readonly ActivityLog _log;
    private readonly OperationRunner _runner;
    private readonly Dictionary<string, Control> _views = [];
    private readonly DispatcherTimer _outputTimer;
    private int _outputShown = -1;
    private double _outputHeight = 220;

    public MainWindow()
    {
        InitializeComponent();
        _log = _services.GetRequiredService<ActivityLog>();
        _runner = _services.GetRequiredService<OperationRunner>();
        PlatformText.Text = Platform.Name;

        // What only Windows has: said on the button, and by the view it opens.
        foreach (var (button, feature, name) in new[] { (NavProjects, PlatformFeature.Iis, "Projects"), (NavSetup, PlatformFeature.Setup, "New project") })
            ToolTip.SetTip(button, Platform.WhyNot(feature) is { } why ? $"{name} - Windows only. {why}" : $"{name} - in the Windows app for now (DnnManager.exe).");

        _runner.PropertyChanged += Runner_Changed;
        _runner.Refused += message => Dispatcher.UIThread.Post(() => StatusText.Text = message);
        _log.RunEnded += _ => Dispatcher.UIThread.Post(FillOutput);
        _outputTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => FillOutput());

        foreach (var notice in Program.StartupNotices)
        {
            if (notice.IsWarning) _log.Warn(notice.Message);
            else _log.Info(notice.Message);
        }
        if (Program.StartupNotices.Any(n => n.IsWarning)) Opened += (_, _) => ShowOutput(true);

        Show("SourceControl");
        KeyDown += Window_KeyDown;
        Closing += Window_Closing;
    }

    // ─── Views ─────────────────────────────────────────────────────────────

    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string view }) Show(view);
    }

    /// <summary>Shows the view <paramref name="name"/> - made the first time.</summary>
    public void Show(string name)
    {
        foreach (var button in new[] { NavSourceControl, NavProjects, NavSetup, NavSettings })
            button.IsChecked = (string?)button.Tag == name;
        if (!_views.TryGetValue(name, out var view))
        {
            view = name switch
            {
                "SourceControl" => new SourceControlView(),
                "Settings" => new SettingsView(),
                "Projects" => NotHere("Projects", PlatformFeature.Iis),
                _ => NotHere("New project", PlatformFeature.Setup)
            };
            _views[name] = view;
        }
        ViewHost.Content = view;
        if (view is SourceControlView scm) scm.Activated();
    }

    /// <summary>A view of what isn't here: why (Windows only), and what to use instead.</summary>
    private static Control NotHere(string title, PlatformFeature feature)
    {
        var why = Platform.WhyNot(feature) ??
                  "This part of DNN Manager is still in the Windows app (DnnManager.exe) - it is being moved here. Source Control works here already.";
        return new Border
        {
            Padding = new Avalonia.Thickness(40),
            Child = new StackPanel
            {
                MaxWidth = 560, Spacing = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = Platform.IsWindows ? "Not in this app yet" : $"Not available on {Platform.Name}", FontSize = 15 },
                    new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                }
            }
        };
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command && e.Key == Key.Oem3)
        {
            ShowOutput(!OutputPanel.IsVisible);
            e.Handled = true;
        }
        else if (command && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.G)
        {
            Show("SourceControl");
            e.Handled = true;
        }
        else if (command && e.Key == Key.OemComma)
        {
            Show("Settings");
            e.Handled = true;
        }
    }

    // An operation running: quitting would cut it off half-way - it is asked about first.
    private bool _quitting;

    private async void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || !_runner.IsBusy) return;
        e.Cancel = true;
        if (!await Services.Dialogs.ConfirmAsync($"'{_runner.Current ?? "An operation"}' is still running. Cancel it and quit? What it did so far is put back first.",
                "Cancel and quit", "Keep running", danger: true))
            return;
        _runner.Cancel();
        await _runner.WhenIdleAsync();
        _quitting = true;
        Close();
    }

    // ─── Output and the status bar ───────────────────────────────────────

    private void Runner_Changed(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        StatusText.Text = _runner.Current is { } current ? $"⟳ {current}" : "";
        CancelButton.IsVisible = _runner.IsBusy;
        if (_runner.IsBusy && OutputPanel.IsVisible) _outputTimer.Start();
        FillOutput();
    });

    private void ToggleOutput_Click(object? sender, RoutedEventArgs e) => ShowOutput(!OutputPanel.IsVisible);

    public void ShowOutput(bool show)
    {
        if (!show && OutputPanel.IsVisible) _outputHeight = Body.RowDefinitions[2].ActualHeight;
        OutputPanel.IsVisible = OutputSplitter.IsVisible = show;
        Body.RowDefinitions[2].Height = show ? new GridLength(_outputHeight) : new GridLength(0);
        if (show)
        {
            _outputShown = -1;
            FillOutput();
            if (_runner.IsBusy) _outputTimer.Start();
        }
        else _outputTimer.Stop();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _runner.Cancel();

    private void ClearOutput_Click(object? sender, RoutedEventArgs e)
    {
        _log.Clear();
        _outputShown = -1;
        Dispatcher.UIThread.Post(FillOutput);
    }

    /// <summary>The log's runs and lines as rows - again when it has more to say.</summary>
    private void FillOutput()
    {
        if (!OutputPanel.IsVisible) return;
        if (!_runner.IsBusy) _outputTimer.Stop();
        var rows = new List<OutputRow>();
        foreach (var item in _log.Items)
        {
            switch (item)
            {
                case OutputRun run:
                    var mark = run.Status switch { RunStatus.Running => "⟳", RunStatus.Finished => "✓", RunStatus.Cancelled => "■", _ => "✕" };
                    rows.Add(new OutputRow
                    {
                        Text = $"{mark} {run.Title}  ·  {run.TimeRange}" + (run.Outcome is { } outcome ? $"  -  {outcome}" : ""),
                        Foreground = run.Status switch { RunStatus.Failed => Brushes.IndianRed, RunStatus.Cancelled => Brushes.Goldenrod, _ => null }
                    });
                    foreach (var stage in run.Stages.Where(s => s.IsShownInLog))
                    {
                        rows.Add(new OutputRow { Text = $"    {StageMark(stage.Status)} {stage.Name}  {stage.DurationText}" });
                        rows.AddRange(stage.Lines.Select(Line));
                    }
                    rows.AddRange(run.Notes.Select(Line));
                    break;
                case OutputLine line:
                    rows.Add(Line(line));
                    break;
            }
        }
        if (rows.Count == _outputShown && !_runner.IsBusy) return;
        _outputShown = rows.Count;
        OutputList.ItemsSource = rows;
        if (rows.Count > 0) OutputList.ScrollIntoView(rows.Count - 1);

        static OutputRow Line(OutputLine line) => new()
        {
            Text = $"        {line.Text}",
            Foreground = line.Level switch
            {
                LineLevel.Error => Brushes.IndianRed, LineLevel.Warn => Brushes.Goldenrod, LineLevel.Success => Brushes.MediumSeaGreen, _ => null
            }
        };

        static string StageMark(StageStatus status) => status switch
        {
            StageStatus.Running => "⟳", StageStatus.Ok => "✓", StageStatus.Warning => "!", StageStatus.Failed => "✕",
            StageStatus.Skipped => "-", StageStatus.Cancelled => "■", _ => "·"
        };
    }

    /// <summary>The status bar's text - what a view has to say for a moment.</summary>
    public void Say(string text) => StatusText.Text = text;
}
