using System.Collections.Immutable;
using System.Text;
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

        // The handwriting styles draw Latin, Cyrillic and Devanagari names. A name they can't draw (Japanese, Chinese,
        // Korean, Arabic) is drawn whole in a font that has it, rather than as empty boxes.
        byte[] fontFile = HandwritingFonts.Load(style);
        int faceIndex = 0;
        string fontName = $"The {style} font";
        if (FirstMissing(fontFile, name) is { } missing && FontWithAll(name, missing) is { } fallback)
        {
            (fontFile, faceIndex) = fallback;
            fontName = "The installed font chosen for this name";
        }

        (ushort[] glyphs, SKPoint[] positions, float advance) = Shape(fontFile, faceIndex, name, TypedFontSize);

        // Script fonts swash well past their advances, so leave generous room and crop afterwards.
        float room = TypedFontSize;
        int width = (int)Math.Ceiling(advance + (2 * room));
        int height = (int)Math.Ceiling(TypedFontSize * 2.4f);
        float baseline = TypedFontSize * 1.5f;

        using var data = SKData.CreateCopy(fontFile);
        using SKTypeface typeface = SKTypeface.FromData(data, faceIndex) ?? throw new InvalidOperationException($"{fontName} could not be loaded.");
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

    /// <summary>The first character of <paramref name="text"/> the font has no glyph for, or null when it has them all.</summary>
    private static int? FirstMissing(byte[] fontFile, string text)
    {
        using var data = SKData.CreateCopy(fontFile);
        using SKTypeface? typeface = SKTypeface.FromData(data);
        if (typeface is null)
        {
            return null;
        }

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && !typeface.ContainsGlyph(rune.Value))
            {
                return rune.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// An installed font that has every character of <paramref name="text"/> and can be loaded from its file: the
    /// system's pick for the first missing one (asking for Japanese forms when the name has kana or the interface is
    /// Japanese, as Chinese fonts draw some kanji differently), else the first family that has them all. The signature
    /// becomes a picture, so any font will do, embeddable or not.
    /// </summary>
    internal static (byte[] File, int FaceIndex)? FontWithAll(string text, int missing)
    {
        int[] needed = [.. text.EnumerateRunes().Where(rune => !Rune.IsWhiteSpace(rune)).Select(rune => rune.Value).Distinct()];
        bool hasKana = needed.Any(codepoint => codepoint is >= 0x3040 and <= 0x30FF);
        string language = hasKana ? "ja" : System.Globalization.CultureInfo.CurrentUICulture.Name;
        (byte[] File, int FaceIndex)? Usable(SKTypeface? typeface) =>
            typeface is not null && needed.All(typeface.ContainsGlyph) && Bytes(typeface) is { } found && Loads(found) ? found : null;

        using (SKTypeface? match = SKFontManager.Default.MatchCharacter(null, SKFontStyle.Normal, [language], missing))
        {
            if (Usable(match) is { } matched)
            {
                return matched;
            }
        }

        foreach (string family in SKFontManager.Default.FontFamilies.Order(StringComparer.Ordinal))
        {
            using SKTypeface? candidate = SKFontManager.Default.MatchFamily(family);
            if (Usable(candidate) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static (byte[] File, int FaceIndex)? Bytes(SKTypeface typeface)
    {
        using SKStreamAsset? stream = typeface.OpenStream(out int faceIndex);
        if (stream is not { Length: > 0 })
        {
            return null;
        }

        var bytes = new byte[stream.Length];
        return stream.Read(bytes, bytes.Length) == bytes.Length ? (bytes, faceIndex) : null;
    }

    // Some system fonts (macOS's PingFang) can't be rebuilt from their file, which drawing the signature needs.
    private static bool Loads((byte[] File, int FaceIndex) font)
    {
        using var data = SKData.CreateCopy(font.File);
        using SKTypeface? typeface = SKTypeface.FromData(data, font.FaceIndex);
        return typeface is not null;
    }

    /// <summary>Shapes with HarfBuzz so the handwriting fonts' kerning and joins apply.</summary>
    private static (ushort[] Glyphs, SKPoint[] Positions, float Advance) Shape(byte[] fontFile, int faceIndex, string text, float fontSize)
    {
        using Blob blob = NativeFontBlob.Create(fontFile);
        using var face = new Face(blob, faceIndex);
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
