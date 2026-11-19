using System.Collections.Immutable;
using Fillsign.Core.Model;
using SkiaSharp;

namespace Fillsign.Pdf.Import;

/// <summary>Pictures pasted from other apps (FR-7.14, FS-715): turned upright, shrunk like opened photos, kept as PNG.</summary>
public static class PictureImport
{
    /// <exception cref="InvalidDataException">The bytes aren't a picture Fillsign can read, or it's too large.</exception>
    public static SignatureImage FromImageFile(ReadOnlySpan<byte> imageFile)
    {
        using SKBitmap upright = ImageToPdf.DecodeUpright(imageFile);
        using SKData png = upright.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("The picture could not be stored.");
        return new SignatureImage(Guid.NewGuid(), ImmutableArray.Create(png.AsSpan()), upright.Width, upright.Height);
    }
}
