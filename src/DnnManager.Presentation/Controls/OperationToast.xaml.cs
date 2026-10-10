using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The running operation as a toast over the bottom-right corner - its name, Cancel and a moving bar - from when it
/// gets under way (<see cref="OperationRunner.Current"/>) until it ends. A click on the name opens the Output tab.
/// </summary>
public partial class OperationToast : UserControl
{
    private OperationRunner _runner = null!;

    public OperationToast() => InitializeComponent();

    /// <summary>The operation's name was clicked - the window shows its output.</summary>
    public event EventHandler? OperationClicked;

    public void Attach(OperationRunner runner)
    {
        _runner = runner;
        _runner.PropertyChanged += OnRunnerChanged;
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        var current = _runner.Current;
        Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        // Ended: the next operation can be cancelled again.
        if (current is null) ShowCancel(stopUndo: false, enabled: true);
        OperationText.Text = current is null ? "" : $"{current}…";
        ShowProgress();
    }

    /// <summary>
    /// The bar moves while an operation runs - but not while the window is minimized (<see cref="EfficiencyMode"/>):
    /// nobody sees it then, and its animation alone keeps WPF drawing about 60 frames a second.
    /// </summary>
    private void ShowProgress() =>
        OperationProgress.IsIndeterminate = _runner.Current is not null && !EfficiencyMode.GetIsSaving(this) && !Motion.GetOff(this);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Minimized or restored while an operation runs: the bar stops, or moves again.
        if ((e.Property == EfficiencyMode.IsSavingProperty || e.Property == Motion.OffProperty) && _runner is not null) ShowProgress();
    }

    // First press: cancel, and what the operation made is undone. Pressed again: stop that undo (one that waits on
    // something that doesn't answer) - its remaining steps are said as not undone. Then there is nothing more to press.
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var stopping = CancelButton.Content as string == StopUndoing;
        _runner.Cancel();
        ShowCancel(stopUndo: !stopping, enabled: !stopping);
    }

    private const string StopUndoing = "Stop undoing";

    private void ShowCancel(bool stopUndo, bool enabled)
    {
        CancelButton.Content = stopUndo ? StopUndoing : "Cancel";
        CancelButton.ToolTip = stopUndo ? "Stop undoing - what isn't undone yet is named on the Output tab" : "Cancel";
        System.Windows.Automation.AutomationProperties.SetName(CancelButton, (string)CancelButton.Content);
        CancelButton.IsEnabled = enabled;
    }

    private void Operation_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OperationClicked?.Invoke(this, EventArgs.Empty);
}
