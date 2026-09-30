using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using DnnManager.Application.Abstractions;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The state of IIS (its web service, W3SVC) in the status bar's first cell, under the sidebar, with Restart and Stop
/// while it runs and Start while it's stopped. It shows what the <see cref="ServerStore"/> knows - Windows reports
/// the service's status, so a start or stop from anywhere shows up by itself. Compact - under the icon-only sidebar -
/// it's the dot (the state in its tooltip) and a ⋮ menu with the same actions.
/// </summary>
public partial class IisStatus : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool),
        typeof(IisStatus), new PropertyMetadata(false, (d, _) => ((IisStatus)d).ApplyLayout()));

    private ServerStore _store = null!;
    private OperationRunner _runner = null!;

    public IisStatus() => InitializeComponent();

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public void Attach(ServerStore store, OperationRunner runner)
    {
        _store = store; _runner = runner;
        _store.RuntimeChanged += (_, _) => Show();
        // The buttons wait for whatever operation is running.
        _runner.PropertyChanged += OnRunnerChanged;
        Show();
    }

    private void ApplyLayout()
    {
        var compact = IsCompact;
        StateText.Visibility = Buttons.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        Layout.Margin = compact ? new Thickness(10, 0, 2, 0) : new Thickness(14, 0, 6, 0);
    }

    // Compact: the actions IIS's state allows, in a menu above the button.
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        void Add(string header, Button button, RoutedEventHandler click)
        {
            if (button.Visibility != Visibility.Visible) return;
            var item = new MenuItem { Header = header, IsEnabled = button.IsEnabled };
            item.Click += click;
            menu.Items.Add(item);
        }
        Add("Start IIS", StartButton, Start_Click);
        Add("Restart IIS", RestartButton, Restart_Click);
        Add("Stop IIS", StopButton, Stop_Click);
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = StateText.Text, IsEnabled = false });
        menu.IsOpen = true;
    }

    /// <summary>What the store knows about IIS now - or, while DNN Manager itself is changing it, what it is doing.</summary>
    private void Show()
    {
        var state = _store.Runtime;
        var pending = _store.RuntimePending;
        StateText.Text = pending ?? state switch
        {
            IisServerState.Running => "IIS running",
            IisServerState.Stopped => "IIS stopped",
            IisServerState.Starting => "IIS starting…",
            IisServerState.Stopping => "IIS stopping…",
            IisServerState.NotInstalled => "IIS not installed",
            _ => "IIS unknown"
        };
        State.ToolTip = StateText.Text + " - " + (state == IisServerState.NotInstalled
            ? "the IIS web service (W3SVC) isn't installed; enable the IIS features on the Environment page."
            : "the IIS web service (W3SVC), which every site runs in.");

        var (stroke, fill) = pending is not null ? ("LogWarn", "LogWarn") : state switch
        {
            IisServerState.Running => ("SuccessText", "SuccessText"),
            IisServerState.Starting or IisServerState.Stopping => ("LogWarn", "LogWarn"),
            IisServerState.NotInstalled or IisServerState.Unknown => ("ErrorText", null),
            _ => ("TextMuted", (string?)null)
        };
        Dot.SetResourceReference(Shape.StrokeProperty, stroke);
        if (fill is null) Dot.Fill = System.Windows.Media.Brushes.Transparent;
        else Dot.SetResourceReference(Shape.FillProperty, fill);
        // The text in the dot's colour, like Docker Desktop's "Engine running".
        StateText.SetResourceReference(TextBlock.ForegroundProperty, stroke);

        // Running: Restart and Stop. Stopped: Start. Changing, missing or unknown: nothing to press.
        var settled = pending is null;
        StartButton.Visibility = settled && state == IisServerState.Stopped ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.Visibility = StopButton.Visibility =
            settled && state == IisServerState.Running ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = RestartButton.IsEnabled = StopButton.IsEnabled = !_runner.IsBusy;
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationRunner.Current)) Show();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Control(IisServerAction.Start);
    private void Stop_Click(object sender, RoutedEventArgs e) => Control(IisServerAction.Stop);
    private void Restart_Click(object sender, RoutedEventArgs e) => Control(IisServerAction.Restart);

    private async void Control(IisServerAction action) => await _store.ControlIisAsync(action);
}
