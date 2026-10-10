using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// A line under a form that says what is wrong (the <c>ErrorLine</c> style) or what is still missing - told to a screen
/// reader too, which otherwise never hears it (WCAG 3.3.1, 4.1.3):
/// <list type="bullet">
/// <item>while it shows, it is the help text of the field it is about (<see cref="ForProperty"/>) - read with the field;</item>
/// <item>once it has stood still for a moment (typing changes it with every key), it is said - once, until it changes.</item>
/// </list>
/// The <c>ErrorLine</c> style binds <see cref="MessageProperty"/> to the text; another text block can do the same.
/// </summary>
public static class FieldError
{
    /// <summary>How long a message has to stay the same before it is said: about a pause in typing.</summary>
    internal static TimeSpan Settle { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>The text block's text - bound by the <c>ErrorLine</c> style.</summary>
    public static readonly DependencyProperty MessageProperty = DependencyProperty.RegisterAttached("Message", typeof(string),
        typeof(FieldError), new PropertyMetadata(null, (d, _) => Changed(d)));

    public static string? GetMessage(DependencyObject d) => (string?)d.GetValue(MessageProperty);
    public static void SetMessage(DependencyObject d, string? value) => d.SetValue(MessageProperty, value);

    /// <summary>
    /// The text block's Visibility - bound by the <c>ErrorLine</c> style: a line shown and hidden with its text already set
    /// changes like its text (IsVisible doesn't change while its page or dialog isn't on screen).
    /// </summary>
    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(Visibility),
        typeof(FieldError), new PropertyMetadata(Visibility.Visible, (d, _) => Changed(d)));

    public static Visibility GetShown(DependencyObject d) => (Visibility)d.GetValue(ShownProperty);
    public static void SetShown(DependencyObject d, Visibility value) => d.SetValue(ShownProperty, value);

    /// <summary>The field the message is about: it carries the message as its help text while the message shows.</summary>
    public static readonly DependencyProperty ForProperty = DependencyProperty.RegisterAttached("For", typeof(UIElement),
        typeof(FieldError), new PropertyMetadata(null, OnForChanged));

    public static UIElement? GetFor(DependencyObject d) => (UIElement?)d.GetValue(ForProperty);
    public static void SetFor(DependencyObject d, UIElement? value) => d.SetValue(ForProperty, value);

    /// <summary>
    /// An error interrupts what the screen reader is saying (true, the default); a note - "Still needed: …" - waits for it.
    /// </summary>
    public static readonly DependencyProperty ImportantProperty = DependencyProperty.RegisterAttached("Important", typeof(bool),
        typeof(FieldError), new PropertyMetadata(true));

    public static bool GetImportant(DependencyObject d) => (bool)d.GetValue(ImportantProperty);
    public static void SetImportant(DependencyObject d, bool value) => d.SetValue(ImportantProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State),
        typeof(FieldError), new PropertyMetadata(null));

    /// <summary>What a text block has said, and the timer that waits for its message to settle.</summary>
    private sealed class State
    {
        public DispatcherTimer? Timer;
        public string? Said;
        public UIElement? Field;
        public bool FieldHadHelp;
        public object? FieldHelp;
        // When it came on screen: a note (not Important) is said when it changes - not what it says as its form opens.
        public DateTime ShownAt = DateTime.UtcNow;
    }

    /// <summary>What the line shows now - empty when it is hidden or blank.</summary>
    internal static string Shown(FrameworkElement line) =>
        line.Visibility == Visibility.Visible && GetMessage(line) is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : "";

    private static State StateOf(FrameworkElement line)
    {
        if (line.GetValue(StateProperty) is State state) return state;
        state = new State();
        line.SetValue(StateProperty, state);
        // Shown and hidden by Visibility, with the text set before: the same as a change of the text.
        line.IsVisibleChanged += (_, _) =>
        {
            if (line.IsVisible) state.ShownAt = DateTime.UtcNow;
            Changed(line);
        };
        return state;
    }

    private static void OnForChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement line) return;
        var state = StateOf(line);
        Restore(state);
        Changed(line);
    }

    private static void Changed(DependencyObject d)
    {
        if (d is not FrameworkElement line) return;
        var state = StateOf(line);
        var message = Shown(line);
        ShowOnField(line, state, message);

        if (message.Length == 0)
        {
            // Gone: said again when it comes back.
            state.Timer?.Stop();
            state.Said = null;
            return;
        }
        if (message == state.Said) return;
        if (state.Timer is null)
        {
            state.Timer = new DispatcherTimer(DispatcherPriority.Background, line.Dispatcher) { Interval = Settle };
            state.Timer.Tick += (_, _) =>
            {
                state.Timer.Stop();
                Say(line, state);
            };
        }
        state.Timer.Stop();
        state.Timer.Interval = Settle;
        state.Timer.Start();
    }

    /// <summary>The message stood still: said, if it still shows and wasn't said yet.</summary>
    private static void Say(FrameworkElement line, State state)
    {
        var message = Shown(line);
        if (message.Length == 0 || message == state.Said || !line.IsVisible) return;
        state.Said = message;
        var important = GetImportant(line);
        if (!important && DateTime.UtcNow - state.ShownAt < Settle + Settle) return;
        Toast.Announce(line, message, important);
    }

    /// <summary>The field's help text is the message while it shows; what it was before, after.</summary>
    private static void ShowOnField(FrameworkElement line, State state, string message)
    {
        var field = GetFor(line);
        if (message.Length == 0 || field is null)
        {
            Restore(state);
            return;
        }
        if (!ReferenceEquals(state.Field, field))
        {
            Restore(state);
            state.Field = field;
            var local = field.ReadLocalValue(AutomationProperties.HelpTextProperty);
            state.FieldHadHelp = local != DependencyProperty.UnsetValue;
            state.FieldHelp = local;
        }
        field.SetValue(AutomationProperties.HelpTextProperty, message);
    }

    private static void Restore(State state)
    {
        if (state.Field is not { } field) return;
        if (state.FieldHadHelp && state.FieldHelp is string help) field.SetValue(AutomationProperties.HelpTextProperty, help);
        else field.ClearValue(AutomationProperties.HelpTextProperty);
        state.Field = null;
        state.FieldHadHelp = false;
        state.FieldHelp = null;
    }
}
