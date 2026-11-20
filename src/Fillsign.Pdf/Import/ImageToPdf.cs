using Fillsign.Pdf.Pdfium;
using SkiaSharp;

namespace Fillsign.Pdf.Import;

/// <summary>
/// Turns a photo or scan into one page (FR-1.2): turned the right way up from its EXIF orientation and scaled so its
/// long side matches A4's. Opaque images are stored as JPEG so a page of a phone photo stays a few hundred KB.
/// </summary>
public static class ImageToPdf
{
    /// <summary>A4's long side in points.</summary>
    public const double A4LongSide = 842;

    /// <summary>Pixel limit for the long side: 300 dpi across A4, plenty for a scan and far smaller than a raw photo.</summary>
    public const int MaxLongSidePixels = 3508;

    /// <summary>
    /// The most pixels decoded from a file (200 MB as BGRA): a header can claim a huge size in a few bytes, and PNG, GIF
    /// and BMP decode at full size before being shrunk.
    /// </summary>
    public const long MaxDecodePixels = 50_000_000;

    private const int JpegQuality = 88;

    /// <summary>Whether the bytes look like an image format Skia can decode.</summary>
    public static bool IsImage(ReadOnlySpan<byte> bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return codec is not null;
    }

    /// <summary>Adds the image as a new page at <paramref name="pageIndex"/>.</summary>
    /// <exception cref="InvalidDataException">The image can't be decoded.</exception>
    public static void AddImagePage(PdfiumDocument document, ReadOnlySpan<byte> imageFile, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        using SKBitmap upright = DecodeUpright(imageFile);
        double scale = A4LongSide / Math.Max(upright.Width, upright.Height);
        double width = upright.Width * scale, height = upright.Height * scale;
        document.AddBlankPage(pageIndex, width, height);

        bool isOpaque = upright.AlphaType == SKAlphaType.Opaque || IsFullyOpaque(upright);
        if (isOpaque)
        {
            using SKData jpeg = upright.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                ?? throw new InvalidDataException("The image could not be re-encoded.");
            document.EditPage(pageIndex, page => page.AddJpeg(jpeg.AsSpan(), 0, 0, width, height));
            return;
        }

        // PDFium wants straight alpha; decoded pixels are premultiplied, which would darken soft edges twice.
        using var straight = new SKBitmap(new SKImageInfo(upright.Width, upright.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        if (!upright.PeekPixels().ReadPixels(straight.Info, straight.GetPixels(), straight.RowBytes, 0, 0))
        {
            throw new InvalidDataException("The image could not be prepared.");
        }

        byte[] pixels = straight.Bytes;
        document.EditPage(pageIndex, page => page.AddImage(pixels, straight.Width, straight.Height, 0, 0, width, height));
    }

    /// <summary>Decodes, downsizes and applies the EXIF orientation. Returns BGRA premultiplied pixels.</summary>
    public static SKBitmap DecodeUpright(ReadOnlySpan<byte> imageFile)
    {
        using var data = SKData.CreateCopy(imageFile);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("This isn't an image format Fillsign can read.");
        SKEncodedOrigin origin = codec.EncodedOrigin;
        SKSizeI size = codec.Info.Size;

        // Let the codec downscale while decoding (cheap for JPEG), then finish with a high-quality resize.
        float limit = (float)MaxLongSidePixels / Math.Max(size.Width, size.Height);
        SKSizeI decodeSize = limit < 1 ? codec.GetScaledDimensions(limit) : size;

        // The size comes from the file, so check it before allocating: only some formats can shrink while decoding.
        if ((long)decodeSize.Width * decodeSize.Height > MaxDecodePixels)
        {
            throw new InvalidDataException($"This image is too large to open ({size.Width} × {size.Height} pixels).");
        }

        var info = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        SKCodecResult result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"The image could not be decoded ({result}).");
        }

        SKBitmap fitted = Fit(decoded);
        try
        {
            return Orient(fitted, origin);
        }
        finally
        {
            if (!ReferenceEquals(fitted, decoded))
            {
                fitted.Dispose();
            }
        }
    }

    private static SKBitmap Fit(SKBitmap bitmap)
    {
        int longSide = Math.Max(bitmap.Width, bitmap.Height);
        if (longSide <= MaxLongSidePixels)
        {
            return bitmap;
        }

        double scale = (double)MaxLongSidePixels / longSide;
        var target = new SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul);
        return bitmap.Resize(target, new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new InvalidDataException("The image could not be resized.");
    }

    /// <summary>Draws the stored pixels so the picture is the right way up (EXIF orientation 1–8).</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        int w = source.Width, h = source.Height;
        bool swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        // x' = ScaleX·x + SkewX·y + TransX, y' = SkewY·x + ScaleY·y + TransY.
        SKMatrix matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        var output = new SKBitmap(new SKImageInfo(swaps ? h : w, swaps ? w : h, SKColorType.Bgra8888, source.AlphaType));
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        return output;
    }

    private static bool IsFullyOpaque(SKBitmap bitmap)
    {
        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
            {
                return false;
            }
        }

        return true;
    }
}
