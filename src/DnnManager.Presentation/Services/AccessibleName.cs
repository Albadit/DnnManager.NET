using System.Globalization;
using System.Windows.Data;

namespace DnnManager.Presentation.Services;

/// <summary>
/// The name a screen reader says for a control that only shows an icon: the first part of its tooltip ("Restart"),
/// not the whole sentence that explains it ("Restart the website's background process - can fix problems; …") - that
/// goes in its help text. Fixed text: nothing is raised to UI Automation listeners when it is read.
/// </summary>
public static class AccessibleName
{
    // Where a tooltip's explanation starts, after the name of what the control does.
    private static readonly string[] Breaks = [" - ", "; ", " (", ": ", ". ", "…", "\n"];

    /// <summary>The short name in a converter: a string's <see cref="Of"/>, anything else (a tooltip control) none.</summary>
    public static IValueConverter Short { get; } = new ShortConverter();

    /// <summary>"Restart the website's background process - can fix problems" → "Restart the website's background process".</summary>
    public static string Of(string text)
    {
        var end = Breaks.Select(b => text.IndexOf(b, StringComparison.Ordinal)).Where(i => i > 0).DefaultIfEmpty(text.Length).Min();
        return text[..end].Trim().TrimEnd('.', ':', ',');
    }

    private sealed class ShortConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string text ? Of(text) : null;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
