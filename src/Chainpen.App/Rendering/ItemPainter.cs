using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Chainpen.Core.Geometry;
using Chainpen.Core.Model;
using CorePathCommand = Chainpen.Core.Geometry.PathCommand;

namespace Chainpen.App.Rendering;

/// <summary>
/// Draws items on a page in page points (push a page→view transform first). Uses the same path lists as the export
/// (ItemPaths) and the bundled Noto font with kerning off, so the screen matches the exported file (NFR-7).
/// </summary>
public sealed class ItemPainter
{
    /// <summary>The bundled Noto Sans, the same file the export embeds.</summary>
    public static readonly FontFamily NotoSans = new("avares://Chainpen.App/Assets/Fonts#Noto Sans");

    /// <summary>The export writes unkerned text, so the screen must not kern either (Phase 0, FS-023).</summary>
    public static readonly FontFeatureCollection NoKerning = [FontFeature.Parse("-kern")];

    private static readonly Typeface s_typeface = new(NotoSans);

    // Items are immutable, so a geometry built for one instance stays right for it.
    private readonly ConditionalWeakTable<Item, Geometry> _geometry = [];
    private readonly Dictionary<Guid, Bitmap> _signatures = [];

    public void Draw(DrawingContext context, Item item, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        if (opacity < 1)
        {
            using (context.PushOpacity(opacity))
            {
                DrawItem(context, item);
            }

            return;
        }

        DrawItem(context, item);
    }

    private void DrawItem(DrawingContext context, Item item)
    {
        switch (item)
        {
            case TextItem text:
                DrawText(context, text);
                break;

            case MarkItem mark:
                context.DrawGeometry(null, StrokePen(mark.Color, ItemPaths.MarkStrokeWidth(mark)), GeometryOf(mark));
                break;

            case ShapeItem { Kind: ShapeKind.Highlight } highlight:
            {
                // The export multiplies the yellow onto the page; on screen the same see-through yellow reads the same.
                var brush = new SolidColorBrush(ToColor(ItemPaths.HighlightColor), ItemPaths.HighlightOpacity);
                context.DrawGeometry(brush, null, GeometryOf(highlight));
                break;
            }

            case ShapeItem { Kind: ShapeKind.Whiteout } whiteout:
                context.DrawGeometry(Brushes.White, null, GeometryOf(whiteout));
                break;

            case ShapeItem shape:
                context.DrawGeometry(null, StrokePen(shape.Color, shape.StrokeWidth), GeometryOf(shape));
                break;

            case InkItem ink:
                context.DrawGeometry(null, StrokePen(ink.Color, ink.StrokeWidth), GeometryOf(ink));
                break;

            case SignatureItem signature:
                DrawImage(context, signature.Image, new Rect(signature.X, signature.Y, signature.Width, signature.Height));
                break;

            case PictureItem picture:
                DrawImage(context, picture.Image, new Rect(picture.X, picture.Y, picture.Width, picture.Height));
                break;
        }
    }

    private void DrawImage(DrawingContext context, SignatureImage image, Rect box)
    {
        if (SignatureBitmap(image) is { } bitmap)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                context.DrawImage(bitmap, box);
            }
        }
    }

    /// <summary>One line of text, its top at <paramref name="top"/>. Baselines sit at top + ascent, as in the export.</summary>
    public static FormattedText LineText(string line, double fontSize, IBrush brush)
    {
        var text = new FormattedText(line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, s_typeface, fontSize, brush);
        text.SetFontFeatures(NoKerning);
        return text;
    }

    public static Color ToColor(InkColor colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    /// <summary>Frees the decoded signature images; they're decoded again if drawn later.</summary>
    public void ReleaseImages()
    {
        foreach (Bitmap bitmap in _signatures.Values)
        {
            bitmap.Dispose();
        }

        _signatures.Clear();
    }

    private static void DrawText(DrawingContext context, TextItem text)
    {
        var brush = new SolidColorBrush(ToColor(text.Color));
        string[] lines = text.Lines;
        double[] offsets = ItemGeometry.LineOffsets(text, Pdf.Fonts.NotoTextMeasurer.Instance);
        double lineHeight = text.FontSize * TextItem.LineHeight;

        // Bold is the regular glyphs with an outline, exactly as the final copy draws them (FS-952).
        Pen? bold = text.IsBold ? new Pen(brush, text.FontSize * TextItem.BoldStroke, lineJoin: PenLineJoin.Round) : null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            var origin = new Point(text.X + offsets[i], text.Y + (i * lineHeight));
            FormattedText line = LineText(lines[i], text.FontSize, brush);
            if (bold is null)
            {
                context.DrawText(line, origin);
            }
            else if (line.BuildGeometry(origin) is { } outline)
            {
                context.DrawGeometry(brush, bold, outline);
            }
        }
    }

    private static Pen StrokePen(InkColor colour, double width) =>
        new(new SolidColorBrush(ToColor(colour)), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private Geometry GeometryOf(Item item) => _geometry.GetValue(item, static item => Build(item));

    private static StreamGeometry Build(Item item)
    {
        IReadOnlyList<CorePathCommand> path = item switch
        {
            MarkItem mark => ItemPaths.MarkPath(mark),
            ShapeItem shape => ItemPaths.ShapePath(shape),
            InkItem ink => ItemPaths.InkPath(ink),
            _ => [],
        };

        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            bool isOpen = false;
            foreach (CorePathCommand command in path)
            {
                switch (command)
                {
                    case MoveTo move:
                        if (isOpen)
                        {
                            g.EndFigure(false);
                        }

                        g.BeginFigure(P(item, move.To), isFilled: item is ShapeItem { IsFilled: true });
                        isOpen = true;
                        break;
                    case LineTo line:
                        g.LineTo(P(item, line.To));
                        break;
                    case QuadTo quad:
                        g.QuadraticBezierTo(P(item, quad.Control), P(item, quad.To));
                        break;
                    case CubicTo cubic:
                        g.CubicBezierTo(P(item, cubic.Control1), P(item, cubic.Control2), P(item, cubic.To));
                        break;
                    case ClosePath:
                        g.EndFigure(true);
                        isOpen = false;
                        break;
                }
            }

            if (isOpen)
            {
                g.EndFigure(false);
            }
        }

        return geometry;
    }

    // Paths are item-local; place them on the page.
    private static Point P(Item item, PagePoint p) => new(item.X + p.X, item.Y + p.Y);

    private Bitmap? SignatureBitmap(SignatureImage image)
    {
        if (_signatures.TryGetValue(image.Id, out Bitmap? bitmap))
        {
            return bitmap;
        }

        try
        {
            using var stream = new MemoryStream(image.Png.ToArray());
            bitmap = new Bitmap(stream);
        }
        catch (ArgumentException)
        {
            // A damaged image draws nothing rather than failing the whole page; its box still shows when selected.
            return null;
        }

        _signatures[image.Id] = bitmap;
        return bitmap;
    }
}
