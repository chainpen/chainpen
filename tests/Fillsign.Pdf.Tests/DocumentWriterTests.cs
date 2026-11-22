using System.Collections.Immutable;
using System.Text;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Core.Session;
using Fillsign.Pdf.Export;
using Fillsign.Pdf.Fonts;
using Fillsign.Pdf.Pdfium;
using SkiaSharp;

namespace Fillsign.Pdf.Tests;

/// <summary>FS-600–603: the writer puts out exactly what the editor shows.</summary>
public sealed class DocumentWriterTests
{
    private const double Scale = 2;

    [Fact]
    public void The_final_copy_has_the_pages_in_order_with_rotation_duplicates_and_deletions()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);

        // D, B, B (rotated), A; C deleted.
        ImmutableArray<PageEntry> entries =
        [
            state.Pages[3],
            state.Pages[1],
            state.Pages[1] with { Id = PageId.New(), Rotation = Rotation.FromQuarterTurns(1) },
            state.Pages[0],
        ];
        WrittenDocument written = DocumentWriter.FinalCopy(source, pages, state.WithPages(entries));
        Assert.False(written.HaveFormsMoved);

        using PdfiumDocument result = PdfiumDocument.Open(written.Pdf);
        Assert.Equal(4, result.PageCount);
        // Text on a rotated page comes out in a different reading order, so look for each page's heading anywhere.
        string[] headings = ["Page D", "Page B", "Page B", "Page A"];
        for (int i = 0; i < 4; i++)
        {
            Assert.Contains(headings[i], result.GetText(i), StringComparison.Ordinal);
            Assert.DoesNotContain(headings.Except([headings[i]]).First(), result.GetText(i), StringComparison.Ordinal);
        }
        Assert.Equal(1, result.GetPageInfo(2).Rotation);
        Assert.Equal(0, result.GetPageInfo(1).Rotation);
        Assert.DoesNotContain(SessionStore.TrailerKey, TestFiles.ReadableText(written.Pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicated_page_with_form_fields_is_filled_like_the_original()
    {
        // The copy is imported from a second open document; the form layer follows links into it while filling and
        // saving, so it has to stay open until then.
        (byte[] source, SourcePage[] pages) = Source("form-fields.pdf");
        DocumentState state = DocumentState.ForSourcePages(1).WithField("Name", "Jordan Lee");
        state = state.WithPages([state.Pages[0], state.Pages[0] with { Id = PageId.New() }]);

        foreach (byte[] written in new[] { DocumentWriter.FinalCopy(source, pages, state).Pdf, DocumentWriter.WorkingCopy(source, pages, state).Pdf })
        {
            using PdfiumDocument result = PdfiumDocument.Open(written);
            Assert.Equal(2, result.PageCount);
            for (int page = 0; page < 2; page++)
            {
                Assert.Equal("Jordan Lee", result.GetFormFields(page).Single(field => field.Name == "Name").Value);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Items_land_where_the_editor_shows_them_on_rotated_pages(int quarterTurns)
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageEntry page = state.Pages[0] with { Rotation = Rotation.FromQuarterTurns(quarterTurns) };
        var display = new PageTransform(pages[0], page.Rotation).DisplaySize;

        // A whiteout covering the page, then a black filled box near the display's top-left: a known spot to find.
        var cover = new ShapeItem(ItemId.New(), page.Id, 0, 0, ShapeKind.Whiteout, display.Width, display.Height, InkColor.Black, 1, false, false);
        var box = new ShapeItem(ItemId.New(), page.Id, 40, 60, ShapeKind.Rectangle, 50, 20, InkColor.Black, 8, false, false);
        DocumentState document = state.WithPages([page]).WithItems([cover, box]);

        using PdfiumDocument result = PdfiumDocument.Open(DocumentWriter.FinalCopy(source, pages, document).Pdf);
        Bitmap image = Render(result, 0, display);
        Assert.True(image.IsDark(40, 60), "the box's top-left corner");
        Assert.True(image.IsDark(90, 80), "its bottom-right corner");
        Assert.False(image.IsDark(65, 70), "its middle is an outline, not filled");
        Assert.False(image.IsDark(140, 140), "nothing outside it");
        Assert.False(image.IsDark(display.Width - 30, display.Height - 30), "the far corner is covered by the whiteout");
    }

    [Fact]
    public void Text_is_real_searchable_text_placed_with_the_editors_baseline()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageId page = state.Pages[1].Id;
        var text = new TextItem(ItemId.New(), page, 100, 300, "Jordan Lee\nÅsa Øberg 17/11/2026", 24, InkColor.Blue);
        WrittenDocument written = DocumentWriter.FinalCopy(source, pages, state.WithItems([text]));

        using PdfiumDocument result = PdfiumDocument.Open(written.Pdf);
        string extracted = result.GetText(1);
        Assert.Contains("Jordan Lee", extracted, StringComparison.Ordinal);
        Assert.Contains("Åsa Øberg 17/11/2026", extracted, StringComparison.Ordinal);

        // Glyphs sit within the first line's box (top 300, 1.2 × 24 tall) and nowhere above it.
        PageSize display = new PageTransform(pages[1], Rotation.None).DisplaySize;
        Bitmap image = Render(result, 1, display);
        double topInk = image.FirstDarkRow(100, 240, 280, 340);
        Assert.InRange(topInk, 300, 310);

        // The font is a subset: far smaller than the full 600 KB Noto Sans.
        Assert.True(written.Pdf.Length < source.Length + 60_000, $"{written.Pdf.Length:N0} bytes");
    }

    [Fact]
    public void Bold_text_is_heavier_and_alignment_lines_up_each_line_as_on_screen()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageId page = state.Pages[0].Id;
        var regular = new TextItem(ItemId.New(), page, 60, 100, "Heavy", 24, InkColor.Black);
        TextItem bold = regular with { Id = ItemId.New(), Y = 160, IsBold = true };
        var right = new TextItem(ItemId.New(), page, 60, 250, "A much longer line\nshort", 20, InkColor.Black, Align: TextAlign.Right);
        var centre = right with { Id = ItemId.New(), Y = 350, Align = TextAlign.Centre };
        WrittenDocument written = DocumentWriter.FinalCopy(source, pages, state.WithItems([regular, bold, right, centre]));

        using PdfiumDocument result = PdfiumDocument.Open(written.Pdf);
        Assert.Contains("Heavy", result.GetText(0), StringComparison.Ordinal);
        Bitmap image = Render(result, 0, new PageTransform(pages[0], Rotation.None).DisplaySize);
        int regularInk = image.CountDark(55, 100, 120, 30), boldInk = image.CountDark(55, 160, 120, 30);
        Assert.True(boldInk > regularInk * 1.15, $"bold {boldInk} vs regular {regularInk}");

        // The short second line ends where the long first line ends (right), or sits in its middle (centre).
        double box = NotoTextMeasurer.Instance.MeasureLineWidth("A much longer line", 20);
        double shortWidth = NotoTextMeasurer.Instance.MeasureLineWidth("short", 20);
        double lineTwo = 250 + (20 * TextItem.LineHeight);
        // Within 2.5 pt: ink stops short of a glyph's advance by its side bearing.
        Assert.InRange(LastDarkColumn(image, lineTwo, 24), 60 + box - 2.5, 60 + box);
        Assert.InRange(FirstDarkColumn(image, lineTwo + 100, 24), 60 + ((box - shortWidth) / 2), 60 + ((box - shortWidth) / 2) + 2.5);

        // Left-aligned, the short line would start at the item's left edge instead.
        Assert.True(LastDarkColumn(image, lineTwo, 24) > 60 + shortWidth + 20);
    }

    private static double LastDarkColumn(Bitmap image, double top, double height)
    {
        for (double x = image.Width / Scale; x > 0; x -= 0.5)
        {
            if (image.CountDark(x, top, 0.5, height) > 0)
            {
                return x;
            }
        }

        return 0;
    }

    private static double FirstDarkColumn(Bitmap image, double top, double height)
    {
        for (double x = 0; x < image.Width / Scale; x += 0.5)
        {
            if (image.CountDark(x, top, 0.5, height) > 0)
            {
                return x;
            }
        }

        return 0;
    }

    [Fact]
    public void Embedded_fonts_carry_a_subset_tag_and_characters_noto_lacks_use_another_font()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageId page = state.Pages[0].Id;
        var text = new TextItem(ItemId.New(), page, 100, 300, "Contract 契约 signed", 20, InkColor.Black);
        byte[] final = DocumentWriter.FinalCopy(source, pages, state.WithItems([text])).Pdf;

        // ISO 32000 §9.6.4: an embedded subset is named TAG+Name, the tag six capital letters.
        Assert.Matches(@"/BaseFont\s*/[A-Z]{6}\+NotoSans", TestFiles.ReadableText(final));

        using SKTypeface? cjk = SKFontManager.Default.MatchCharacter('契');
        Assert.SkipWhen(cjk is null, "No font on this computer has Chinese characters.");
        using PdfiumDocument result = PdfiumDocument.Open(final);
        string extracted = result.GetText(0);
        Assert.True(extracted.Contains("契约", StringComparison.Ordinal), $"{cjk!.FamilyName}: «{extracted}» fonts: {string.Join(", ", System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(final), @"/BaseFont\s*/[^\s/>]+").Select(m => m.Value))}");
        Assert.Contains("Contract", result.GetText(0), StringComparison.Ordinal);
    }

    [Fact]
    public void Japanese_text_with_kana_and_kanji_exports_as_searchable_text()
    {
        using SKTypeface? japanese = SKFontManager.Default.MatchCharacter('や');
        Assert.SkipWhen(japanese is null, "No font on this computer has Japanese characters.");
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        var text = new TextItem(ItemId.New(), state.Pages[0].Id, 100, 300, "氏名 やまだ 太郎 2026/11/21", 20, InkColor.Black);
        byte[] final = DocumentWriter.FinalCopy(source, pages, state.WithItems([text])).Pdf;

        using PdfiumDocument result = PdfiumDocument.Open(final);
        string extracted = result.GetText(0);
        Assert.Contains("やまだ", extracted, StringComparison.Ordinal);
        Assert.Contains("太郎", extracted, StringComparison.Ordinal);
        Assert.Contains("2026/11/21", extracted, StringComparison.Ordinal);
    }

    [Fact]
    public void Highlights_multiply_whiteout_covers_and_signatures_keep_their_transparency()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        PageId page = state.Pages[0].Id;
        var text = new TextItem(ItemId.New(), page, 100, 400, "UNDER", 40, InkColor.Black);
        var highlight = new ShapeItem(ItemId.New(), page, 90, 395, ShapeKind.Highlight, 180, 55, InkColor.Black, 1, false, false);
        var signature = new SignatureItem(ItemId.New(), page, 100, 500, SignatureKind.Signature, HalfTransparentImage(), 100, 50);
        var blackUnderSignature = new ShapeItem(ItemId.New(), page, 100, 500, ShapeKind.Rectangle, 100, 50, InkColor.Black, 50, false, false);
        WrittenDocument written = DocumentWriter.FinalCopy(source, pages, state.WithItems([text, highlight, blackUnderSignature, signature]));

        string raw = TestFiles.ReadableText(written.Pdf);
        Assert.Matches(@"/BM\s*/Multiply", raw);
        Assert.Contains("/SMask", raw, StringComparison.Ordinal);

        using PdfiumDocument result = PdfiumDocument.Open(written.Pdf);
        Bitmap image = Render(result, 0, new PageTransform(pages[0], Rotation.None).DisplaySize);
        (byte r, byte g, byte b) = image.At(260, 440);
        Assert.True(r > 240 && g > 220 && b < 170 && b > 110, $"highlight on paper looks like the screen's yellow: {(r, g, b)}");
        Assert.True(image.CountDark(100, 400, 150, 45) > 300, "text under the highlight stays readable");

        // Left half of the signature image is opaque red; the right half lets the black box show through.
        Assert.True(image.At(120, 525).r > 200 && image.At(120, 525).b < 80, "opaque half");
        Assert.True(image.IsDark(180, 525), "transparent half");
    }

    [Fact]
    public void A_working_copy_keeps_the_clean_original_restores_exactly_and_does_not_grow()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        var tick = new MarkItem(ItemId.New(), state.Pages[2].Id, 100, 100, MarkKind.Tick, 24, InkColor.Blue);
        DocumentState document = state.WithPages([state.Pages[2], state.Pages[0] with { Rotation = Rotation.FromQuarterTurns(2) }]).WithItems([tick]);

        byte[] working = DocumentWriter.WorkingCopy(source, pages, document).Pdf;
        Assert.True(source.AsSpan().SequenceEqual(working.AsSpan(0, source.Length)), "the clean original comes first, byte for byte");

        // The updates use cross-reference streams like the compact original: a classic table pointing back to a stream
        // is a mix macOS PDFKit won't open.
        Assert.DoesNotContain("trailer", Encoding.Latin1.GetString(working, source.Length, working.Length - source.Length), StringComparison.Ordinal);

        // Other readers see the finished pages.
        using (PdfiumDocument asOthersSeeIt = PdfiumDocument.Open(working))
        {
            Assert.Equal(2, asOthersSeeIt.PageCount);
            Assert.Equal("Page C", FirstLine(asOthersSeeIt.GetText(0)));
            Assert.Equal(2, asOthersSeeIt.GetPageInfo(1).Rotation);
        }

        SessionLookup lookup = SessionStore.Find(working);
        Assert.Equal(SessionStatus.Intact, lookup.Status);
        Assert.True(lookup.IsOriginalAvailable);
        Assert.Equal(document.Pages, lookup.Record!.Document.Pages);
        Assert.Equal(tick, Assert.Single(lookup.Record.Document.Items));

        // Saving the restored document again starts from the clean original, so the file stays the same size.
        byte[] restoredBase = working[..(int)lookup.Record.Base.Length];
        byte[] again = DocumentWriter.WorkingCopy(restoredBase, pages, lookup.Record.Document).Pdf;
        Assert.InRange(again.Length, working.Length - 64, working.Length + 64);
    }

    [Fact]
    public void A_final_copy_from_a_working_copy_has_no_record_and_no_deleted_pages()
    {
        (byte[] source, SourcePage[] pages) = Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(4);
        DocumentState onlyB = state.WithPages([state.Pages[1]]);
        byte[] final = DocumentWriter.FinalCopy(source, pages, onlyB).Pdf;

        string raw = TestFiles.ReadableText(final);
        Assert.DoesNotContain("FillsignSession", raw, StringComparison.Ordinal);
        using PdfiumDocument result = PdfiumDocument.Open(final);
        Assert.Equal(1, result.PageCount);
        Assert.True(final.Length < source.Length, "deleted pages aren't carried along");
    }

    [Theory]
    [InlineData("corpus/160F-2019.pdf")]
    [InlineData("corpus/form_two_pages.pdf")]
    public void Form_fields_keep_working_after_pages_are_reordered(string name)
    {
        Assert.SkipUnless(File.Exists(TestFiles.Sample(name)), "Corpus not downloaded; run build/fetch-corpus.sh");
        (byte[] source, SourcePage[] pages) = Source(name);
        var state = DocumentState.ForSourcePages(pages.Length);
        DocumentState reversed = state.WithPages([.. state.Pages.Reverse()]);
        WrittenDocument written = DocumentWriter.FinalCopy(source, pages, reversed);

        Assert.False(written.HaveFormsMoved);
        Assert.Contains("/AcroForm", TestFiles.ReadableText(written.Pdf), StringComparison.Ordinal);
        using PdfiumDocument before = PdfiumDocument.Open(source);
        using PdfiumDocument after = PdfiumDocument.Open(written.Pdf);
        Assert.Equal(before.GetAnnotationCount(pages.Length - 1), after.GetAnnotationCount(0));
    }

    internal static (byte[] Clean, SourcePage[] Pages) Source(string name)
    {
        using PdfiumDocument original = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample(name)));
        // As the app and the command hold it (FS-967).
        byte[] clean = PdfCompactor.Compact(original.SaveCopy(removeSecurity: true));
        using PdfiumDocument reopened = PdfiumDocument.Open(clean);
        return (clean, [.. Enumerable.Range(0, reopened.PageCount).Select(i => reopened.GetPageInfo(i).ToSourcePage())]);
    }

    internal static Bitmap Render(PdfiumDocument pdf, int page, PageSize display)
    {
        int width = (int)Math.Round(display.Width * Scale), height = (int)Math.Round(display.Height * Scale);
        var pixels = new byte[width * height * 4];
        pdf.RenderPage(page, pixels, width, height, width * 4);
        return new Bitmap(pixels, width, height);
    }

    private static string FirstLine(string text) => text.Split('\r', '\n')[0].Trim();

    internal static SignatureImage HalfTransparentImage()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        for (int x = 0; x < 40; x++)
        {
            for (int y = 0; y < 20; y++)
            {
                bitmap.SetPixel(x, y, x < 20 ? new SKColor(220, 20, 20, 255) : new SKColor(220, 20, 20, 0));
            }
        }

        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new SignatureImage(Guid.NewGuid(), [.. png.ToArray()], 40, 20);
    }

    /// <summary>A rendered page, read in display points.</summary>
    internal sealed class Bitmap(byte[] pixels, int width, int height)
    {
        public int Width => width;

        public int Height => height;

        public byte[] Pixels => pixels;

        public (byte r, byte g, byte b) At(double x, double y)
        {
            int px = Math.Clamp((int)(x * Scale), 0, width - 1), py = Math.Clamp((int)(y * Scale), 0, height - 1);
            int i = ((py * width) + px) * 4;
            return (pixels[i + 2], pixels[i + 1], pixels[i]);
        }

        public bool IsDark(double x, double y)
        {
            (byte r, byte g, byte b) = At(x, y);
            return r + g + b < 200;
        }

        public int CountDark(double x, double y, double w, double h)
        {
            int count = 0;
            for (double py = y; py < y + h; py += 1 / Scale)
            {
                for (double px = x; px < x + w; px += 1 / Scale)
                {
                    count += IsDark(px, py) ? 1 : 0;
                }
            }

            return count;
        }

        /// <summary>The first row (in points) between top and bottom with dark pixels between left and right.</summary>
        public double FirstDarkRow(double left, double right, double top, double bottom)
        {
            for (double y = top; y < bottom; y += 1 / Scale)
            {
                for (double x = left; x < right; x += 1 / Scale)
                {
                    if (IsDark(x, y) || At(x, y).b > At(x, y).r + 60)
                    {
                        return y;
                    }
                }
            }

            return bottom;
        }
    }
}
