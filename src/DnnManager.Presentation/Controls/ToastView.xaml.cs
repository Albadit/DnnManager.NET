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
        if (duration > TimeSpan.Zero) _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        if (!IsVisible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            // A new message may have arrived while fading.
            if (Opacity == 0) Visibility = Visibility.Collapsed;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        var action = _action;
        Hide();
        action?.Invoke();
    }
}
