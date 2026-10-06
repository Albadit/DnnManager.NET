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
/// the service's status, so a start or stop from anywhere shows up by itself.
/// </summary>
public partial class IisStatus : UserControl
{
    private ServerStore _store = null!;
    private OperationRunner _runner = null!;

    public IisStatus() => InitializeComponent();

    /// <summary>
    /// Under a sidebar on the window's right: the pixel that lines it up with the sidebar's edge on its left side. No line
    /// there - only the room the sidebar's edge takes.
    /// </summary>
    public void SetSide(bool right) => Frame.Padding = right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);

    public void Attach(ServerStore store, OperationRunner runner)
    {
        _store = store; _runner = runner;
        _store.RuntimeChanged += (_, _) => Show();
        // The buttons wait for whatever operation is running.
        _runner.PropertyChanged += OnRunnerChanged;
        Show();
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
            ? "IIS, the web server, isn't installed: Settings → IIS → Set up IIS turns it on."
            : "IIS is the web server every website runs in (the W3SVC service). Running: your websites can be opened.");

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
