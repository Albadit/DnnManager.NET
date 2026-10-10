using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The toast in the main window (see <see cref="Toast"/>): one message at a time; what comes while one stays waits its
/// turn (<see cref="ToastQueue"/>) - "1 more" under the message says so - and shows when it is closed.
/// </summary>
public partial class ToastView : UserControl
{
    private readonly DispatcherTimer _timer = new();
    private readonly ToastQueue _queue = new();
    private Action? _action;
    // Shown (or still showing) while the window is minimized (EfficiencyMode): its time starts once the window is back -
    // a toast doesn't go away unseen.
    private bool _timeWaits;

    public ToastView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Hide();
        // Kept while the mouse is on it, so it doesn't fade away while being read.
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (_timer.Interval > TimeSpan.Zero && IsVisible) _timer.Start(); };
    }

    /// <summary>The toasts waiting their turn - for tests.</summary>
    internal ToastQueue Queue => _queue;

    /// <summary>Shows <paramref name="message"/> now - or after the one showing, when that one stays.</summary>
    public void Enqueue(ToastMessage message)
    {
        if (_queue.Add(message) is { } now) Present(now);
        else ShowWaiting();
    }

    private void Present(ToastMessage message)
    {
        _timer.Stop();
        Message.Text = message.Text;
        Glyph.SetResourceReference(TextBlock.TextProperty, message.Kind switch
        {
            ToastKind.Success => "GlyphCheck",
            ToastKind.Warning => "GlyphWarning",
            ToastKind.Error => "GlyphError",
            _ => "GlyphInfo"
        });
        Glyph.SetResourceReference(TextBlock.ForegroundProperty, message.Kind switch
        {
            ToastKind.Success => "SuccessText",
            ToastKind.Warning => "LogWarn",
            ToastKind.Error => "ErrorText",
            _ => "Accent"
        });

        _action = message.Action;
        ActionButton.Content = message.ActionText;
        ActionButton.Visibility = message.Action is null ? Visibility.Collapsed : Visibility.Visible;
        ShowWaiting();

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Visibility = Visibility.Visible;
        var duration = message.Duration;
        _timer.Interval = duration;
        _timeWaits = false;
        if (duration <= TimeSpan.Zero) return;
        if (EfficiencyMode.GetIsSaving(this)) _timeWaits = true;
        else _timer.Start();
    }

    /// <summary>"1 more" / "2 more" under the message while others wait - they show when this one is closed.</summary>
    private void ShowWaiting()
    {
        var waiting = _queue.Waiting;
        More.Text = waiting == 0 ? "" : $"{waiting} more - shown when you close this one";
        More.Visibility = waiting == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Closes the toast showing: the next one waiting shows - or, with none, it fades out.</summary>
    public void Hide()
    {
        _timer.Stop();
        _timeWaits = false;
        if (_queue.Next() is { } next)
        {
            Present(next);
            return;
        }
        if (!IsVisible) return;
        if (Motion.GetOff(this))
        {
            BeginAnimation(OpacityProperty, null);
            Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            // A new message may have arrived while fading.
            if (Opacity == 0) Visibility = Visibility.Collapsed;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // Minimized while counting down: the time stops. Restored: the toast gets its whole time again.
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != EfficiencyMode.IsSavingProperty) return;
        if ((bool)e.NewValue)
        {
            if (!_timer.IsEnabled) return;
            _timer.Stop();
            _timeWaits = true;
        }
        else if (_timeWaits)
        {
            _timeWaits = false;
            _timer.Start();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        var action = _action;
        Hide();
        action?.Invoke();
    }
}
