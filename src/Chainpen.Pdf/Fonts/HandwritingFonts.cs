using System.Reflection;

namespace Chainpen.Pdf.Fonts;

/// <summary>The three styles offered for a typed signature (FR-4.2, D2).</summary>
public enum HandwritingStyle
{
    Caveat,
    DancingScript,
    Kalam,
}

/// <summary>
/// The bundled handwriting fonts (OFL, unmodified upstream files). The dialog previews a typed name in these with
/// Avalonia, and <see cref="Signatures.SignatureImageMaker"/> draws the placed image from the same files.
/// </summary>
public static class HandwritingFonts
{
    public static IReadOnlyList<HandwritingStyle> All { get; } = [HandwritingStyle.Caveat, HandwritingStyle.DancingScript, HandwritingStyle.Kalam];

    /// <summary>The family name inside the font file, used to find it among Avalonia's resources.</summary>
    public static string FamilyName(HandwritingStyle style) => style switch
    {
        HandwritingStyle.Caveat => "Caveat",
        HandwritingStyle.DancingScript => "Dancing Script",
        HandwritingStyle.Kalam => "Kalam",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown handwriting style."),
    };

    /// <summary>The weight of the bundled face, so the preview never asks for a synthetic bold.</summary>
    public static int Weight(HandwritingStyle style) => style switch
    {
        HandwritingStyle.Caveat => 700,
        HandwritingStyle.DancingScript => 600,
        HandwritingStyle.Kalam => 400,
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown handwriting style."),
    };

    public static string FileName(HandwritingStyle style) => style switch
    {
        HandwritingStyle.Caveat => "Caveat-Bold.ttf",
        HandwritingStyle.DancingScript => "DancingScript-SemiBold.ttf",
        HandwritingStyle.Kalam => "Kalam-Regular.ttf",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown handwriting style."),
    };

    /// <summary>The raw font file.</summary>
    public static byte[] Load(HandwritingStyle style)
    {
        string resource = $"Chainpen.Pdf.Fonts.Handwriting.{FileName(style)}";
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Bundled font '{resource}' is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
