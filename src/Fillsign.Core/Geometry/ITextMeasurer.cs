using Fillsign.Core.Model;

namespace Fillsign.Core.Geometry;

/// <summary>
/// Measures a line of text in the bundled font. The app supplies one backed by the real font; Core can't load fonts.
/// </summary>
public interface ITextMeasurer
{
    /// <summary>Width of one line, in points, without kerning (the export doesn't kern).</summary>
    double MeasureLineWidth(string line, double fontSize);
}

/// <summary>Average-glyph estimate for tests and for code paths without a font.</summary>
public sealed class ApproximateTextMeasurer : ITextMeasurer
{
    public static readonly ApproximateTextMeasurer Instance = new();

    private const double AverageGlyphWidth = 0.55;

    public double MeasureLineWidth(string line, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Length * fontSize * AverageGlyphWidth;
    }
}
