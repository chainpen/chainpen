using System.Collections.Immutable;
using Chainpen.Core.Model;
using Chainpen.Core.Session;
using SkiaSharp;

namespace Chainpen.Pdf.Import;

/// <summary>Pictures pasted from other apps (FR-7.14, FS-715): turned upright, shrunk like opened photos, kept as PNG.</summary>
public static class PictureImport
{
    // A few tries is plenty: each aims a little under the limit, and a smaller picture compresses no worse.
    private const int MaxShrinks = 6;

    /// <exception cref="InvalidDataException">The bytes aren't a picture Chainpen can read, or it's too large.</exception>
    public static SignatureImage FromImageFile(ReadOnlySpan<byte> imageFile)
    {
        SKBitmap bitmap = ImageToPdf.DecodeUpright(imageFile);
        try
        {
            SKData png = Encode(bitmap);
            try
            {
                // A detailed photo can be far larger as a PNG than as the JPEG it came from. The working copy only
                // reads back pictures up to a size, so a bigger one is made smaller here, while it can still be seen
                // to happen, rather than saved and then not opened for editing.
                for (int shrinks = 0; png.Size > SessionJson.MaxImageBytes; shrinks++)
                {
                    if (shrinks == MaxShrinks)
                    {
                        throw new InvalidDataException("The picture is too detailed to keep in a working copy.");
                    }

                    double scale = Math.Sqrt((double)SessionJson.MaxImageBytes / png.Size) * 0.9;
                    var target = new SKImageInfo(
                        Math.Max(1, (int)Math.Round(bitmap.Width * scale)),
                        Math.Max(1, (int)Math.Round(bitmap.Height * scale)),
                        bitmap.ColorType,
                        bitmap.AlphaType);
                    SKBitmap smaller = bitmap.Resize(target, new SKSamplingOptions(SKCubicResampler.Mitchell))
                        ?? throw new InvalidDataException("The picture could not be resized.");
                    bitmap.Dispose();
                    bitmap = smaller;
                    png.Dispose();
                    png = Encode(bitmap);
                }

                return new SignatureImage(Guid.NewGuid(), ImmutableArray.Create(png.AsSpan()), bitmap.Width, bitmap.Height);
            }
            finally
            {
                png.Dispose();
            }
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static SKData Encode(SKBitmap bitmap) =>
        bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("The picture could not be stored.");
}
