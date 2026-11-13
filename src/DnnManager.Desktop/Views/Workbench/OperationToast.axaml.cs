using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DnnManager.Presentation.Services;

namespace DnnManager.Desktop.Views.Workbench;

/// <summary>
/// The running operation as a toast over the bottom-right corner - its name, Cancel and a moving bar - while it runs. A click
/// on the name opens Output. Cancel pressed again stops the undo (one that waits on something that doesn't answer).
/// </summary>
public partial class OperationToast : UserControl
{
    private const string StopUndoing = "Stop undoing";
    private OperationRunner? _runner;

    public OperationToast() => InitializeComponent();

    /// <summary>The operation's name was clicked - the window shows its output.</summary>
    public event EventHandler? OperationClicked;

    public void Attach(OperationRunner runner)
    {
        _runner = runner;
        runner.PropertyChanged += OnRunnerChanged;
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var current = _runner?.Current;
            IsVisible = current is not null;
            if (current is null) ShowCancel(stopUndo: false, enabled: true);
            OperationText.Text = current is null ? "" : $"{current}…";
            OperationProgress.IsIndeterminate = current is not null;
        });
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        var stopping = CancelButton.Content as string == StopUndoing;
        _runner?.Cancel();
        ShowCancel(stopUndo: !stopping, enabled: !stopping);
    }

    private void ShowCancel(bool stopUndo, bool enabled)
    {
        CancelButton.Content = stopUndo ? StopUndoing : "Cancel";
        ToolTip.SetTip(CancelButton, stopUndo ? "Stop undoing - what isn't undone yet is named in Output" : "Cancel");
        CancelButton.IsEnabled = enabled;
    }

    private void Operation_PointerReleased(object? sender, PointerReleasedEventArgs e) => OperationClicked?.Invoke(this, EventArgs.Empty);
}
