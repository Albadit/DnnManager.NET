using DnnManager.Application.Abstractions;
using DnnManager.Presentation.Controls;

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
    public Task<bool> ConfirmAsync(string question, string yes, string no, bool defaultYes = false, CancellationToken ct = default)
        => OnUiThread(() => Dialogs.Confirm(question, yes, no, defaultYes));

    private static Task<T> OnUiThread<T>(Func<T> show)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        return dispatcher.CheckAccess() ? Task.FromResult(show()) : dispatcher.InvokeAsync(show).Task;
    }
}

/// <summary>Questions and warnings, shown in the app's own themed <see cref="MessageDialog"/>.</summary>
internal static class Dialogs
{
    /// <summary>True when <paramref name="yes"/> is chosen - both buttons say what they do, e.g. "Quit anyway" / "Keep running".</summary>
    public static bool Confirm(string question, string yes, string no, bool defaultYes = false) =>
        MessageDialog.Ask(question, yes, no, defaultYes);

    public static void Error(string message) => MessageDialog.Warn(message);
}
