using System.Reflection;

namespace Fillsign.Pdf.Fonts;

/// <summary>
/// The Noto fonts bundled with Fillsign (OFL). Screen and export use these same files, so text measures
/// and renders identically on every OS and never depends on installed fonts.
/// </summary>
public static class BundledFonts
{
    public const string FamilyName = "Noto Sans";
    public const string RegularFace = "NotoSans-Regular";
    public const string SymbolsFace = "NotoSansSymbols2-Regular";

    /// <summary>The raw font file.</summary>
    public static byte[] Load(string faceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Fillsign.Pdf.Fonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"Bundled font '{faceName}' is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
