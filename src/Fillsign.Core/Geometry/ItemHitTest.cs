using Fillsign.Core.Model;

namespace Fillsign.Core.Geometry;

/// <summary>
/// Which item is under a point. Anything under the point wins over an outlined rectangle or ellipse around it, so
/// what sits inside can still be selected (FR-3.7); a click on empty space inside the outline grabs the shape.
/// Drawings and lines are only grabbed by their strokes.
/// </summary>
public static class ItemHitTest
{
    /// <summary>The topmost item on <paramref name="page"/> under <paramref name="point"/>, or null.</summary>
    /// <param name="tolerance">Extra reach in points, so thin lines can be grabbed at any zoom.</param>
    public static Item? TopmostAt(DocumentState document, PageId page, PagePoint point, double tolerance, ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(measurer);

        // Later items are drawn on top, so they're hit first.
        for (int i = document.Items.Count - 1; i >= 0; i--)
        {
            Item item = document.Items[i];
            if (item.Page == page && Hits(item, point, tolerance, measurer))
            {
                return item;
            }
        }

        for (int i = document.Items.Count - 1; i >= 0; i--)
        {
            if (document.Items[i] is ShapeItem { Kind: ShapeKind.Rectangle or ShapeKind.Ellipse } outline
                && outline.Page == page
                && new PageRect(outline.X, outline.Y, outline.Width, outline.Height).Contains(point))
            {
                return outline;
            }
        }

        return null;
    }

    public static bool Hits(Item item, PagePoint point, double tolerance, ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(item);
        PagePoint local = point - item.Position;
        switch (item)
        {
            case ShapeItem { IsFilled: true } filled:
                return Inflate(new PageRect(0, 0, filled.Width, filled.Height), tolerance).Contains(local);

            case ShapeItem { Kind: ShapeKind.Rectangle } rectangle:
                return DistanceToRectangleEdge(local, rectangle.Width, rectangle.Height) <= tolerance + (rectangle.StrokeWidth / 2);

            case ShapeItem { Kind: ShapeKind.Ellipse } ellipse:
                return DistanceToEllipse(local, ellipse.Width, ellipse.Height) <= tolerance + (ellipse.StrokeWidth / 2);

            case ShapeItem line:
            {
                var (start, end) = ItemPaths.LineEnds(line.Width, line.Height, line.FlipX, line.FlipY);
                return DistanceToSegment(local, start, end) <= tolerance + (line.StrokeWidth / 2);
            }

            case InkItem ink:
                return DistanceToInk(local, ink) <= tolerance + (ink.StrokeWidth / 2);

            case TextItem or MarkItem or SignatureItem or PictureItem:
                return Inflate(ItemGeometry.Bounds(item, measurer), tolerance).Contains(point);

            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.GetType().Name, "Unknown item.");
        }
    }

    private static PageRect Inflate(PageRect box, double by) => new(box.X - by, box.Y - by, box.Width + (2 * by), box.Height + (2 * by));

    private static double DistanceToRectangleEdge(PagePoint p, double width, double height)
    {
        PagePoint[] corners = [new(0, 0), new(width, 0), new(width, height), new(0, height)];
        double best = double.MaxValue;
        for (int i = 0; i < corners.Length; i++)
        {
            best = Math.Min(best, DistanceToSegment(p, corners[i], corners[(i + 1) % corners.Length]));
        }

        return best;
    }

    /// <summary>Approximate distance to the outline of the ellipse inscribed in the box, measured from its centre.</summary>
    private static double DistanceToEllipse(PagePoint p, double width, double height)
    {
        double rx = width / 2, ry = height / 2;
        if (rx <= 0 || ry <= 0)
        {
            return DistanceToSegment(p, new PagePoint(0, 0), new PagePoint(width, height));
        }

        double dx = p.X - rx, dy = p.Y - ry;
        double radial = Math.Sqrt((dx * dx) + (dy * dy));
        double normalised = Math.Sqrt((dx * dx / (rx * rx)) + (dy * dy / (ry * ry)));
        if (normalised == 0)
        {
            return Math.Min(rx, ry);
        }

        // The outline crosses the ray from the centre through p at radial / normalised.
        return Math.Abs(radial - (radial / normalised));
    }

    private static double DistanceToInk(PagePoint p, InkItem ink)
    {
        double best = double.MaxValue;
        foreach (var stroke in ink.Strokes)
        {
            PagePoint? previous = null;
            foreach (PagePoint fraction in stroke)
            {
                var point = new PagePoint(fraction.X * ink.Width, fraction.Y * ink.Height);
                best = Math.Min(best, DistanceToSegment(p, previous ?? point, point));
                previous = point;
            }
        }

        return best;
    }

    private static double DistanceToSegment(PagePoint p, PagePoint a, PagePoint b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double lengthSquared = (vx * vx) + (vy * vy);
        double t = lengthSquared == 0 ? 0 : Math.Clamp((((p.X - a.X) * vx) + ((p.Y - a.Y) * vy)) / lengthSquared, 0, 1);
        double cx = a.X + (t * vx) - p.X, cy = a.Y + (t * vy) - p.Y;
        return Math.Sqrt((cx * cx) + (cy * cy));
    }
}
