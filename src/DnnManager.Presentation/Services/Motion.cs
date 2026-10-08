using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Whether DNN Manager animates: the panel and the sidebar sliding, toasts fading out, menus and lists fading in,
/// switches sliding, and the pulsing state dots, flames, spinners and progress bars. Off with Settings → General →
/// Animations - and while Windows' own animation effects are off (Settings → Accessibility → Visual effects), as other
/// apps do. Off, everything changes at once; nothing else changes.
///
/// <para>Code asks <see cref="Enabled"/>. What animates in XAML follows <see cref="OffProperty"/>: set on the main window
/// (<see cref="Track"/>) and inherited by everything in it, menus and drop-downs too.</para>
/// </summary>
public static class Motion
{
    private static bool _setting = true;

    static Motion() =>
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Changed?.Invoke();
        };

    /// <summary>Animations play: the setting is on and Windows' animation effects are too.</summary>
    public static bool Enabled => _setting && SystemParameters.ClientAreaAnimation;

    /// <summary><see cref="Enabled"/> may have changed.</summary>
    public static event Action? Changed;

    /// <summary>The setting (appearance.animations) - at start and when the settings are saved.</summary>
    public static void Apply(bool animations)
    {
        if (_setting == animations) return;
        _setting = animations;
        Changed?.Invoke();
    }

    public static readonly DependencyProperty OffProperty = DependencyProperty.RegisterAttached("Off", typeof(bool), typeof(Motion),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetOff(DependencyObject element) => (bool)element.GetValue(OffProperty);

    public static void SetOff(DependencyObject element, bool value) => element.SetValue(OffProperty, value);

    /// <summary><paramref name="window"/> and everything in it follow <see cref="Enabled"/>.</summary>
    public static void Track(Window window)
    {
        void Set() => SetOff(window, !Enabled);
        Set();
        Changed += () => window.Dispatcher.BeginInvoke(Set);
    }

    /// <summary>A popup's animation: <see cref="PopupAnimation.Fade"/>, or none while <see cref="OffProperty"/> is set.</summary>
    public static IValueConverter PopupFade { get; } = new PopupFadeConverter();

    private sealed class PopupFadeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? PopupAnimation.None : PopupAnimation.Fade;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

/// <summary>
/// A control template's visual states without their transitions while <see cref="Motion.OffProperty"/> is set - a
/// switch's knob is then where it goes at once (set as the template root's VisualStateManager.CustomVisualStateManager).
/// </summary>
public sealed class MotionStates : VisualStateManager
{
    // A custom manager is asked for every state the control goes to - also those the template doesn't have ("Normal",
    // "MouseOver"…): those come without a group and a state, and are ignored, as the default manager ignores them.
    protected override bool GoToStateCore(FrameworkElement control, FrameworkElement stateGroupsRoot, string stateName,
        VisualStateGroup group, VisualState state, bool useTransitions) =>
        group is not null && state is not null &&
        base.GoToStateCore(control, stateGroupsRoot, stateName, group, state, useTransitions && !Motion.GetOff(control ?? stateGroupsRoot));
}
