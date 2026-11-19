using System.Buffers.Binary;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Pdf.Import;
using Fillsign.Pdf.Pdfium;
using SkiaSharp;

namespace Fillsign.Pdf.Tests;

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
}
