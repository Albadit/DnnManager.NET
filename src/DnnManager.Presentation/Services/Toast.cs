using System.Windows.Automation;
using System.Windows.Automation.Peers;
using DnnManager.Presentation.Controls;
using Microsoft.Extensions.Logging;

namespace DnnManager.Presentation.Services;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>
/// A short message popping up over the bottom-right of the page and fading out by itself - for feedback on
/// something that happened out of view, like a setting saved. Warnings and errors - and a message with an action - stay
/// until closed; one that comes meanwhile waits its turn (<see cref="ToastQueue"/>), so none of them is lost.
/// </summary>
public static class Toast
{
    private static ToastView? _view;
    // Shown before the window could show them (start-up): shown once it can.
    private static List<ToastMessage>? _early = [];

    /// <summary>Called by the main window with the view it shows toasts in.</summary>
    public static void Attach(ToastView view)
    {
        _view = view;
        var early = _early;
        _early = null;
        foreach (var message in early ?? []) view.Enqueue(message);
    }

    /// <summary>
    /// Shows <paramref name="message"/> - its first sentence, when it says more: all of it goes to the log file, and behind
    /// a Details button unless the toast offers something else (Show output, where it is in full too).
    /// </summary>
    public static void Show(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
        var shown = Summary(message);
        if (shown != message)
        {
            App.Log?.Log(kind switch
            {
                ToastKind.Error => LogLevel.Error,
                ToastKind.Warning => LogLevel.Warning,
                _ => LogLevel.Information
            }, "{Message}", message);
            if (action is null)
            {
                actionText = "Details";
                action = () => MessageDialog.Warn(message);
            }
        }
        var toast = new ToastMessage(shown, kind, actionText, action);
        if (_view is not null) _view.Enqueue(toast);
        else if (_early is { Count: < 20 } early) early.Add(toast);
        Announce(_view, shown, kind is ToastKind.Error or ToastKind.Warning);
    }

    // About two lines of a toast.
    private const int SummaryLength = 140;

    /// <summary>
    /// What a toast shows of <paramref name="message"/>: its first sentence, and of a long one the part before its first
    /// " - " or ": " - or its first words, with "…". The whole message when it is short already.
    /// </summary>
    internal static string Summary(string message)
    {
        var text = message.Trim();
        // A sentence ends at ". " before a capital letter - not at the dot in a file name or a version.
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

    /// <summary>
    /// Says <paramref name="message"/> to a screen reader (UI Automation's notification) - an error or a warning before
    /// what it is reading. One event, and only while something listens: nothing is walked or raised otherwise.
    /// </summary>
    public static void Announce(System.Windows.UIElement? source, string message, bool important = false)
    {
        try
        {
            if (source is null || !AutomationPeer.ListenerExists(AutomationEvents.Notification)) return;
            var peer = UIElementAutomationPeer.FromElement(source) ?? UIElementAutomationPeer.CreatePeerForElement(source);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
                important ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
                message, "DnnManager.Status");
        }
        catch (InvalidOperationException) { /* no window to say it from yet */ }
    }

    /// <summary>Closes the toast showing; the next one waiting, if any, shows instead.</summary>
    public static void Hide() => _view?.Hide();
}

/// <summary>One toast: what it says, how it went, and what its button does.</summary>
public sealed record ToastMessage(string Text, ToastKind Kind, string? ActionText = null, Action? Action = null)
{
    /// <summary>
    /// How long it stays: a success or a note goes by itself (longer with a button to press); something that needs
    /// attention waits for the user - <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public TimeSpan Duration => Kind is ToastKind.Success or ToastKind.Info ? TimeSpan.FromSeconds(Action is null ? 3 : 8) : TimeSpan.Zero;

    /// <summary>Goes by itself and offers nothing to press - "Settings saved": the next toast may take its place.</summary>
    public bool Passing => Duration > TimeSpan.Zero && Action is null;

    /// <summary>The same toast - shown twice, it shows once.</summary>
    public bool SameAs(ToastMessage other) => Text == other.Text && Kind == other.Kind && ActionText == other.ActionText;
}

/// <summary>
/// Which toast shows, and which wait: one at a time, and nothing that matters is lost - start-up can bring several at
/// once (the sign-in task, the install location, the update's result).
/// <list type="bullet">
/// <item>A passing one (<see cref="ToastMessage.Passing"/>) showing gives way to the next at once - as before.</item>
/// <item>A passing one coming while another stays shows at once; the other comes back after it.</item>
/// <item>Otherwise the new one waits its turn: it shows when the one before is closed (or has timed out).</item>
/// <item>The same toast showing or waiting isn't added again.</item>
/// </list>
/// </summary>
public sealed class ToastQueue
{
    private readonly LinkedList<ToastMessage> _waiting = new();

    /// <summary>The toast showing; null when none is.</summary>
    public ToastMessage? Current { get; private set; }

    /// <summary>How many wait to be shown.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>A toast came: the one to show now - or null, when the one showing stays and it waits.</summary>
    public ToastMessage? Add(ToastMessage message)
    {
        if (Current is { } current && current.SameAs(message))
            // Again: a passing one gets its time again; one that stays stays as it is.
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

    /// <summary>The toast showing was closed (or went by itself): the next to show, null when none waits.</summary>
    public ToastMessage? Next()
    {
        Current = null;
        if (_waiting.First is not { } first) return null;
        _waiting.RemoveFirst();
        return Current = first.Value;
    }
}
