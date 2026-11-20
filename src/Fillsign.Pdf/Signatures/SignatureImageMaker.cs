using System.Collections.Immutable;
using Fillsign.Core.Model;
using Fillsign.Pdf.Fonts;
using HarfBuzzSharp;
using SkiaSharp;

namespace Fillsign.Pdf.Signatures;

/// <summary>
/// Makes the transparent PNG behind a signature or initials (FR-4.1–4.4): drawn ink, a typed name in a handwriting
/// font, or a photo with its paper removed. Every image is cropped to the signature itself.
/// </summary>
public static class SignatureImageMaker
{
    /// <summary>
    /// Drawn signatures are rendered at twice the pad's size: a signature placed 150 pt wide then has about 300 dpi.
    /// </summary>
    public const double InkScale = 2;

    /// <summary>Longest name that can be typed. Long enough for any name, short enough to keep the image small.</summary>
    public const int MaxTypedLength = 60;

    // Pixels with less coverage than this are anti-aliasing haze, not ink.
    private const byte TrimAlphaThreshold = 8;
    private const int TrimPadding = 4;
    private const float TypedFontSize = 120;

    /// <summary>The drawn strokes as an image, or null when nothing was drawn.</summary>
    public static SignatureImage? FromInk(IReadOnlyList<IReadOnlyList<InkSample>> strokes, InkColor color)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        List<IReadOnlyList<InkSegment>> segments = [.. strokes.Where(stroke => stroke.Count > 0).Select(SignatureInk.Segments)];
        if (segments.Count == 0)
        {
            return null;
        }

        IEnumerable<InkSegment> all = segments.SelectMany(stroke => stroke);
        double margin = all.Max(segment => segment.Width) + 2;
        double left = all.Min(s => Math.Min(s.Start.X, Math.Min(s.Control.X, s.End.X))) - margin;
        double top = all.Min(s => Math.Min(s.Start.Y, Math.Min(s.Control.Y, s.End.Y))) - margin;
        double right = all.Max(s => Math.Max(s.Start.X, Math.Max(s.Control.X, s.End.X))) + margin;
        double bottom = all.Max(s => Math.Max(s.Start.Y, Math.Max(s.Control.Y, s.End.Y))) + margin;

        int width = (int)Math.Ceiling((right - left) * InkScale);
        int height = (int)Math.Ceiling((bottom - top) * InkScale);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)InkScale);
            canvas.Translate((float)-left, (float)-top);
            using var paint = new SKPaint
            {
                Color = ToSkia(color),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            foreach (InkSegment segment in segments.SelectMany(stroke => stroke))
            {
                paint.StrokeWidth = (float)segment.Width;
                using var path = new SKPath();
                path.MoveTo((float)segment.Start.X, (float)segment.Start.Y);
                path.QuadTo((float)segment.Control.X, (float)segment.Control.Y, (float)segment.End.X, (float)segment.End.Y);
                canvas.DrawPath(path, paint);
            }
        }

        return TrimToImage(bitmap);
    }

    /// <summary>A typed name in a handwriting style, or null when the text is blank.</summary>
    public static SignatureImage? FromText(string text, HandwritingStyle style, InkColor color)
    {
        ArgumentNullException.ThrowIfNull(text);
        string name = text.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        if (name.Length > MaxTypedLength)
        {
            name = name[..MaxTypedLength];
        }

        byte[] fontFile = HandwritingFonts.Load(style);
        (ushort[] glyphs, SKPoint[] positions, float advance) = Shape(fontFile, name, TypedFontSize);

        // Script fonts swash well past their advances, so leave generous room and crop afterwards.
        float room = TypedFontSize;
        int width = (int)Math.Ceiling(advance + (2 * room));
        int height = (int)Math.Ceiling(TypedFontSize * 2.4f);
        float baseline = TypedFontSize * 1.5f;

        using var data = SKData.CreateCopy(fontFile);
        using SKTypeface typeface = SKTypeface.FromData(data) ?? throw new InvalidOperationException($"The {style} font could not be loaded.");
        using var font = new SKFont(typeface, TypedFontSize) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var builder = new SKTextBlobBuilder();
        SKPositionedRunBuffer run = builder.AllocatePositionedRun(font, glyphs.Length);
        glyphs.CopyTo(run.Glyphs);
        positions.CopyTo(run.Positions);
        using SKTextBlob blob = builder.Build() ?? throw new InvalidOperationException("The typed signature could not be laid out.");

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = ToSkia(color), IsAntialias = true };
            canvas.DrawText(blob, room, baseline, paint);
        }

        return TrimToImage(bitmap);
    }

    /// <summary>Encodes a bitmap cropped to its visible pixels, or null when it has none.</summary>
    internal static SignatureImage? TrimToImage(SKBitmap bitmap)
    {
        SKRectI? bounds = VisibleBounds(bitmap);
        if (bounds is not { } box)
        {
            return null;
        }

        using var cropped = new SKBitmap(new SKImageInfo(box.Width, box.Height, bitmap.ColorType, bitmap.AlphaType));
        if (!bitmap.ExtractSubset(cropped, box))
        {
            throw new InvalidOperationException("The signature could not be cropped.");
        }

        return Encode(cropped);
    }

    internal static SignatureImage Encode(SKBitmap bitmap)
    {
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("The signature could not be saved as PNG.");
        return new SignatureImage(Guid.NewGuid(), ImmutableArray.Create(png.AsSpan()), bitmap.Width, bitmap.Height);
    }

    /// <summary>The smallest box holding every pixel with real coverage, plus a little padding.</summary>
    private static SKRectI? VisibleBounds(SKBitmap bitmap)
    {
        if (bitmap.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888))
        {
            throw new ArgumentException("Expected 32-bit pixels.", nameof(bitmap));
        }

        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                if (row[(x * 4) + 3] <= TrimAlphaThreshold)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < 0)
        {
            return null;
        }

        return new SKRectI(
            Math.Max(0, minX - TrimPadding),
            Math.Max(0, minY - TrimPadding),
            Math.Min(width, maxX + 1 + TrimPadding),
            Math.Min(height, maxY + 1 + TrimPadding));
    }

    /// <summary>Shapes with HarfBuzz so the handwriting fonts' kerning and joins apply.</summary>
    private static (ushort[] Glyphs, SKPoint[] Positions, float Advance) Shape(byte[] fontFile, string text, float fontSize)
    {
        using Blob blob = NativeFontBlob.Create(fontFile);
        using var face = new Face(blob, 0);
        using var font = new HarfBuzzSharp.Font(face);
        int unitsPerEm = face.UnitsPerEm;
        font.SetScale(unitsPerEm, unitsPerEm);

        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        font.Shape(buffer);

        GlyphInfo[] infos = buffer.GlyphInfos;
        GlyphPosition[] placements = buffer.GlyphPositions;
        float scale = fontSize / unitsPerEm;
        var glyphs = new ushort[infos.Length];
        var positions = new SKPoint[infos.Length];
        float penX = 0;
        for (int i = 0; i < infos.Length; i++)
        {
            glyphs[i] = (ushort)infos[i].Codepoint;
            // HarfBuzz measures y upwards; Skia downwards.
            positions[i] = new SKPoint(penX + (placements[i].XOffset * scale), -placements[i].YOffset * scale);
            penX += placements[i].XAdvance * scale;
        }

        return (glyphs, positions, penX);
    }

    internal static SKColor ToSkia(InkColor color) => new(color.R, color.G, color.B);
}
