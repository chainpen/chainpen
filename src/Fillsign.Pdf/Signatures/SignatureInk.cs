using Fillsign.Core.Model;

namespace Fillsign.Pdf.Signatures;

/// <summary>One pen sample on the signature pad, in pad units (DIPs). Pressure runs 0–1; a mouse reports 0.5.</summary>
public readonly record struct InkSample(double X, double Y, double Pressure);

/// <summary>
/// One piece of a drawn stroke: a quadratic curve from <see cref="Start"/> to <see cref="End"/> drawn at one width.
/// A dot has all three points equal.
/// </summary>
public readonly record struct InkSegment(PagePoint Start, PagePoint Control, PagePoint End, double Width);

/// <summary>
/// How drawn signatures look (FR-4.1). The pad draws these segments live and <see cref="SignatureImageMaker"/> draws
/// the same segments into the placed image, so what you sign is what lands on the page.
/// </summary>
public static class SignatureInk
{
    /// <summary>Stroke width at mouse pressure, in pad units.</summary>
    public const double BaseWidth = 2.6;

    /// <summary>Mice and trackpads report this pressure, which draws <see cref="BaseWidth"/>.</summary>
    public const double MousePressure = 0.5;

    /// <summary>Samples closer than this to the previous one add nothing but jitter, so the pad drops them.</summary>
    public const double MinSampleSpacing = 1.2;

    // Pressure is smoothed so a pen's noisy readings don't make the line lumpy.
    private const double PressureSmoothing = 0.35;

    public static IReadOnlyList<InkColor> Colors { get; } = [InkColor.Black, InkColor.Blue];

    /// <summary>Light pressure draws about half the base width and full pressure about one and a half times.</summary>
    public static double WidthFor(double pressure) => BaseWidth * (0.55 + (0.9 * Math.Clamp(pressure, 0, 1)));

    /// <summary>Curves through the midpoints between samples, each piece at the smoothed pressure of its sample.</summary>
    public static IReadOnlyList<InkSegment> Segments(IReadOnlyList<InkSample> stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        if (stroke.Count == 0)
        {
            return [];
        }

        double[] widths = SmoothedWidths(stroke);
        PagePoint[] points = [.. stroke.Select(sample => new PagePoint(sample.X, sample.Y))];
        if (points.Length == 1)
        {
            return [new InkSegment(points[0], points[0], points[0], widths[0])];
        }

        var segments = new List<InkSegment>(points.Length);
        PagePoint start = points[0];
        for (int i = 1; i < points.Length - 1; i++)
        {
            PagePoint mid = Midpoint(points[i], points[i + 1]);
            segments.Add(new InkSegment(start, points[i], mid, widths[i]));
            start = mid;
        }

        PagePoint last = points[^1];
        segments.Add(new InkSegment(start, Midpoint(start, last), last, widths[^1]));
        return segments;
    }

    private static double[] SmoothedWidths(IReadOnlyList<InkSample> stroke)
    {
        var widths = new double[stroke.Count];
        double pressure = stroke[0].Pressure;
        for (int i = 0; i < stroke.Count; i++)
        {
            pressure += (stroke[i].Pressure - pressure) * PressureSmoothing;
            widths[i] = WidthFor(pressure);
        }

        return widths;
    }

    private static PagePoint Midpoint(PagePoint a, PagePoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}
