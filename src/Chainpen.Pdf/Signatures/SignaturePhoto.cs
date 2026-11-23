using System.Collections.Immutable;
using Chainpen.Core.Model;
using Chainpen.Pdf.Import;
using SkiaSharp;

namespace Chainpen.Pdf.Signatures;

/// <summary>
/// A photo or scan of a signature (FR-4.3), decoded once so the dialog can redraw the preview as the person ticks or
/// unticks "Remove the paper background".
/// </summary>
public sealed class SignaturePhoto : IDisposable
{
    /// <summary>A signature never needs more than this across; it keeps a phone photo's PNG to a few hundred KB.</summary>
    public const int MaxLongSidePixels = 1200;

    // Paper brightness is estimated per block, so a shadow across part of the photo doesn't turn into ink.
    private const int BlockSize = 48;
    private const double PaperPercentile = 0.9;

    // Relative to the local paper: darker than InkRatio is solid ink, lighter than PaperRatio is paper, and the
    // range between fades, which keeps pen edges soft.
    private const double InkRatio = 0.55;
    private const double PaperRatio = 0.8;

    private readonly SKBitmap _opaque;

    private SignaturePhoto(SKBitmap opaque, ImmutableArray<byte> originalPng)
    {
        _opaque = opaque;
        OriginalPng = originalPng;
    }

    /// <summary>The photo as decoded and turned upright, for the "Your photo" preview.</summary>
    public ImmutableArray<byte> OriginalPng { get; }

    public int PixelWidth => _opaque.Width;

    public int PixelHeight => _opaque.Height;

    /// <summary>Decodes the file, turns it upright and shrinks it.</summary>
    /// <exception cref="InvalidDataException">The file isn't an image Chainpen can read.</exception>
    public static SignaturePhoto Load(ReadOnlySpan<byte> imageFile)
    {
        using SKBitmap upright = ImageToPdf.DecodeUpright(imageFile);
        double scale = Math.Min(1, (double)MaxLongSidePixels / Math.Max(upright.Width, upright.Height));
        int width = Math.Max(1, (int)Math.Round(upright.Width * scale));
        int height = Math.Max(1, (int)Math.Round(upright.Height * scale));

        // Transparent areas of a PNG count as paper, so draw the photo onto white.
        var opaque = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(opaque))
        {
            canvas.Clear(SKColors.White);
            using SKImage image = SKImage.FromBitmap(upright);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using SKData png = opaque.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("The image could not be re-encoded.");
        return new SignaturePhoto(opaque, ImmutableArray.Create(png.AsSpan()));
    }

    /// <summary>
    /// The signature to place: with the paper made transparent and cropped to the ink, or the whole photo as it is.
    /// Null when removing the paper leaves nothing, for example a blank or very faint photo.
    /// </summary>
    public SignatureImage? MakeSignature(bool removeBackground)
    {
        if (!removeBackground)
        {
            return SignatureImageMaker.Encode(_opaque);
        }

        using SKBitmap ink = RemovePaper(_opaque);
        return SignatureImageMaker.TrimToImage(ink);
    }

    public void Dispose() => _opaque.Dispose();

    /// <summary>
    /// Makes paper transparent. Each pixel is compared with the paper around it, and its colour is divided by that
    /// paper's colour, so ink keeps its hue but loses the photo's lighting cast.
    /// </summary>
    internal static SKBitmap RemovePaper(SKBitmap opaque)
    {
        int width = opaque.Width, height = opaque.Height;
        ReadOnlySpan<byte> source = opaque.GetPixelSpan();
        int stride = opaque.RowBytes;
        var luminance = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * stride) + (x * 4);
                luminance[(y * width) + x] = Luminance(source[i + 2], source[i + 1], source[i]);
            }
        }

        PaperMap paper = PaperMap.Estimate(luminance, width, height);
        var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Span<byte> target = output.GetPixelSpan();
        int targetStride = output.RowBytes;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double paperLevel = paper.At(x, y);
                double ratio = luminance[(y * width) + x] / paperLevel;
                double coverage = Math.Clamp((PaperRatio - ratio) / (PaperRatio - InkRatio), 0, 1);
                int i = (y * stride) + (x * 4);
                int o = (y * targetStride) + (x * 4);
                double whiteBalance = 255 / paperLevel;
                target[o] = ClampToByte(source[i] * whiteBalance);
                target[o + 1] = ClampToByte(source[i + 1] * whiteBalance);
                target[o + 2] = ClampToByte(source[i + 2] * whiteBalance);
                target[o + 3] = ClampToByte(coverage * 255);
            }
        }

        return output;
    }

    private static byte Luminance(byte r, byte g, byte b) => (byte)(((299 * r) + (587 * g) + (114 * b)) / 1000);

    private static byte ClampToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    /// <summary>Paper brightness across the photo: a bright percentile per block, blended between block centres.</summary>
    private sealed class PaperMap
    {
        private readonly double[] _levels;
        private readonly int _columns;
        private readonly int _rows;

        private PaperMap(double[] levels, int columns, int rows)
        {
            _levels = levels;
            _columns = columns;
            _rows = rows;
        }

        public static PaperMap Estimate(byte[] luminance, int width, int height)
        {
            int columns = Math.Max(1, (int)Math.Ceiling(width / (double)BlockSize));
            int rows = Math.Max(1, (int)Math.Ceiling(height / (double)BlockSize));
            double overall = Percentile(luminance, width, 0, 0, width, height);

            // A block that is mostly ink (a heavy stroke) would otherwise call the ink "paper".
            double floor = Math.Max(1, overall * 0.75);
            var levels = new double[columns * rows];
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    int x0 = column * BlockSize, y0 = row * BlockSize;
                    double level = Percentile(luminance, width, x0, y0, Math.Min(width, x0 + BlockSize), Math.Min(height, y0 + BlockSize));
                    levels[(row * columns) + column] = Math.Max(level, floor);
                }
            }

            return new PaperMap(levels, columns, rows);
        }

        public double At(int x, int y)
        {
            // Block centres sit at (n + 0.5) × BlockSize.
            double gx = Math.Clamp(((x + 0.5) / BlockSize) - 0.5, 0, _columns - 1);
            double gy = Math.Clamp(((y + 0.5) / BlockSize) - 0.5, 0, _rows - 1);
            int c0 = (int)gx, r0 = (int)gy;
            int c1 = Math.Min(c0 + 1, _columns - 1), r1 = Math.Min(r0 + 1, _rows - 1);
            double fx = gx - c0, fy = gy - r0;
            double top = Lerp(Level(c0, r0), Level(c1, r0), fx);
            double bottom = Lerp(Level(c0, r1), Level(c1, r1), fx);
            return Lerp(top, bottom, fy);
        }

        private double Level(int column, int row) => _levels[(row * _columns) + column];

        private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

        private static double Percentile(byte[] luminance, int width, int x0, int y0, int x1, int y1)
        {
            Span<int> histogram = stackalloc int[256];
            int count = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    histogram[luminance[(y * width) + x]]++;
                    count++;
                }
            }

            int target = (int)Math.Ceiling(count * PaperPercentile);
            int seen = 0;
            for (int level = 0; level < 256; level++)
            {
                seen += histogram[level];
                if (seen >= target)
                {
                    return level;
                }
            }

            return 255;
        }
    }
}
