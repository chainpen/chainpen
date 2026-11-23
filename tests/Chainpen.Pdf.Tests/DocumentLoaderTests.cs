using Chainpen.Pdf.Import;
using Chainpen.Pdf.Pdfium;
using SkiaSharp;

namespace Chainpen.Pdf.Tests;

/// <summary>FS-200, FS-201, FS-202, FS-207, FS-208: opening, combining, images and errors.</summary>
public sealed class DocumentLoaderTests
{
    private static readonly PasswordPrompt s_noPassword = (_, _) => Task.FromResult<string?>(null);

    private static InputFile Sample(string name) => new(Path.GetFileName(name), TestFiles.Sample(name), File.ReadAllBytes(TestFiles.Sample(name)));

    [Fact]
    public async Task A_file_whose_owner_forbids_changes_is_reported_so_the_person_can_be_asked()
    {
        InputFile restricted = Sample("corpus/empty_protected.pdf");
        OpenResult result = await DocumentLoader.OpenAsync([restricted], s_noPassword, CancellationToken.None);
        Assert.Equal(["empty_protected.pdf"], result.RestrictedFiles);
        result.Source.Dispose();

        OpenResult ordinary = await DocumentLoader.OpenAsync([Sample("four-pages.pdf")], s_noPassword, CancellationToken.None);
        Assert.Empty(ordinary.RestrictedFiles);
        ordinary.Source.Dispose();
    }

    [Fact]
    public async Task A_single_pdf_is_used_as_it_is()
    {
        OpenResult result = await DocumentLoader.OpenAsync([Sample("four-pages.pdf")], s_noPassword, CancellationToken.None);
        using SourceDocument source = result.Source;
        Assert.Equal("four-pages.pdf", source.Name);
        Assert.NotNull(source.Path);
        Assert.Equal(4, source.PageCount);
        Assert.Empty(result.Skipped);

        source.LoadPages(null, CancellationToken.None);
        Assert.True(source.IsFullyLoaded);
        Assert.Empty(source.DamagedPages);
    }

    [Fact]
    public async Task Several_files_combine_in_order_with_images_as_pages()
    {
        InputFile photo = new("photo.jpg", null, Jpeg(200, 100, exifOrientation: 1));
        OpenResult result = await DocumentLoader.OpenAsync([Sample("sample-form.pdf"), photo, Sample("four-pages.pdf")], s_noPassword, CancellationToken.None);
        using SourceDocument source = result.Source;
        int formPages = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample("sample-form.pdf"))).PageCount;
        Assert.Equal(formPages + 1 + 4, source.PageCount);
        Assert.Equal("sample-form.pdf", source.Name);
        Assert.Null(source.Path);

        // The photo is one landscape page whose long side matches A4's.
        var info = source.Pdf.GetPageInfo(formPages);
        Assert.Equal(842, info.Width, 0.5);
        Assert.Equal(421, info.Height, 0.5);
    }

    [Fact]
    public async Task Exif_orientation_turns_the_photo_the_right_way_up()
    {
        // Stored 200×100 with the left half red; orientation 6 means "rotate 90° clockwise to view".
        InputFile photo = new("sideways.jpg", null, Jpeg(200, 100, exifOrientation: 6));
        OpenResult result = await DocumentLoader.OpenAsync([photo], s_noPassword, CancellationToken.None);
        using SourceDocument source = result.Source;
        var info = source.Pdf.GetPageInfo(0);
        Assert.True(info.Height > info.Width, "the page should be portrait");

        int w = (int)info.Width / 4, h = (int)info.Height / 4;
        var pixels = new byte[w * h * 4];
        source.Pdf.RenderPage(0, pixels, w, h, w * 4);
        (byte b, byte g, byte r) top = Pixel(pixels, w, w / 2, h / 4), bottom = Pixel(pixels, w, w / 2, h * 3 / 4);
        Assert.True(top.r > 200 && top.b < 80, $"top should be red, was {top}");
        Assert.True(bottom.b > 200, $"bottom should be blue, was {bottom}");
    }

    [Fact]
    public async Task A_transparent_png_keeps_its_transparency()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawRect(0, 0, 20, 20, new SKPaint { Color = SKColors.Black });
        }

        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        OpenResult result = await DocumentLoader.OpenAsync([new InputFile("sig.png", null, png.ToArray())], s_noPassword, CancellationToken.None);
        using SourceDocument source = result.Source;
        var pixels = new byte[84 * 42 * 4];
        source.Pdf.RenderPage(0, pixels, 84, 42, 84 * 4);
        Assert.True(Pixel(pixels, 84, 10, 20).r < 50);
        Assert.True(Pixel(pixels, 84, 70, 20).r > 240); // transparent half shows the white page
    }

    [Fact]
    public async Task Unreadable_files_are_explained()
    {
        InputFile docx = new("notes.docx", null, "PK\u0003\u0004 not a pdf"u8.ToArray());
        InputFile broken = new("referral.pdf", null, "%PDF-1.7\nnothing else"u8.ToArray());
        InputFile missing = new("scan.jpg", "/nowhere/scan.jpg", null, new FileNotFoundException());

        var ex = await Assert.ThrowsAsync<DocumentOpenException>(() => DocumentLoader.OpenAsync([docx, broken, missing], s_noPassword, CancellationToken.None));
        Assert.Equal([OpenProblemKind.NotPdfOrImage, OpenProblemKind.Damaged, OpenProblemKind.CantRead], ex.Problems.Select(p => p.Kind));
        Assert.Equal("notes.docx isn’t a PDF or an image. Save it as a PDF first, then open it here.", ex.Problems[0].Message);
        Assert.Equal("referral.pdf is damaged and can’t be opened", ex.Problems[1].Title);
    }

    [Fact]
    public async Task Some_files_open_while_others_are_reported()
    {
        InputFile docx = new("notes.docx", null, "nope"u8.ToArray());
        OpenResult result = await DocumentLoader.OpenAsync([Sample("four-pages.pdf"), docx], s_noPassword, CancellationToken.None);
        using SourceDocument source = result.Source;
        Assert.Equal(4, source.PageCount);
        Assert.Equal("notes.docx", Assert.Single(result.Skipped).FileName);
    }

    [Fact]
    public async Task A_password_is_asked_for_until_right_or_skipped()
    {
        string path = TestFiles.CorpusFiles().FirstOrDefault(f => f.EndsWith("crypt_filters.pdf", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Run build/fetch-corpus.sh first.");
        var asked = new List<bool>();
        PasswordPrompt prompt = (name, wasWrong) =>
        {
            asked.Add(wasWrong);
            return Task.FromResult(asked.Count < 3 ? "wrong" + asked.Count : null);
        };

        InputFile file = new("crypt_filters.pdf", path, File.ReadAllBytes(path));
        await Assert.ThrowsAsync<OperationCanceledException>(() => DocumentLoader.OpenAsync([file], prompt, CancellationToken.None));
        Assert.Equal([false, true, true], asked);
    }

    [Fact]
    public async Task Pages_can_be_added_to_an_open_document()
    {
        OpenResult result = await DocumentLoader.OpenAsync([Sample("four-pages.pdf")], s_noPassword, CancellationToken.None);
        using SourceDocument source = result.Source;
        var (pages, skipped, _) = await DocumentLoader.OpenForAddingAsync([Sample("four-pages.pdf"), new InputFile("p.jpg", null, Jpeg(50, 80, 1))], s_noPassword, CancellationToken.None);
        using (pages)
        {
            Assert.Empty(skipped);
            Assert.Equal([4, 5, 6, 7, 8], source.AppendFrom(pages));
        }

        Assert.Equal(9, source.PageCount);
        Assert.True(source.IsFullyLoaded);
    }

    [Fact]
    public async Task Fields_on_added_pages_are_offered_only_if_they_survive_saving()
    {
        OpenResult result = await DocumentLoader.OpenAsync([Sample("four-pages.pdf")], s_noPassword, CancellationToken.None);
        using SourceDocument source = result.Source;
        var (pages, _, _) = await DocumentLoader.OpenForAddingAsync([Sample("form-fields.pdf")], s_noPassword, CancellationToken.None);
        using (pages)
        {
            Assert.Equal([4], source.AppendFrom(pages));
        }

        // PDFium's import loses the radio group's parent field, so it isn't offered; fields that are their own widget are.
        IReadOnlyList<FormField> fields = source.FormFields(4);
        Assert.Contains(fields, field => field.Name == "Name" && field.Kind == FormFieldKind.Text);
        Assert.Contains(fields, field => field.Name == "Signature");
        Assert.DoesNotContain(fields, field => field.Kind == FormFieldKind.Radio);

        // And what's offered fills and saves.
        using PdfiumDocument copy = PdfiumDocument.Open(source.Pdf.SaveCopy(removeSecurity: true));
        copy.FillFormFields(new Dictionary<string, string> { ["Name"] = "Added page" });
        using PdfiumDocument back = PdfiumDocument.Open(copy.SaveCopy(removeSecurity: true));
        Assert.Equal("Added page", back.GetFormFields(4).Single(field => field.Name == "Name").Value);
    }

    private static (byte b, byte g, byte r) Pixel(byte[] bgra, int width, int x, int y)
    {
        int i = ((y * width) + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    /// <summary>A JPEG whose left half is red and right half blue, with an EXIF orientation tag.</summary>
    private static byte[] Jpeg(int width, int height, int exifOrientation)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawRect(0, 0, width / 2f, height, new SKPaint { Color = SKColors.Red });
            canvas.DrawRect(width / 2f, 0, width / 2f, height, new SKPaint { Color = SKColors.Blue });
        }

        using SKData jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        byte[] data = jpeg.ToArray();

        // APP1 Exif segment, big-endian TIFF with one IFD entry: Orientation (0x0112), SHORT, 1 value.
        byte[] exif =
        [
            .. "Exif\0\0"u8,
            .. "MM\0*"u8, 0, 0, 0, 8,
            0, 1,
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)exifOrientation, 0, 0,
            0, 0, 0, 0,
        ];
        int length = exif.Length + 2;
        return [.. data[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exif, .. data[2..]];
    }
}
