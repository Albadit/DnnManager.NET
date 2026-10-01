using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>The toast in the main window (see <see cref="Toast"/>): one message at a time, the newest replacing the last.</summary>
public partial class ToastView : UserControl
{
    private readonly DispatcherTimer _timer = new();
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

    /// <param name="duration">How long it stays; <see cref="TimeSpan.Zero"/> keeps it until closed or replaced.</param>
    public void Show(string message, ToastKind kind, TimeSpan duration, string? actionText, Action? action)
    {
        _timer.Stop();
        Message.Text = message;
        Glyph.Text = kind switch
        {
            ToastKind.Success => "", // check mark
            ToastKind.Warning => "", // warning
            ToastKind.Error => "",   // error badge
            _ => ""                  // info
        };
        Glyph.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            ToastKind.Success => "SuccessText",
            ToastKind.Warning => "LogWarn",
            ToastKind.Error => "ErrorText",
            _ => "Accent"
        });

        _action = action;
        ActionButton.Content = actionText;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Visibility = Visibility.Visible;
        _timer.Interval = duration;
        _timeWaits = false;
        if (duration <= TimeSpan.Zero) return;
        if (EfficiencyMode.GetIsSaving(this)) _timeWaits = true;
        else _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        _timeWaits = false;
        if (!IsVisible) return;
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
