using System.Globalization;

namespace Fillsign.App.Localization;

/// <summary>
/// Fills in translated templates. Every sentence is a whole string in <see cref="Strings"/> with numbered
/// placeholders ("Move {0} pages"), never glued from pieces, so each language can order its words its own way.
/// </summary>
public static class Text
{
    /// <summary>A template with its placeholders filled in, numbers written the interface language's way.</summary>
    public static string Format(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, template, args);

    /// <summary>
    /// The singular or plural template for <paramref name="count"/>, filled in with it as {0}. Languages without a
    /// separate singular (Japanese) give the same text for both.
    /// </summary>
    public static string Plural(int count, string one, string other) => Format(count == 1 ? one : other, count);
}
