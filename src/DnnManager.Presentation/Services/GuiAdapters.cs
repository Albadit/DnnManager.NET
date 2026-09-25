using System.Windows;
using DnnManager.Application.Abstractions;

namespace DnnManager.Presentation.Services;

/// <summary>Adapts the activity log to the application-layer reporter interface.</summary>
public sealed class GuiProgressReporter : IProgressReporter
{
    private readonly ActivityLog _log;
    public GuiProgressReporter(ActivityLog log) => _log = log;
    public void Step(string title)   => _log.Step(title);
    public void Info(string message) => _log.Info(message);
    public void Success(string m)    => _log.Success(m);
    public void Fail(string m)       => _log.Fail(m);
    public void Warn(string m)       => _log.Warn(m);
    public void Progress(string m)   => _log.Progress(m);
}

/// <summary>
/// Answers use-case questions with modal dialogs. Use cases call this from the thread pool, so the
/// dialog is shown on the UI thread and the use case awaits the answer.
/// </summary>
public sealed class GuiUserPrompt : IUserPrompt
{
    public Task<bool> ConfirmAsync(string question, bool defaultYes = false, CancellationToken ct = default)
        => OnUiThread(() => Dialogs.Confirm(question, defaultYes));

    private static Task<T> OnUiThread<T>(Func<T> show)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        return dispatcher.CheckAccess() ? Task.FromResult(show()) : dispatcher.InvokeAsync(show).Task;
    }
}

internal static class Dialogs
{
    private const string Caption = "DNN Manager";

    private static Window? Owner => System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null;

    public static bool Confirm(string question, bool defaultYes = false)
    {
        var result = Owner is { } owner
            ? MessageBox.Show(owner, question, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question,
                defaultYes ? MessageBoxResult.Yes : MessageBoxResult.No)
            : MessageBox.Show(question, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question,
                defaultYes ? MessageBoxResult.Yes : MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public static void Error(string message)
    {
        if (Owner is { } owner) MessageBox.Show(owner, message, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(message, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
