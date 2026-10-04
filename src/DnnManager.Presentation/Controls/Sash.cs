using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// A bar to drag between two parts of the window, as VS Code's sash: the cursor says which way it resizes, three dots in
/// its middle say it can be dragged, and it turns blue while it is dragged - or while <see cref="IsActive"/> (its corner
/// with another sash is). Its look is the Sash style (Themes/Controls/LayoutStyles); what a drag changes is its owner's.
/// </summary>
public sealed class Sash : Thumb
{
    /// <summary>Horizontal: a bar along the width, dragged up and down. Vertical: a bar along the height, dragged sideways.</summary>
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation),
        typeof(Orientation), typeof(Sash), new PropertyMetadata(Orientation.Vertical));

    /// <summary>Its three dots shown - off where only a line marks it (the Compact density).</summary>
    public static readonly DependencyProperty IsGripVisibleProperty = DependencyProperty.Register(nameof(IsGripVisible),
        typeof(bool), typeof(Sash), new PropertyMetadata(true));

    /// <summary>Drawn as dragged while a corner it meets is dragged.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive),
        typeof(bool), typeof(Sash), new PropertyMetadata(false));

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public bool IsGripVisible
    {
        get => (bool)GetValue(IsGripVisibleProperty);
        set => SetValue(IsGripVisibleProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }
}
