using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Pdf.Export;
using Fillsign.Pdf.Import;
using Fillsign.Pdf.Pdfium;
using SkiaSharp;
using static Fillsign.Pdf.Tests.DocumentWriterTests;

namespace Fillsign.Pdf.Tests;

/// <summary>FS-614: in the final copy, whiteout removes what it covers rather than painting over it.</summary>
public sealed class WhiteoutRedactionTests
{
    [Fact]
    public void Text_entirely_under_whiteout_is_gone_from_the_final_copy_but_kept_in_the_working_copy()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageId page = state.Pages[0].Id;
        var secret = new TextItem(ItemId.New(), page, 100, 400, "SECRET 4417", 20, InkColor.Black);
        ShapeItem whiteout = Whiteout(page, 90, 390, 200, 50);
        DocumentState document = state.WithItems([secret, whiteout]);

        using PdfiumDocument final = PdfiumDocument.Open(DocumentWriter.FinalCopy(source, pages, document).Pdf);
        Assert.DoesNotContain("SECRET", final.GetText(0), StringComparison.Ordinal);
        Assert.Contains("Page A", final.GetText(0), StringComparison.Ordinal);
        Assert.DoesNotContain("image", final.GetObjectKinds(0));

        using PdfiumDocument working = PdfiumDocument.Open(DocumentWriter.WorkingCopy(source, pages, document).Pdf);
        Assert.Contains("SECRET", working.GetText(0), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_partly_under_whiteout_turns_that_page_into_a_picture_that_still_shows_the_rest()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageId page = state.Pages[0].Id;
        var text = new TextItem(ItemId.New(), page, 100, 400, "VISIBLE HIDDEN", 30, InkColor.Black);
        ShapeItem whiteout = Whiteout(page, 230, 390, 200, 50);
        DocumentState document = state.WithItems([text, whiteout]);

        using PdfiumDocument final = PdfiumDocument.Open(DocumentWriter.FinalCopy(source, pages, document).Pdf);
        Assert.Equal(["image"], final.GetObjectKinds(0));
        Assert.Equal(string.Empty, final.GetText(0).Trim());

        // Other pages are untouched.
        Assert.Contains("Page B", final.GetText(1), StringComparison.Ordinal);

        Bitmap image = Render(final, 0, new PageTransform(pages[0], Rotation.None).DisplaySize);
        Assert.True(image.CountDark(100, 400, 120, 36) > 100, "the uncovered words still show");
        Assert.Equal(0, image.CountDark(232, 392, 196, 46));
    }

    [Fact]
    public void Pixels_of_a_scan_under_whiteout_are_made_white_in_the_file_and_the_rest_kept()
    {
        (byte[] source, SourcePage[] pages) = BlackScan();
        var state = DocumentState.ForSourcePages(1);
        PageId page = state.Pages[0].Id;
        PageSize display = new PageTransform(pages[0], Rotation.None).DisplaySize;
        ShapeItem whiteout = Whiteout(page, display.Width * 0.25, display.Height * 0.25, display.Width * 0.5, display.Height * 0.5);

        using PdfiumDocument final = PdfiumDocument.Open(DocumentWriter.FinalCopy(source, pages, state.WithItems([whiteout])).Pdf);
        Assert.Equal(["image", "path"], final.GetObjectKinds(0));
        (int width, int height, byte[] lightness) = final.ReadImage(0, 0)!.Value;
        byte At(double x, double y) => lightness[((int)(y * height) * width) + (int)(x * width)];
        Assert.True(At(0.5, 0.5) > 240, $"the middle of the stored scan is white: {At(0.5, 0.5)}");
        Assert.True(At(0.3, 0.3) > 240, "inside the whiteout near its corner");
        Assert.True(At(0.1, 0.1) < 30, "outside the whiteout the scan is unchanged");
        Assert.True(At(0.9, 0.6) < 30, "to its right too");
    }

    [Fact]
    public void Things_drawn_on_top_of_whiteout_stay_and_a_box_drawn_round_it_does_not_flatten_the_page()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageId page = state.Pages[0].Id;
        var box = new ShapeItem(ItemId.New(), page, 80, 380, ShapeKind.Rectangle, 240, 70, InkColor.Black, 2, false, false);
        ShapeItem whiteout = Whiteout(page, 90, 390, 200, 50);
        var answer = new TextItem(ItemId.New(), page, 100, 400, "NEW ANSWER", 20, InkColor.Blue);
        DocumentState document = state.WithItems([box, whiteout, answer]);

        using PdfiumDocument final = PdfiumDocument.Open(DocumentWriter.FinalCopy(source, pages, document).Pdf);
        Assert.Contains("NEW ANSWER", final.GetText(0), StringComparison.Ordinal);
        Assert.Contains("Page A", final.GetText(0), StringComparison.Ordinal);
        Assert.DoesNotContain("image", final.GetObjectKinds(0));
    }

    [Fact]
    public void A_line_running_under_whiteout_flattens_the_page()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageId page = state.Pages[0].Id;
        var line = new ShapeItem(ItemId.New(), page, 50, 415, ShapeKind.Line, 400, 0, InkColor.Black, 2, false, false);
        ShapeItem whiteout = Whiteout(page, 90, 390, 200, 50);

        using PdfiumDocument final = PdfiumDocument.Open(DocumentWriter.FinalCopy(source, pages, state.WithItems([line, whiteout])).Pdf);
        Assert.Equal(["image"], final.GetObjectKinds(0));
    }

    [Fact]
    public void Typed_text_under_whiteout_leaves_no_font_behind_to_reveal_its_characters()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageId page = state.Pages[0].Id;
        var secret = new TextItem(ItemId.New(), page, 100, 400, "4417 2290", 20, InkColor.Black);
        DocumentState document = state.WithItems([secret, Whiteout(page, 90, 390, 200, 50)]);

        // The only typed text is covered, so no font is embedded at all: a font would list the digits it was made for.
        byte[] final = DocumentWriter.FinalCopy(source, pages, document).Pdf;
        Assert.DoesNotContain("/FontFile2", TestFiles.ReadableText(final), StringComparison.Ordinal);

        // Visible text still gets a real, searchable font.
        var visible = new TextItem(ItemId.New(), page, 100, 600, "Visible", 20, InkColor.Black);
        byte[] withVisible = DocumentWriter.FinalCopy(source, pages, document.WithItems([.. document.Items, visible])).Pdf;
        using PdfiumDocument result = PdfiumDocument.Open(withVisible);
        Assert.Contains("Visible", result.GetText(0), StringComparison.Ordinal);
        Assert.DoesNotContain("4417", result.GetText(0), StringComparison.Ordinal);
    }

    [Fact]
    public void Form_fields_under_whiteout_are_taken_out_and_other_pages_keep_theirs()
    {
        (byte[] source, SourcePage[] pages) = Source("corpus/form_two_pages.pdf");
        using (PdfiumDocument original = PdfiumDocument.Open(source))
        {
            Assert.True(original.GetAnnotationCount(0) > 0, "the sample has fields on page 1");
        }

        var state = DocumentState.ForSourcePages(pages.Length);
        PageId page = state.Pages[0].Id;
        PageSize size = new PageTransform(pages[0], Rotation.None).DisplaySize;
        DocumentState document = state.WithItems([Whiteout(page, 0, 0, size.Width, size.Height)]);

        using PdfiumDocument final = PdfiumDocument.Open(DocumentWriter.FinalCopy(source, pages, document).Pdf);
        Assert.Equal(0, final.GetAnnotationCount(0));
        Assert.True(final.GetAnnotationCount(1) > 0, "page 2 has no whiteout, so its fields stay");
    }

    [Fact]
    public void Nothing_under_whiteout_is_left_anywhere_in_the_final_copys_bytes()
    {
        // Reading the page isn't enough: a filled field's answer or a typed word can survive in drawing data no page
        // uses any more, which anyone can unpack from the file.
        (byte[] source, SourcePage[] pages) = Source("form-fields.pdf");
        var state = DocumentState.ForSourcePages(1).WithField("Name", "HIDDENANSWER");
        PageId page = state.Pages[0].Id;
        PageSize size = new PageTransform(pages[0], Rotation.None).DisplaySize;
        var typed = new TextItem(ItemId.New(), page, 100, 400, "HIDDENWORDS", 20, InkColor.Black);
        DocumentState document = state.WithItems([typed, Whiteout(page, 0, 0, size.Width, size.Height)]);

        byte[] final = DocumentWriter.FinalCopy(source, pages, document).Pdf;
        string everything = EveryStreamUnpacked(final);
        Assert.DoesNotContain("HIDDENANSWER", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDENWORDS", everything, StringComparison.Ordinal);

        // The working copy keeps them, as it should.
        Assert.Contains("HIDDENANSWER", EveryStreamUnpacked(DocumentWriter.WorkingCopy(source, pages, document).Pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void The_final_copy_drops_page_thumbnails_and_other_apps_private_copies()
    {
        // This Illustrator file carries a thumbnail of its page and Illustrator's own full copy of the artwork: both
        // would still show what whiteout covered, and neither is needed to read the page.
        Assert.SkipUnless(File.Exists(TestFiles.Sample("corpus/cmykjpeg.pdf")), "Corpus not downloaded; run build/fetch-corpus.sh");
        (byte[] source, SourcePage[] pages) = Source("corpus/cmykjpeg.pdf");
        Assert.Contains("/Thumb", EveryStreamUnpacked(source), StringComparison.Ordinal);
        var state = DocumentState.ForSourcePages(pages.Length);
        PageSize size = new PageTransform(pages[0], Rotation.None).DisplaySize;
        DocumentState document = state.WithItems([Whiteout(state.Pages[0].Id, 0, 0, size.Width, size.Height)]);

        string final = EveryStreamUnpacked(DocumentWriter.FinalCopy(source, pages, document).Pdf);
        Assert.DoesNotContain("/Thumb", final, StringComparison.Ordinal);
        Assert.DoesNotContain("/PieceInfo", final, StringComparison.Ordinal);
        Assert.DoesNotContain("AIPrivateData", final, StringComparison.Ordinal);
    }

    /// <summary>The file's bytes with every compressed stream unpacked after them, as someone digging through it would.</summary>
    private static string EveryStreamUnpacked(byte[] pdf)
    {
        var text = new System.Text.StringBuilder(System.Text.Encoding.Latin1.GetString(pdf));
        string raw = text.ToString();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(raw, "stream\r?\n"))
        {
            try
            {
                int start = match.Index + match.Length;
                using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(pdf, start, pdf.Length - start), System.IO.Compression.CompressionMode.Decompress);
                using var unpacked = new MemoryStream();
                zlib.CopyTo(unpacked);
                text.Append('\n').Append(System.Text.Encoding.Latin1.GetString(unpacked.ToArray()));
            }
            catch (InvalidDataException)
            {
                // Not compressed, or not with zlib: its bytes are already in the text.
            }
        }

        return text.ToString();
    }

    private static ShapeItem Whiteout(PageId page, double x, double y, double width, double height) =>
        new(ItemId.New(), page, x, y, ShapeKind.Whiteout, width, height, InkColor.Black, 1, false, false);

    /// <summary>A one-page PDF of a solid black photo, as opening a scan makes.</summary>
    private static (byte[] Clean, SourcePage[] Pages) BlackScan()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(300, 400, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.Black);
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using PdfiumDocument pdf = PdfiumDocument.CreateNew();
        ImageToPdf.AddImagePage(pdf, png.AsSpan(), 0);
        byte[] clean = pdf.SaveCopy(removeSecurity: true);
        using PdfiumDocument reopened = PdfiumDocument.Open(clean);
        return (clean, [reopened.GetPageInfo(0).ToSourcePage()]);
    }
}
