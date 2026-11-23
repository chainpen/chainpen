using System.Buffers.Binary;
using Chainpen.Core.Geometry;
using Chainpen.Core.Model;
using Chainpen.Pdf.Import;
using Chainpen.Pdf.Pdfium;
using SkiaSharp;

namespace Chainpen.Pdf.Tests;

/// <summary>FS-201: pictures opened as pages.</summary>
public sealed class ImageImportTests
{
    [Fact]
    public void A_see_through_picture_keeps_its_colour_rather_than_darkening()
    {
        // Half-transparent red on white paper should look pink, not dark red.
        using var bitmap = new SKBitmap(new SKImageInfo(40, 40, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bitmap.Erase(new SKColor(255, 0, 0, 128));
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using PdfiumDocument pdf = PdfiumDocument.CreateNew();
        ImageToPdf.AddImagePage(pdf, png.AsSpan(), 0);

        PdfPageInfo info = pdf.GetPageInfo(0);
        int width = (int)info.Width, height = (int)info.Height;
        var pixels = new byte[width * height * 4];
        pdf.RenderPage(0, pixels, width, height, width * 4);
        int middle = (((height / 2) * width) + (width / 2)) * 4;
        (byte b, byte g, byte r) = (pixels[middle], pixels[middle + 1], pixels[middle + 2]);
        Assert.True(r > 240 && g is > 110 and < 145 && b is > 110 and < 145, $"pink: {(r, g, b)}");
    }

    [Fact]
    public void A_picture_claiming_an_enormous_size_is_refused_before_anything_is_allocated()
    {
        // A tiny PNG whose header claims 30000 × 30000 pixels: decoding it would need 3.6 GB.
        byte[] png = PngClaiming(30_000, 30_000);
        InvalidDataException problem = Assert.Throws<InvalidDataException>(() => ImageToPdf.DecodeUpright(png));
        Assert.Contains("too large", problem.Message, StringComparison.Ordinal);
    }

    private static byte[] PngClaiming(int width, int height)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        using var stream = new MemoryStream();
        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(stream, "IHDR", header);
        WriteChunk(stream, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        byte[] typeAndData = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    [Fact]
    public void A_pasted_photo_too_detailed_for_a_working_copy_is_made_smaller_and_reads_back()
    {
        // Noise is the worst case for PNG: this is about 17 MB as stored, over twice what the working copy reads back.
        using var noisy = new SKBitmap(new SKImageInfo(2400, 1800, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var random = new Random(7);
        random.NextBytes(noisy.GetPixelSpan());
        using SKData file = noisy.Encode(SKEncodedImageFormat.Png, 100);
        Assert.True(file.Size > Core.Session.SessionJson.MaxImageBytes);

        Core.Model.SignatureImage picture = PictureImport.FromImageFile(file.AsSpan());
        Assert.True(picture.Png.Length <= Core.Session.SessionJson.MaxImageBytes, $"{picture.Png.Length} bytes");
        Assert.True(picture.PixelWidth < 2400);
        Assert.Equal(2400.0 / 1800, (double)picture.PixelWidth / picture.PixelHeight, 2);

        // Saved in a working copy's record, it comes back: before, the record was written and then refused.
        Core.Model.DocumentState document = Core.Model.DocumentState.ForSourcePages(1);
        Assert.True(Core.Session.SessionJson.HasRoomFor(document, picture));
        document = Core.Editing.ItemEdits.Add(document, new Core.Model.PictureItem(Core.Model.ItemId.New(), document.Pages[0].Id, 10, 10, picture, 200, 150));
        byte[] json = Core.Session.SessionJson.Write(new Core.Session.SessionRecord(Core.Session.ByteStamp.Of("a"u8), Core.Session.ByteStamp.Of("b"u8), 1, document));
        Assert.Single(Core.Session.SessionJson.Read(json).Document.Items);

        // An ordinary picture is kept at its own size.
        using var plain = new SKBitmap(new SKImageInfo(800, 400, SKColorType.Bgra8888, SKAlphaType.Opaque));
        plain.Erase(SKColors.Teal);
        using SKData small = plain.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Equal(800, PictureImport.FromImageFile(small.AsSpan()).PixelWidth);
    }
}
