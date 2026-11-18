using Fillsign.Core.Geometry;
using HarfBuzzSharp;

namespace Fillsign.Pdf.Fonts;

/// <summary>
/// Measures text in the bundled Noto Sans with HarfBuzz, kerning off, exactly as the export lays it out. The screen
/// draws text with kerning off too (Phase 0, FS-023), so boxes, hit tests and the exported file agree.
/// </summary>
public sealed class NotoTextMeasurer : ITextMeasurer, IDisposable
{
    public static readonly NotoTextMeasurer Instance = new();

    // Widths are cached per line at 1 pt and scaled; HarfBuzz advances are linear in the font size.
    private const int MaxCachedLines = 4096;

    private readonly Lock _lock = new();
    private readonly Blob _blob;
    private readonly Face _face;
    private readonly Font _font;
    private readonly Feature[] _features = [new Feature(new Tag('k', 'e', 'r', 'n'), 0)];
    private readonly Dictionary<string, double> _widthAtOnePoint = new(StringComparer.Ordinal);

    private NotoTextMeasurer()
    {
        byte[] bytes = BundledFonts.Load(BundledFonts.RegularFace);
        _blob = Blob.FromStream(new MemoryStream(bytes));
        _face = new Face(_blob, 0);
        _font = new Font(_face);
        UnitsPerEm = _face.UnitsPerEm;
        _font.SetScale(UnitsPerEm, UnitsPerEm);
    }

    public int UnitsPerEm { get; }

    public double MeasureLineWidth(string line, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            if (!_widthAtOnePoint.TryGetValue(line, out double width))
            {
                width = Shape(line);
                if (_widthAtOnePoint.Count >= MaxCachedLines)
                {
                    _widthAtOnePoint.Clear();
                }

                _widthAtOnePoint[line] = width;
            }

            return width * fontSize;
        }
    }

    public void Dispose()
    {
        _font.Dispose();
        _face.Dispose();
        _blob.Dispose();
    }

    private double Shape(string line)
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(line);
        buffer.GuessSegmentProperties();
        _font.Shape(buffer, _features);
        long total = 0;
        foreach (GlyphPosition position in buffer.GlyphPositions)
        {
            total += position.XAdvance;
        }

        return total / (double)UnitsPerEm;
    }
}
