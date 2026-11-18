using Fillsign.Core.Model;

namespace Fillsign.Core.Geometry;

/// <summary>Vector paths for marks, shapes and ink, shared by the screen and the export.</summary>
public static class ItemPaths
{
    /// <summary>Marks are designed in a 24×24 box and scaled to the item's size.</summary>
    public const double MarkDesignSize = 24;

    /// <summary>Stroke width of a mark in the 24×24 design box.</summary>
    public const double MarkDesignStroke = 3;

    /// <summary>Highlight colour and opacity (drawn with Multiply).</summary>
    public static readonly InkColor HighlightColor = new(0xff, 0xd6, 0x0a);
    public const double HighlightOpacity = 0.45;

    // Control-point distance for a quarter ellipse drawn with a cubic Bézier.
    private const double Kappa = 0.5523;

    private static readonly (PagePoint From, PagePoint To)[] s_tick =
    [
        (new(4, 12.5), new(9.5, 18)),
        (new(9.5, 18), new(20, 6)),
    ];

    private static readonly (PagePoint From, PagePoint To)[] s_cross =
    [
        (new(5, 5), new(19, 19)),
        (new(19, 5), new(5, 19)),
    ];

    /// <summary>The mark's strokes, scaled to its size; draw with round caps and <see cref="MarkStrokeWidth"/>.</summary>
    public static IReadOnlyList<PathCommand> MarkPath(MarkItem mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        double k = mark.Size / MarkDesignSize;
        var segments = mark.Kind == MarkKind.Tick ? s_tick : s_cross;
        var path = new List<PathCommand>();
        foreach (var (from, to) in segments)
        {
            path.Add(new MoveTo(new PagePoint(from.X * k, from.Y * k)));
            path.Add(new LineTo(new PagePoint(to.X * k, to.Y * k)));
        }

        return path;
    }

    public static double MarkStrokeWidth(MarkItem mark) => MarkDesignStroke * mark.Size / MarkDesignSize;

    /// <summary>The start and end of a line or arrow inside its box.</summary>
    public static (PagePoint Start, PagePoint End) LineEnds(double width, double height, bool flipX, bool flipY) =>
        (new PagePoint(flipX ? width : 0, flipY ? height : 0), new PagePoint(flipX ? 0 : width, flipY ? 0 : height));

    public static IReadOnlyList<PathCommand> ShapePath(ShapeItem shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        double w = shape.Width;
        double h = shape.Height;
        switch (shape.Kind)
        {
            case ShapeKind.Rectangle or ShapeKind.Highlight or ShapeKind.Whiteout:
                return [new MoveTo(new(0, 0)), new LineTo(new(w, 0)), new LineTo(new(w, h)), new LineTo(new(0, h)), ClosePath.Instance];

            case ShapeKind.Ellipse:
            {
                double rx = w / 2, ry = h / 2, kx = rx * Kappa, ky = ry * Kappa;
                return
                [
                    new MoveTo(new(rx, 0)),
                    new CubicTo(new(rx + kx, 0), new(w, ry - ky), new(w, ry)),
                    new CubicTo(new(w, ry + ky), new(rx + kx, h), new(rx, h)),
                    new CubicTo(new(rx - kx, h), new(0, ry + ky), new(0, ry)),
                    new CubicTo(new(0, ry - ky), new(rx - kx, 0), new(rx, 0)),
                    ClosePath.Instance,
                ];
            }

            case ShapeKind.Line:
            {
                var (start, end) = LineEnds(w, h, shape.FlipX, shape.FlipY);
                return [new MoveTo(start), new LineTo(end)];
            }

            case ShapeKind.Arrow:
            {
                var (start, end) = LineEnds(w, h, shape.FlipX, shape.FlipY);
                double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                double length = Math.Max(8, shape.StrokeWidth * 4);
                double wing = Math.PI / 180 * 28;
                PagePoint Head(double delta) =>
                    new(end.X - (length * Math.Cos(angle + delta)), end.Y - (length * Math.Sin(angle + delta)));
                return [new MoveTo(start), new LineTo(end), new MoveTo(Head(wing)), new LineTo(end), new LineTo(Head(-wing))];
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape.Kind, "Unknown shape.");
        }
    }

    /// <summary>Smooths pen strokes by curving through the midpoints between samples.</summary>
    public static IReadOnlyList<PathCommand> InkPath(InkItem ink)
    {
        ArgumentNullException.ThrowIfNull(ink);
        var path = new List<PathCommand>();
        foreach (var stroke in ink.Strokes)
        {
            if (stroke.IsEmpty)
            {
                continue;
            }

            var points = stroke.Select(p => new PagePoint(p.X * ink.Width, p.Y * ink.Height)).ToArray();
            path.Add(new MoveTo(points[0]));
            if (points.Length == 1)
            {
                path.Add(new LineTo(points[0])); // A dot: round caps make it visible.
                continue;
            }

            for (int i = 1; i < points.Length - 1; i++)
            {
                var mid = new PagePoint((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
                path.Add(new QuadTo(points[i], mid));
            }

            path.Add(new LineTo(points[^1]));
        }

        return path;
    }
}
