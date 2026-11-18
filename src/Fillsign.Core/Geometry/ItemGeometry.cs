using System.Collections.Immutable;
using Fillsign.Core.Model;

namespace Fillsign.Core.Geometry;

/// <summary>Sizes, hit boxes and placement rules for items.</summary>
public static class ItemGeometry
{
    /// <summary>A click (rather than a drag) is a movement smaller than this, in points.</summary>
    public const double ClickTolerance = 3;

    public const double DefaultShapeWidth = 80;
    public const double DefaultShapeHeight = 40;
    public const double DefaultHighlightHeight = 14;

    /// <summary>The item's bounding box in page space.</summary>
    public static PageRect Bounds(Item item, ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(measurer);
        PageSize size = Size(item, measurer);
        return new PageRect(item.X, item.Y, size.Width, size.Height);
    }

    public static PageSize Size(Item item, ITextMeasurer measurer) => item switch
    {
        TextItem text => TextSize(text, measurer),
        MarkItem mark => new PageSize(mark.Size, mark.Size),
        SignatureItem signature => new PageSize(signature.Width, signature.Height),
        ShapeItem shape => new PageSize(shape.Width, shape.Height),
        InkItem ink => new PageSize(ink.Width, ink.Height),
    };

    public static PageSize TextSize(TextItem text, ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measurer);
        string[] lines = text.Lines;
        double width = lines.Max(line => measurer.MeasureLineWidth(line, text.FontSize));
        return new PageSize(width, lines.Length * text.FontSize * TextItem.LineHeight);
    }

    /// <summary>
    /// Builds a shape from a drag between two page points. A click gives a default-sized shape; a default highlight
    /// is centred on a typical line of text.
    /// </summary>
    public static ShapeItem MakeShape(ItemId id, PageId page, ShapeKind kind, PagePoint from, PagePoint to, InkColor color, double strokeWidth)
    {
        double x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        bool isClick = Math.Abs(x1 - x0) < ClickTolerance && Math.Abs(y1 - y0) < ClickTolerance;
        if (isClick)
        {
            bool isLine = kind is ShapeKind.Line or ShapeKind.Arrow;
            x1 = x0 + DefaultShapeWidth;
            y1 = isLine ? y0 : y0 + DefaultShapeHeight;
            if (kind == ShapeKind.Highlight)
            {
                y0 -= DefaultHighlightHeight / 2;
                y1 = y0 + DefaultHighlightHeight;
            }
        }

        return new ShapeItem(
            id,
            page,
            Math.Min(x0, x1),
            Math.Min(y0, y1),
            kind,
            Math.Abs(x1 - x0),
            Math.Abs(y1 - y0),
            color,
            strokeWidth,
            FlipX: x1 < x0,
            FlipY: y1 < y0);
    }

    /// <summary>Builds an ink item from strokes in page coordinates.</summary>
    public static InkItem MakeInk(ItemId id, PageId page, IReadOnlyList<IReadOnlyList<PagePoint>> strokes, InkColor color, double strokeWidth)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var all = strokes.SelectMany(stroke => stroke).ToList();
        if (all.Count == 0)
        {
            throw new ArgumentException("A drawing needs at least one point.", nameof(strokes));
        }

        double minX = all.Min(p => p.X), minY = all.Min(p => p.Y);
        double width = all.Max(p => p.X) - minX, height = all.Max(p => p.Y) - minY;
        ImmutableArray<ImmutableArray<PagePoint>> normalised =
        [
            .. strokes.Select(stroke => stroke
                .Select(p => new PagePoint(width > 0 ? (p.X - minX) / width : 0, height > 0 ? (p.Y - minY) / height : 0))
                .ToImmutableArray()),
        ];
        return new InkItem(id, page, minX, minY, width, height, normalised, color, strokeWidth);
    }

    /// <summary>An ink item's strokes in page coordinates (used to append strokes or rotate the drawing).</summary>
    public static IReadOnlyList<IReadOnlyList<PagePoint>> InkToPageStrokes(InkItem ink)
    {
        ArgumentNullException.ThrowIfNull(ink);
        return [.. ink.Strokes.Select(stroke => (IReadOnlyList<PagePoint>)[.. stroke.Select(p => new PagePoint(ink.X + (p.X * ink.Width), ink.Y + (p.Y * ink.Height)))])];
    }

    /// <summary>Adds strokes (page coordinates) to an existing drawing, keeping its id.</summary>
    public static InkItem AppendStrokes(InkItem ink, IReadOnlyList<IReadOnlyList<PagePoint>> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var combined = InkToPageStrokes(ink).Concat(strokes).ToList();
        return MakeInk(ink.Id, ink.Page, combined, ink.Color, ink.StrokeWidth);
    }

    /// <summary>
    /// Moves an item along with its page when the page turns a quarter (FR-6.5). <paramref name="pageBefore"/> is the
    /// page's display size before turning. Shapes and drawings turn with the page; text, marks and signatures stay
    /// upright and keep their position relative to the page content (their centre turns).
    /// </summary>
    public static Item RotateWithPage(Item item, bool clockwise, PageSize pageBefore, ITextMeasurer measurer)
    {
        PagePoint Turn(PagePoint p) => clockwise
            ? new PagePoint(pageBefore.Height - p.Y, p.X)
            : new PagePoint(p.Y, pageBefore.Width - p.X);

        switch (item)
        {
            case InkItem ink:
            {
                var turned = InkToPageStrokes(ink).Select(stroke => (IReadOnlyList<PagePoint>)[.. stroke.Select(Turn)]).ToList();
                return MakeInk(ink.Id, ink.Page, turned, ink.Color, ink.StrokeWidth);
            }

            case ShapeItem shape:
            {
                var (start, end) = ItemPaths.LineEnds(shape.Width, shape.Height, shape.FlipX, shape.FlipY);
                PagePoint from = Turn(shape.Position + start);
                PagePoint to = Turn(shape.Position + end);
                return shape with
                {
                    X = Math.Min(from.X, to.X),
                    Y = Math.Min(from.Y, to.Y),
                    Width = Math.Abs(to.X - from.X),
                    Height = Math.Abs(to.Y - from.Y),
                    FlipX = to.X < from.X,
                    FlipY = to.Y < from.Y,
                };
            }

            default:
            {
                PageSize size = Size(item, measurer);
                PagePoint centre = Turn(new PagePoint(item.X + (size.Width / 2), item.Y + (size.Height / 2)));
                return MoveTo(item, new PagePoint(centre.X - (size.Width / 2), centre.Y - (size.Height / 2)));
            }
        }
    }

    public static Item MoveTo(Item item, PagePoint position)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item with { X = position.X, Y = position.Y };
    }

    public static Item MoveBy(Item item, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item with { X = item.X + dx, Y = item.Y + dy };
    }

    /// <summary>
    /// Limits a group move so every item stays on its page (FR-5.3). Returns the allowed offset.
    /// </summary>
    public static (double Dx, double Dy) ClampGroupMove(IEnumerable<PageRect> bounds, PageSize page, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        foreach (PageRect box in bounds)
        {
            // Items bigger than the page can't fit; keep their top-left on the page instead.
            double maxX = Math.Max(0, page.Width - box.Width);
            double maxY = Math.Max(0, page.Height - box.Height);
            dx = Math.Clamp(dx, -box.X, Math.Max(-box.X, maxX - box.X));
            dy = Math.Clamp(dy, -box.Y, Math.Max(-box.Y, maxY - box.Y));
        }

        return (dx, dy);
    }

    /// <summary>Keeps one item's box on its page.</summary>
    public static PagePoint ClampToPage(PageRect box, PageSize page) =>
        new(Math.Clamp(box.X, 0, Math.Max(0, page.Width - box.Width)), Math.Clamp(box.Y, 0, Math.Max(0, page.Height - box.Height)));
}
