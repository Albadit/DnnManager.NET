using DnnManager.Application.Abstractions;
using DnnManager.Presentation.Controls;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Answers use-case questions with modal dialogs. Use cases call this from the thread pool, so the
/// dialog is shown on the UI thread and the use case awaits the answer.
/// </summary>
public sealed class GuiUserPrompt : IUserPrompt
{
    public Task<bool> ConfirmAsync(string question, string yes, string no, bool defaultYes = false, CancellationToken ct = default)
        => OnUiThread(() => Dialogs.Confirm(question, yes, no, defaultYes));

    public Task<bool> ConfirmDangerAsync(string question, string yes, string no, CancellationToken ct = default)
        => OnUiThread(() => Dialogs.ConfirmDanger(question, yes, no));

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

    /// <summary>
    /// Before something that can't be taken back (deleting, dropping, resetting): <paramref name="yes"/> is the red
    /// button, and Enter picks <paramref name="no"/>.
    /// </summary>
    public static bool ConfirmDanger(string question, string yes, string no) =>
        MessageDialog.Ask(question, yes, no, defaultYes: false, danger: true);

    public static void Error(string message) => MessageDialog.Warn(message);
}
