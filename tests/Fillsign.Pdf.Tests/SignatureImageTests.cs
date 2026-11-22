using Fillsign.Core.Model;
using Fillsign.Pdf.Fonts;
using Fillsign.Pdf.Signatures;
using SkiaSharp;

namespace Fillsign.Pdf.Tests;

public sealed class SignatureImageTests
{
    private static readonly string s_output = Path.Combine(TestFiles.RepoRoot, "artifacts", "signatures");

    [Fact]
    public void Drawn_ink_is_cropped_to_the_strokes_at_twice_the_pad_size()
    {
        IReadOnlyList<InkSample> stroke = [new(100, 50, 0.5), new(140, 70, 0.5), new(180, 50, 0.5), new(220, 70, 0.5)];
        SignatureImage image = SignatureImageMaker.FromInk([stroke], InkColor.Blue) ?? throw new InvalidOperationException("no image");
        Save(image, "drawn");

        // 120 × 20 pad units of path, plus the pen width and a few pixels' padding, at 2×.
        Assert.InRange(image.PixelWidth, 240, 260);
        Assert.InRange(image.PixelHeight, 40, 60);
        using SKBitmap bitmap = Decode(image);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Contains(Pixels(bitmap), pixel => pixel.Alpha == 255 && pixel.Blue > pixel.Red);
    }

    [Fact]
    public void Nothing_drawn_or_typed_makes_no_image()
    {
        Assert.Null(SignatureImageMaker.FromInk([], InkColor.Black));
        Assert.Null(SignatureImageMaker.FromInk([[]], InkColor.Black));
        Assert.Null(SignatureImageMaker.FromText("   ", HandwritingStyle.Caveat, InkColor.Black));
    }

    [Fact]
    public void A_single_tap_is_a_dot()
    {
        SignatureImage dot = SignatureImageMaker.FromInk([[new InkSample(10, 10, 0.5)]], InkColor.Black) ?? throw new InvalidOperationException("no image");
        Assert.InRange(dot.PixelWidth, 5, 20);
    }

    [Fact]
    public void Firmer_pen_pressure_draws_a_wider_line()
    {
        Assert.Equal(SignatureInk.BaseWidth, SignatureInk.WidthFor(SignatureInk.MousePressure), 6);
        Assert.True(SignatureInk.WidthFor(1) > SignatureInk.WidthFor(0.2));

        IReadOnlyList<InkSample> Line(double pressure) => [.. Enumerable.Range(0, 20).Select(i => new InkSample(i * 10, 0, pressure))];
        SignatureImage light = SignatureImageMaker.FromInk([Line(0.1)], InkColor.Black)!;
        SignatureImage firm = SignatureImageMaker.FromInk([Line(1)], InkColor.Black)!;
        Assert.True(firm.PixelHeight > light.PixelHeight);
    }

    [Theory]
    [InlineData(HandwritingStyle.Caveat)]
    [InlineData(HandwritingStyle.DancingScript)]
    [InlineData(HandwritingStyle.Kalam)]
    public void Typed_names_use_each_handwriting_font_and_are_cropped(HandwritingStyle style)
    {
        SignatureImage image = SignatureImageMaker.FromText("  Jordan Lee ", style, InkColor.Black) ?? throw new InvalidOperationException("no image");
        Save(image, $"typed-{style}");

        // Wider than tall, and cropped: no empty margin bigger than the padding on any side.
        Assert.InRange(image.PixelWidth / (double)image.PixelHeight, 2.5, 9);
        using SKBitmap bitmap = Decode(image);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.True(ColumnHasInk(bitmap, 5) || ColumnHasInk(bitmap, 4), "left edge should be close to the ink");
        Assert.True(ColumnHasInk(bitmap, bitmap.Width - 6) || ColumnHasInk(bitmap, bitmap.Width - 5), "right edge should be close to the ink");
    }

    [Fact]
    public void Each_style_looks_different()
    {
        SignatureImage caveat = SignatureImageMaker.FromText("Jordan Lee", HandwritingStyle.Caveat, InkColor.Black)!;
        SignatureImage kalam = SignatureImageMaker.FromText("Jordan Lee", HandwritingStyle.Kalam, InkColor.Black)!;
        Assert.NotEqual((caveat.PixelWidth, caveat.PixelHeight), (kalam.PixelWidth, kalam.PixelHeight));
    }

    [Fact]
    public void Very_long_names_are_cut_to_the_limit()
    {
        SignatureImage longest = SignatureImageMaker.FromText(new string('m', SignatureImageMaker.MaxTypedLength), HandwritingStyle.Kalam, InkColor.Black)!;
        SignatureImage longer = SignatureImageMaker.FromText(new string('m', SignatureImageMaker.MaxTypedLength + 40), HandwritingStyle.Kalam, InkColor.Black)!;
        Assert.Equal(longest.PixelWidth, longer.PixelWidth);
    }

    [Fact]
    public void A_photo_loses_its_paper_and_shadow_but_keeps_the_ink()
    {
        // Paper that darkens from 240 to 170 across the photo (a shadow), with a dark pen line through the middle.
        byte[] photo = MakePhoto(800, 400, paper: x => 240 - (70 * x / 800), ink: 60);
        using SignaturePhoto loaded = SignaturePhoto.Load(photo);
        SignatureImage image = loaded.MakeSignature(removeBackground: true) ?? throw new InvalidOperationException("no image");
        Save(image, "photo");

        // Cropped to the line (x 100–700, y 180–220) plus padding, not the whole photo.
        Assert.InRange(image.PixelWidth, 600, 620);
        Assert.InRange(image.PixelHeight, 40, 60);
        using SKBitmap bitmap = Decode(image);
        Assert.Equal(255, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).Alpha);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width / 2, 1).Alpha);

        // Even in the shadow, the ink is darker than the line was on the lit side's paper, and still solid.
        SKColor shadowedInk = bitmap.GetPixel(bitmap.Width - 20, bitmap.Height / 2);
        Assert.Equal(255, shadowedInk.Alpha);
        Assert.True(shadowedInk.Red < 128);
    }

    [Fact]
    public void Keeping_the_background_keeps_the_whole_photo()
    {
        byte[] photo = MakePhoto(800, 400, paper: _ => 230, ink: 60);
        using SignaturePhoto loaded = SignaturePhoto.Load(photo);
        SignatureImage image = loaded.MakeSignature(removeBackground: false)!;
        Assert.Equal((800, 400), (image.PixelWidth, image.PixelHeight));
        Assert.False(loaded.OriginalPng.IsDefaultOrEmpty);
    }

    [Fact]
    public void Large_photos_are_shrunk_and_blank_ones_have_no_signature()
    {
        byte[] photo = MakePhoto(3000, 1500, paper: _ => 235, ink: null);
        using SignaturePhoto loaded = SignaturePhoto.Load(photo);
        Assert.Equal(SignaturePhoto.MaxLongSidePixels, loaded.PixelWidth);
        Assert.Null(loaded.MakeSignature(removeBackground: true));
    }

    [Fact]
    public void Files_that_are_not_images_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => SignaturePhoto.Load("not an image"u8));
    }

    private static byte[] MakePhoto(int width, int height, Func<int, int> paper, byte? ink)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (int x = 0; x < width; x++)
        {
            byte level = (byte)paper(x);
            for (int y = 0; y < height; y++)
            {
                bitmap.SetPixel(x, y, new SKColor(level, level, (byte)(level - 8)));
            }
        }

        if (ink is { } inkLevel)
        {
            using var canvas = new SKCanvas(bitmap);
            using var pen = new SKPaint { Color = new SKColor(inkLevel, inkLevel, (byte)(inkLevel + 30)), StrokeWidth = 8, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var path = new SKPath();
            path.MoveTo(100, height / 2f);
            path.CubicTo(250, (height / 2f) - 40, 550, (height / 2f) + 40, 700, height / 2f);
            canvas.DrawPath(path, pen);
        }

        using SKData jpeg = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return jpeg.ToArray();
    }

    private static SKBitmap Decode(SignatureImage image) => SKBitmap.Decode(image.Png.AsSpan().ToArray());

    private static IEnumerable<SKColor> Pixels(SKBitmap bitmap) =>
        Enumerable.Range(0, bitmap.Width).SelectMany(x => Enumerable.Range(0, bitmap.Height).Select(y => bitmap.GetPixel(x, y)));

    private static bool ColumnHasInk(SKBitmap bitmap, int x) => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).Alpha > 8);

    private static void Save(SignatureImage image, string name)
    {
        Directory.CreateDirectory(s_output);
        File.WriteAllBytes(Path.Combine(s_output, name + ".png"), image.Png.AsSpan().ToArray());
    }

    [Fact]
    public void A_name_the_handwriting_styles_cant_draw_is_drawn_in_a_font_that_can()
    {
        // Needs an installed Japanese font (macOS and Windows have them; a bare Linux machine may not).
        using SKTypeface? japanese = SKFontManager.Default.MatchCharacter('山');
        Assert.SkipWhen(japanese is null, "No Japanese font is installed.");
        SignatureImage image = SignatureImageMaker.FromText("やまだ 太郎", HandwritingStyle.Caveat, InkColor.Black) ?? throw new InvalidOperationException("no image");
        using SKBitmap bitmap = SKBitmap.Decode(image.Png.ToArray());
        Directory.CreateDirectory(Path.Combine(TestFiles.RepoRoot, "artifacts", "screenshots"));
        using (FileStream file = File.Create(Path.Combine(TestFiles.RepoRoot, "artifacts", "screenshots", "typed-signature-japanese.png")))
        {
            bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
        }

        // Drawn in one font that has every character, so none comes out as an empty box.
        (byte[] fontFile, int faceIndex) = SignatureImageMaker.FontWithAll("やまだ太郎", 'や') ?? throw new InvalidOperationException("no font has the name");
        using (var data = SKData.CreateCopy(fontFile))
        using (SKTypeface typeface = SKTypeface.FromData(data, faceIndex)!)
        {
            Assert.All("やまだ太郎", character => Assert.True(typeface.ContainsGlyph(character), $"{typeface.FamilyName} has no {character}"));
        }

        // The strokes cover a fair share of the picture.
        int inked = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                inked += bitmap.GetPixel(x, y).Alpha > 128 ? 1 : 0;
            }
        }

        Assert.InRange(inked / (double)(bitmap.Width * bitmap.Height), 0.08, 0.8);
    }
}
