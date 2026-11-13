using Avalonia.Threading;
using DnnManager.Desktop.Views.Workbench;

namespace DnnManager.Desktop.Services;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>
/// A short message over the page's bottom-right corner (src/DnnManager.Presentation/Services/Toast.cs): what just happened,
/// a warning, an error - with a button when there is something to do about it. From any thread. A long message is
/// shortened to its first sentence, with Details for the whole of it.
/// </summary>
public static class Toast
{
    private static ToastView? _view;
    // Said before the window was there (the start's warnings) - shown once it is.
    private static List<ToastMessage>? _early = [];

    public static void Attach(ToastView view)
    {
        _view = view;
        var early = _early;
        _early = null;
        foreach (var message in early ?? []) view.Enqueue(message);
    }

    public static void Show(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(message, kind, actionText, action));
            return;
        }
        var shown = Summary(message);
        if (shown != message && action is null)
        {
            actionText = "Details";
            action = () => _ = Dialogs.MessageAsync(message);
        }
        var toast = new ToastMessage(shown, kind, actionText, action);
        if (_view is not null) _view.Enqueue(toast);
        else if (_early is { Count: < 20 } early) early.Add(toast);
    }

    public static void Hide() => _view?.Hide();

    private const int SummaryLength = 140;

    /// <summary>The message's first sentence - at most about 140 characters.</summary>
    internal static string Summary(string message)
    {
        var text = message.Trim();
        for (var i = 0; i + 2 < text.Length; i++)
            if (text[i] == '.' && text[i + 1] == ' ' && char.IsUpper(text[i + 2]))
            {
                text = text[..(i + 1)];
                break;
            }
        if (text.Length <= SummaryLength) return text;
        foreach (var separator in new[] { " - ", ": " })
            if (text.IndexOf(separator, StringComparison.Ordinal) is var at && at >= 20 && at <= SummaryLength)
                return text[..at] + ".";
        var space = text.LastIndexOf(' ', SummaryLength);
        return text[..(space > 20 ? space : SummaryLength)].TrimEnd(',', ';', ' ') + "…";
    }
}

/// <summary>A toast: its text, kind and its button. Info and success go by themselves; warnings and errors stay until closed.</summary>
public sealed record ToastMessage(string Text, ToastKind Kind, string? ActionText = null, Action? Action = null)
{
    public TimeSpan Duration => Kind is ToastKind.Success or ToastKind.Info ? TimeSpan.FromSeconds(Action is null ? 3 : 8) : TimeSpan.Zero;

    /// <summary>Goes by itself and asks nothing: a newer message may replace it.</summary>
    public bool Passing => Duration > TimeSpan.Zero && Action is null;

    public bool SameAs(ToastMessage other) => Text == other.Text && Kind == other.Kind && ActionText == other.ActionText;
}

/// <summary>
/// The toasts waiting their turn: one shows at a time; a message that asks for something waits until the one before is
/// closed - a passing one doesn't, it shows at once and the one it covers comes back after it. The same message twice
/// shows once.
/// </summary>
public sealed class ToastQueue
{
    private readonly LinkedList<ToastMessage> _waiting = new();

    public ToastMessage? Current { get; private set; }

    public int Waiting => _waiting.Count;

    /// <summary>Adds <paramref name="message"/>; returns what to show now - null when it waits (or is there already).</summary>
    public ToastMessage? Add(ToastMessage message)
    {
        if (Current is { } current && current.SameAs(message))
            return current.Passing ? Current = message : null;
        if (_waiting.Any(m => m.SameAs(message))) return null;
        if (Current is null || Current.Passing) return Current = message;
        if (message.Passing) _waiting.AddFirst(Current);
        else
        {
            _waiting.AddLast(message);
            return null;
        }
        return Current = message;
    }

    /// <summary>The current one closed: the next to show, if any.</summary>
    public ToastMessage? Next()
    {
        Current = null;
        if (_waiting.First is not { } first) return null;
        _waiting.RemoveFirst();
        return Current = first.Value;
    }
}
