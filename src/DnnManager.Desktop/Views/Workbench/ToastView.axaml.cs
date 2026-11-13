using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DnnManager.Desktop.Services;

namespace DnnManager.Desktop.Views.Workbench;

/// <summary>Shows the toasts one at a time (<see cref="ToastQueue"/>), each for its time - kept while the mouse is on it.</summary>
public partial class ToastView : UserControl
{
    private readonly DispatcherTimer _timer = new();
    private readonly ToastQueue _queue = new();
    private Action? _action;
    private int _shown;

    public ToastView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Hide();
        PointerEntered += (_, _) => _timer.Stop();
        PointerExited += (_, _) => { if (_timer.Interval > TimeSpan.Zero && IsVisible) _timer.Start(); };
    }

    internal ToastQueue Queue => _queue;

    /// <summary>The message shown now, if any - what the tests and the tour read.</summary>
    internal string? Text => IsVisible ? Message.Text : null;

    public void Enqueue(ToastMessage message)
    {
        if (_queue.Add(message) is { } now) Present(now);
        else ShowWaiting();
    }

    private void Present(ToastMessage message)
    {
        _timer.Stop();
        Message.Text = message.Text;
        Glyph.Text = Resource<string>(message.Kind switch
        {
            ToastKind.Success => "GlyphCheck", ToastKind.Warning => "GlyphWarning", ToastKind.Error => "GlyphError", _ => "GlyphInfo"
        });
        Mark.Text = message.Kind switch { ToastKind.Success => "✓", ToastKind.Warning => "!", ToastKind.Error => "✕", _ => "i" };
        var brush = message.Kind switch { ToastKind.Success => "SuccessText", ToastKind.Warning => "LogWarn", ToastKind.Error => "ErrorText", _ => "Accent" };
        Glyph.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));
        Mark.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));

        _action = message.Action;
        ActionButton.Content = message.ActionText;
        ToolTip.SetTip(ActionButton, message.ActionText);
        ActionButton.IsVisible = message.Action is not null;
        ShowWaiting();

        _shown++;
        IsVisible = true;
        Opacity = 1;
        _timer.Interval = message.Duration;
        if (message.Duration > TimeSpan.Zero) _timer.Start();
    }

    private T? Resource<T>(string key) where T : class =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as T : null;

    private void ShowWaiting()
    {
        var waiting = _queue.Waiting;
        More.Text = waiting == 0 ? "" : $"{waiting} more - shown when you close this one";
        More.IsVisible = waiting > 0;
    }

    /// <summary>Closes the toast shown - the next one waiting shows; none: it fades out.</summary>
    public async void Hide()
    {
        _timer.Stop();
        if (_queue.Next() is { } next)
        {
            Present(next);
            return;
        }
        if (!IsVisible) return;
        var shown = _shown;
        Opacity = 0;
        await Task.Delay(260);
        // A new one may have come while it faded.
        if (shown == _shown) IsVisible = false;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Hide();

    private void Action_Click(object? sender, RoutedEventArgs e)
    {
        var action = _action;
        Hide();
        action?.Invoke();
    }
}
