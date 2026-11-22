using System.Globalization;

namespace Fillsign.Pdf.Localization;

/// <summary>Fills in translated templates ("{0} can’t be opened") with values, in the interface's language.</summary>
internal static class Text
{
    public static string Format(string template, params object?[] args) => string.Format(CultureInfo.CurrentCulture, template, args);
}
