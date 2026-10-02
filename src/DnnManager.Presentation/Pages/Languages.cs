using System.Globalization;

namespace DnnManager.Presentation.Pages;

/// <summary>DNN's languages as the pages list them.</summary>
internal static class Languages
{
    /// <summary>"Deutsch (de-DE)" - the language's own name and its code; the code alone when Windows doesn't know it.</summary>
    public static string Name(string culture)
    {
        try
        {
            var info = CultureInfo.GetCultureInfo(culture);
            return $"{char.ToUpper(info.NativeName[0])}{info.NativeName[1..]} ({culture})";
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }
}
