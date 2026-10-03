using System.Windows;
using System.Windows.Controls;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Where the keyboard is, clearly: every control that would show Windows' faint dotted focus rectangle shows an accent
/// ring instead (the <c>FocusRing</c> style) - only when the keyboard moved there, never for a click, like VS Code's
/// focus outline. Controls whose own style draws their focus (text boxes, check boxes, switches) are left as they are.
/// </summary>
public static class FocusRing
{
    /// <summary>Once, before the window is made.</summary>
    public static void Install()
    {
        if (System.Windows.Application.Current?.TryFindResource("FocusRing") is not Style ring) return;
        EventManager.RegisterClassHandler(typeof(Control), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            // Only Windows' own: a style of the app that sets it (or turns it off) knows better.
            if (sender is Control { FocusVisualStyle: not null } control &&
                DependencyPropertyHelper.GetValueSource(control, FrameworkElement.FocusVisualStyleProperty).BaseValueSource == BaseValueSource.DefaultStyle)
                control.FocusVisualStyle = ring;
        }));
    }
}
