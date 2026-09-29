using DnnManager.Presentation.Controls;

namespace DnnManager.Presentation.Services;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>
/// A short message popping up over the bottom-right of the page and fading out by itself - for feedback on
/// something that happened out of view, like a setting saved. Warnings and errors stay until closed or replaced.
/// </summary>
public static class Toast
{
    private static ToastView? _view;

    /// <summary>Called by the main window with the view it shows toasts in.</summary>
    public static void Attach(ToastView view) => _view = view;

    public static void Show(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
        // Successes go by themselves; something that needs attention waits for the user.
        var duration = kind switch
        {
            ToastKind.Success or ToastKind.Info => TimeSpan.FromSeconds(action is null ? 3 : 8),
            _ => TimeSpan.Zero
        };
        _view?.Show(message, kind, duration, actionText, action);
    }

    public static void Hide() => _view?.Hide();
}
