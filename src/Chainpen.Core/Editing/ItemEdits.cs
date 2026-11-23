using System.Collections.Immutable;
using Chainpen.Core.Geometry;
using Chainpen.Core.Model;

namespace Chainpen.Core.Editing;

/// <summary>Changes to items. Each returns a new state; the caller records undo.</summary>
public static class ItemEdits
{
    /// <summary>Small and large keyboard nudge steps in points (FR-5.6).</summary>
    public const double SmallNudge = 1;
    public const double LargeNudge = 10;

    public const double SignatureMinWidth = 20;
    public const double SignatureMaxWidth = 600;

    public static DocumentState Add(DocumentState document, Item item)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.WithItems(document.Items.Add(item));
    }

    public static DocumentState Replace(DocumentState document, Item item)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(item);
        int index = document.Items.FindIndex(existing => existing.Id == item.Id);
        if (index < 0)
        {
            throw new ArgumentException($"Item {item.Id} isn't in the document.", nameof(item));
        }

        return document.WithItems(document.Items.SetItem(index, item));
    }

    public static DocumentState Remove(DocumentState document, IReadOnlySet<ItemId> ids)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        return document.WithItems(document.Items.RemoveAll(item => ids.Contains(item.Id)));
    }

    /// <summary>
    /// Moves every selected item by the same offset, limited so each stays on its page (FR-5.3).
    /// </summary>
    public static DocumentState MoveGroup(
        DocumentState document,
        IReadOnlySet<ItemId> ids,
        double dx,
        double dy,
        Func<PageId, PageSize> pageSize,
        ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(pageSize);
        var moving = document.Items.Where(item => ids.Contains(item.Id)).ToList();
        foreach (var group in moving.GroupBy(item => item.Page))
        {
            (dx, dy) = ItemGeometry.ClampGroupMove(group.Select(item => ItemGeometry.Bounds(item, measurer)), pageSize(group.Key), dx, dy);
        }

        if (dx == 0 && dy == 0)
        {
            return document;
        }

        double finalDx = dx, finalDy = dy;
        return document.WithItems(document.Items.ConvertAll(item => ids.Contains(item.Id) ? ItemGeometry.MoveBy(item, finalDx, finalDy) : item));
    }

    /// <summary>
    /// Resizes one item to a new box size from its top-left (FR-5.4). Signatures and drawings keep their proportions,
    /// shapes resize freely, text and marks change size.
    /// </summary>
    public static Item Resize(Item item, double width, double height, ITextMeasurer measurer)
    {
        const double MinSide = 4;
        width = Math.Max(MinSide, width);
        height = Math.Max(MinSide, height);
        switch (item)
        {
            case ShapeItem shape:
                return shape with { Width = width, Height = shape.IsLine && shape.Height == 0 ? 0 : height };

            case PictureItem picture:
            {
                double scale = Math.Max(width / picture.Width, height / picture.Height);
                return picture with { Width = picture.Width * scale, Height = picture.Height * scale };
            }

            case SignatureItem signature:
            {
                // The same limits as the size buttons, so "bigger" never shrinks a signature dragged past them.
                double scale = Math.Max(width / signature.Width, height / signature.Height);
                double clampedWidth = Math.Clamp(signature.Width * scale, SignatureMinWidth, SignatureMaxWidth);
                scale = clampedWidth / signature.Width;
                return signature with { Width = clampedWidth, Height = signature.Height * scale };
            }

            case InkItem ink:
            {
                double scale = Math.Max(ink.Width > 0 ? width / ink.Width : 0, ink.Height > 0 ? height / ink.Height : 0);
                return ink with { Width = ink.Width * scale, Height = ink.Height * scale };
            }

            case TextItem text:
            {
                PageSize size = ItemGeometry.TextSize(text, measurer);
                double fontSize = text.FontSize * height / size.Height;
                return text with { FontSize = Math.Round(Math.Clamp(fontSize, TextItem.MinFontSize, TextItem.MaxFontSize), 1) };
            }

            case MarkItem mark:
                return mark with { Size = Math.Clamp(Math.Max(width, height), MarkItem.MinSize, MarkItem.MaxSize) };

            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.GetType().Name, "Unknown item.");
        }
    }

    /// <summary>
    /// Resizes one item by dragging <paramref name="corner"/> of its box to <paramref name="dragTo"/>, keeping the
    /// opposite corner where it was. <paramref name="original"/> is the item as it was when the drag started.
    /// </summary>
    public static Item ResizeFromCorner(Item original, Corner corner, PagePoint dragTo, ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original is ShapeItem { IsLine: true } line)
        {
            return MoveLineEnd(line, corner, dragTo);
        }

        PageRect box = ItemGeometry.Bounds(original, measurer);
        bool isLeft = corner is Corner.TopLeft or Corner.BottomLeft;
        bool isTop = corner is Corner.TopLeft or Corner.TopRight;
        var anchor = new PagePoint(isLeft ? box.Right : box.X, isTop ? box.Bottom : box.Y);

        // The dragged corner can't cross the anchor: the item would have to flip.
        double width = isLeft ? anchor.X - dragTo.X : dragTo.X - anchor.X;
        double height = isTop ? anchor.Y - dragTo.Y : dragTo.Y - anchor.Y;
        Item resized = Resize(original, width, height, measurer);
        PageSize size = ItemGeometry.Size(resized, measurer);
        return ItemGeometry.MoveTo(resized, new PagePoint(isLeft ? anchor.X - size.Width : anchor.X, isTop ? anchor.Y - size.Height : anchor.Y));
    }

    /// <summary>
    /// Moves the end of a line at <paramref name="corner"/> to <paramref name="dragTo"/>; the other end stays put, so
    /// the line can swing all the way round it. An arrow keeps its head at the same end.
    /// </summary>
    private static ShapeItem MoveLineEnd(ShapeItem line, Corner corner, PagePoint dragTo)
    {
        var (startOffset, endOffset) = ItemPaths.LineEnds(line.Width, line.Height, line.FlipX, line.FlipY);
        PagePoint start = line.Position + startOffset;
        PagePoint end = line.Position + endOffset;
        return corner == ItemGeometry.LineEndCorners(line).Start
            ? ItemGeometry.WithLineEnds(line, dragTo, end)
            : ItemGeometry.WithLineEnds(line, start, dragTo);
    }

    /// <summary>Sets the colour of everything selected that has one (FR-5.5). Highlights and whiteout keep theirs.</summary>
    public static DocumentState SetColor(DocumentState document, IReadOnlySet<ItemId> ids, InkColor color)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        return document.WithItems(document.Items.ConvertAll(item => ids.Contains(item.Id) ? WithColor(item, color) : item));
    }

    private static Item WithColor(Item item, InkColor color) => item switch
    {
        TextItem text => text with { Color = color },
        MarkItem mark => mark with { Color = color },
        ShapeItem shape when !shape.IsFilled => shape with { Color = color },
        InkItem ink => ink with { Color = color },
        _ => item,
    };

    /// <summary>Sets bold and/or alignment on the text items among <paramref name="ids"/> (FS-952); null leaves it as it is.</summary>
    public static DocumentState SetTextStyle(DocumentState document, IReadOnlySet<ItemId> ids, bool? isBold, TextAlign? align)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        return document.WithItems(document.Items.ConvertAll(item => ids.Contains(item.Id) && item is TextItem text
            ? text with { IsBold = isBold ?? text.IsBold, Align = align ?? text.Align }
            : item));
    }

    /// <summary>Makes every selected item one step bigger (+1) or smaller (−1): font size, mark size or line width.</summary>
    public static DocumentState BumpSize(DocumentState document, IReadOnlySet<ItemId> ids, int direction)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        return document.WithItems(document.Items.ConvertAll(item => ids.Contains(item.Id) ? BumpSize(item, direction) : item));
    }

    public static Item BumpSize(Item item, int direction) => item switch
    {
        TextItem text => text with { FontSize = Math.Clamp(text.FontSize + direction, TextItem.MinFontSize, TextItem.MaxFontSize) },
        MarkItem mark => mark with { Size = Math.Clamp(mark.Size + (direction * 2), MarkItem.MinSize, MarkItem.MaxSize) },
        ShapeItem shape when !shape.IsFilled => shape with { StrokeWidth = BumpStroke(shape.StrokeWidth, direction) },
        InkItem ink => ink with { StrokeWidth = BumpStroke(ink.StrokeWidth, direction) },
        SignatureItem signature => ScaleSignature(signature, direction),
        PictureItem picture => ScalePicture(picture, direction),
        _ => item,
    };

    /// <summary>Signatures grow or shrink by a fifth per step and keep their proportions.</summary>
    /// <summary>Pictures grow or shrink by a fifth per step, keeping their proportions, from 20 pt to a page width.</summary>
    private static PictureItem ScalePicture(PictureItem picture, int direction)
    {
        const double Step = 1.2, MinWidth = 20, MaxWidth = 1200;
        double width = Math.Clamp(picture.Width * Math.Pow(Step, direction), MinWidth, MaxWidth);
        double scale = width / picture.Width;
        return picture with { Width = width, Height = picture.Height * scale };
    }

    private static SignatureItem ScaleSignature(SignatureItem signature, int direction)
    {
        const double Step = 1.2;
        double width = Math.Clamp(signature.Width * Math.Pow(Step, direction), SignatureMinWidth, SignatureMaxWidth);
        double scale = width / signature.Width;
        return signature with { Width = width, Height = signature.Height * scale };
    }


    // Lines of 2 pt and thinner change in half-point steps, so there's a choice below the 2 pt default.
    private static double BumpStroke(double width, int direction)
    {
        bool isThin = direction < 0 ? width <= 2 : width < 2;
        double step = isThin ? 0.5 : 1;
        return Math.Clamp(width + (direction * step), InkItem.MinStrokeWidth, InkItem.MaxStrokeWidth);
    }

    /// <summary>
    /// FS-957: copies the items on one page to every other page, so initials (or a date) can go on each page in one
    /// step. Each copy keeps its distance from the page edges it's nearest to, so initials in the bottom-right corner
    /// stay there on pages of another size, and stays on the page. The copies are listed in <paramref name="added"/>.
    /// </summary>
    public static DocumentState CopyToEveryPage(
        DocumentState document,
        IReadOnlySet<ItemId> ids,
        Func<PageId, PageSize> pageSize,
        ITextMeasurer measurer,
        out ImmutableArray<ItemId> added)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(pageSize);
        added = [];
        var selected = document.Items.Where(item => ids.Contains(item.Id)).ToList();
        if (selected.Count == 0)
        {
            return document;
        }

        // A selection spans one page in practice; if not, the first page's items are the ones copied.
        PageId source = selected[0].Page;
        var items = selected.Where(item => item.Page == source).ToList();
        PageRect group = Union(items.Select(item => ItemGeometry.Bounds(item, measurer)));
        PageSize from = pageSize(source);
        bool isNearRight = group.Centre.X > from.Width / 2;
        bool isNearBottom = group.Centre.Y > from.Height / 2;

        var copies = ImmutableArray.CreateBuilder<Item>();
        foreach (PageEntry page in document.Pages)
        {
            if (page.Id == source)
            {
                continue;
            }

            PageSize to = pageSize(page.Id);
            double x = isNearRight ? to.Width - (from.Width - group.X) : group.X;
            double y = isNearBottom ? to.Height - (from.Height - group.Y) : group.Y;
            PagePoint target = ItemGeometry.ClampToPage(group with { X = x, Y = y }, to);
            double dx = target.X - group.X, dy = target.Y - group.Y;
            copies.AddRange(CopiesOn(items, page.Id).Select(copy => ItemGeometry.MoveBy(copy, dx, dy)));
        }

        added = [.. copies.Select(copy => copy.Id)];
        return document.WithItems(document.Items.AddRange(copies));
    }

    private static PageRect Union(IEnumerable<PageRect> boxes)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (PageRect box in boxes)
        {
            left = Math.Min(left, box.X);
            top = Math.Min(top, box.Y);
            right = Math.Max(right, box.Right);
            bottom = Math.Max(bottom, box.Bottom);
        }

        return new PageRect(left, top, right - left, bottom - top);
    }

    /// <summary>Copies items with fresh ids onto a page.</summary>
    public static ImmutableArray<Item> CopiesOn(IEnumerable<Item> items, PageId page) =>
        [.. items.Select(item => item with { Id = ItemId.New(), Page = page })];
}
