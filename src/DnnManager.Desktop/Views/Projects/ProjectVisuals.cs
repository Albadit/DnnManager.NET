using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace DnnManager.Desktop.Views.Projects;

/// <summary>The converters the Projects page's templates use - the Windows app's DataTriggers.</summary>
public static class ProjectConverters
{
    public static readonly IValueConverter IsRunning = new FuncValueConverter<SiteRunState, bool>(s => s == SiteRunState.Running);
    public static readonly IValueConverter IsLive = new FuncValueConverter<string?, bool>(s => s == "Live");
    public static readonly IValueConverter IsOffline = new FuncValueConverter<string?, bool>(s => s == "Offline");
    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(b => !b);
}

/// <summary>
/// A theme brush by its key (Palette.axaml) - light or dark as the app is - looked up from <paramref name="from"/>.
/// </summary>
internal static class ThemeBrush
{
    public static IBrush Get(string key, Control? from = null)
    {
        var app = Avalonia.Application.Current;
        object? value = null;
        if (from is not null && from.TryFindResource(key, from.ActualThemeVariant, out var found)) value = found;
        else app?.TryGetResource(key, app.ActualThemeVariant, out value);
        return value as IBrush ?? Brushes.Gray;
    }

    public static Geometry? Geometry(string key)
    {
        var app = Avalonia.Application.Current;
        object? value = null;
        app?.TryGetResource(key, app.ActualThemeVariant, out value);
        return value as Geometry;
    }

    public static string Glyph(string key)
    {
        var app = Avalonia.Application.Current;
        object? value = null;
        app?.TryGetResource(key, app.ActualThemeVariant, out value);
        return value as string ?? "";
    }
}

/// <summary>
/// A site's keep-warm flame (the Windows app's KeepWarmFlame): an outline while off; filled while on - in the flame's
/// colour when warm or busy (pulsing while a request is on its way or due), grey while paused, with a red dot while
/// failing. Follows the <see cref="ProjectRow"/> it is given as DataContext.
/// </summary>
public sealed class KeepWarmFlame : Panel
{
    private readonly Avalonia.Controls.Shapes.Path _flame = new() { Stretch = Stretch.Uniform };
    private readonly Ellipse _dot = new()
    {
        Width = 6, Height = 6, Margin = new Thickness(0, 0, -2, -1), HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false
    };
    private ProjectRow? _row;

    public KeepWarmFlame()
    {
        Width = 14;
        Height = 14;
        Children.Add(_flame);
        Children.Add(_dot);
        ActualThemeVariantChanged += (_, _) => Show();
        AttachedToVisualTree += (_, _) => Show();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_row is not null) _row.PropertyChanged -= Row_PropertyChanged;
        _row = DataContext as ProjectRow;
        if (_row is not null && VisualRoot is not null) _row.PropertyChanged += Row_PropertyChanged;
        Show();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_row is not null)
        {
            _row.PropertyChanged -= Row_PropertyChanged;
            _row.PropertyChanged += Row_PropertyChanged;
        }
    }

    // Let go of the row when the flame isn't shown - a row lives as long as its site.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_row is not null) _row.PropertyChanged -= Row_PropertyChanged;
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectRow.KeepWarmLook) or nameof(ProjectRow.KeepWarm)) Show();
    }

    private void Show()
    {
        var look = _row?.KeepWarmLook ?? KeepWarmLook.Off;
        _flame.Data = ThemeBrush.Geometry(look == KeepWarmLook.Off ? "FlameOutline" : "FlameFilled");
        _flame.Fill = ThemeBrush.Get(look is KeepWarmLook.Warm or KeepWarmLook.Busy or KeepWarmLook.Problem ? "KeepWarmFg" : "TextMuted", this);
        _dot.Fill = ThemeBrush.Get("ErrorText", this);
        _dot.IsVisible = look == KeepWarmLook.Problem;
        Classes.Set("pulse", look == KeepWarmLook.Busy);
    }
}
