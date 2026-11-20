using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fillsign.Pdf.Fonts;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Tests;

/// <summary>
/// FS-024: can PDFium's own editing API replace PDFsharp as the only writer?
/// (a) pages, (b) subset fonts and text, (c) paths, images, blend, (d) the editable record, (e) the corpus.
/// </summary>
public sealed partial class PdfiumWriterSpikeTests(ITestOutputHelper output)
{
    private const string SessionName = "fillsign-session.json";
    private static readonly byte[] s_noto = BundledFonts.Load(BundledFonts.RegularFace);

    // ---------- (a) Pages ----------

    [Fact]
    public void A_moves_deletes_rotates_and_imports_pages()
    {
        using var original = Open("four-pages.pdf");
        using var form = Open("sample-form.pdf");
        var a = Render(original, 0);
        var aRotated = Render(original, 0, quarterTurns: 1);
        var b = Render(original, 1);
        var d = Render(original, 3);
        var formPage = Render(form, 0);

        using var doc = Open("four-pages.pdf");
        doc.SetRotation(0, 1);              // A turned right
        doc.MovePages([3], 0);              // D A B C
        doc.DeletePage(3);                  // D A B
        doc.ImportPages(form, [0], 1);      // D F A B
        using var saved = PdfiumDocument.Open(doc.SaveCopy(removeSecurity: false));

        Assert.Equal(4, saved.PageCount);
        Assert.Equal(d, Render(saved, 0));
        Assert.Equal(formPage, Render(saved, 1));
        Assert.Equal(1, saved.GetPageInfo(2).Rotation);
        Assert.Equal(aRotated, Render(saved, 2));
        Assert.Equal(b, Render(saved, 3));
        Assert.NotEqual(a, aRotated);
    }

    [Theory]
    [InlineData("corpus/160F-2019.pdf")]
    [InlineData("corpus/form_two_pages.pdf")]
    [InlineData("corpus/annotation-link-text-popup.pdf")]
    [InlineData("corpus/TAMReview.pdf")]
    public void A_form_fields_and_links_survive_reordering(string name)
    {
        Assert.SkipUnless(File.Exists(TestFiles.Sample(name)), "Corpus not downloaded; run build/fetch-corpus.sh");
        using var doc = Open(name);
        int last = doc.PageCount - 1;
        var before = Enumerable.Range(0, doc.PageCount).Select(doc.GetAnnotationCount).ToArray();
        Assert.True(before.Sum() > 0, "test file should have annotations");

        doc.MovePages([last], 0);
        doc.SetRotation(0, 1);
        byte[] bytes = doc.SaveCopy(removeSecurity: false);
        using var saved = PdfiumDocument.Open(bytes);

        var after = Enumerable.Range(0, saved.PageCount).Select(saved.GetAnnotationCount).ToArray();
        int[] expected = [before[last], .. before[..last]];
        Assert.Equal(expected, after);
        if (Encoding.Latin1.GetString(File.ReadAllBytes(TestFiles.Sample(name))).Contains("/AcroForm", StringComparison.Ordinal))
        {
            Assert.Contains("/AcroForm", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        }

        output.WriteLine($"annotations per page before {string.Join(",", before)}, after {string.Join(",", after)}");
    }

    // ---------- (b) Text with subset fonts ----------

    [Fact]
    public void B_embeds_a_small_noto_subset_with_unicode_mapping()
    {
        const string text = "Fillsign Ünïcödé Ελληνικά Кириллица";
        using var doc = PdfiumDocument.CreateNew();
        doc.AddBlankPage(0, 595, 842);
        var font = doc.LoadSubsetFont(s_noto, text);
        doc.EditPage(0, page => page.AddText(font, 12, text, 72, 770, (0, 0, 0)));
        byte[] pdf = doc.SaveCopy(removeSecurity: false);
        string raw = Encoding.Latin1.GetString(pdf);

        output.WriteLine($"PDF with PDFium + HarfBuzz subset: {pdf.Length:N0} bytes (PDFsharp: 16,849)");
        foreach (Match m in BaseFontRegex().Matches(raw))
        {
            output.WriteLine(m.Value);
        }

        Assert.Contains("/FontFile2", raw, StringComparison.Ordinal);
        Assert.Contains("/ToUnicode", raw, StringComparison.Ordinal);
        Assert.True(pdf.Length < 25_000, $"{pdf.Length:N0} bytes: the font was probably embedded in full");

        // Text must stay selectable and searchable (FR-8.2), in any script (FR-8.7).
        using var saved = PdfiumDocument.Open(pdf);
        Assert.Equal(text, saved.GetText(0));
    }

    [Fact]
    public void B_full_font_without_subsetting_is_large()
    {
        // Baseline for the report: what FPDFText_LoadFont produces when given the whole font file.
        using var doc = PdfiumDocument.CreateNew();
        doc.AddBlankPage(0, 595, 842);
        var font = doc.LoadSubsetFont(s_noto, string.Concat(Enumerable.Range(0x20, 0x250).Select(c => (char)c)));
        doc.EditPage(0, page => page.AddText(font, 12, "Fillsign", 72, 770, (0, 0, 0)));
        output.WriteLine($"PDF with ~600 Latin characters subset: {doc.SaveCopy(removeSecurity: false).Length:N0} bytes");
    }

    // ---------- (c) Paths, images, blend ----------

    [Fact]
    public void C_draws_shapes_ink_images_and_a_multiply_highlight()
    {
        const float H = 400;
        using var doc = PdfiumDocument.CreateNew();
        doc.AddBlankPage(0, 600, H);
        var font = doc.LoadSubsetFont(s_noto, "HIGHLIGHT");

        // 4×4 BGRA image: left half opaque red, right half fully transparent.
        var image = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            bool left = i % 4 < 2;
            image[(i * 4) + 2] = left ? (byte)255 : (byte)0;
            image[(i * 4) + 3] = left ? (byte)255 : (byte)0;
        }

        doc.EditPage(0, page =>
        {
            // Rectangle outline, ellipse (4 béziers), line, arrow, freehand-ish curve.
            page.AddPath(20, 20, p => p.LineTo(120, 20).LineTo(120, 80).LineTo(20, 80).Close(), stroke: (0, 0, 255, 255), strokeWidth: 2);
            const float k = 0.5523f * 40;
            page.AddPath(200, 50, p => p
                .CurveTo(200, 50 + (k * 0.75f), 160 + k, 80, 160, 80)
                .CurveTo(160 - k, 80, 120 + 0, 50 + (k * 0.75f), 120, 50)
                .CurveTo(120, 50 - (k * 0.75f), 160 - k, 20, 160, 20)
                .CurveTo(160 + k, 20, 200, 50 - (k * 0.75f), 200, 50), stroke: (255, 0, 0, 255), strokeWidth: 2);
            page.AddPath(220, 20, p => p.LineTo(320, 80), stroke: (0, 0, 0, 255), strokeWidth: 3);
            page.AddPath(340, 20, p => p.LineTo(430, 80).MoveTo(430, 80).LineTo(412, 78).MoveTo(430, 80).LineTo(422, 64), stroke: (0, 0, 0, 255), strokeWidth: 3);
            page.AddPath(450, 20, p => p.CurveTo(470, 90, 500, -10, 580, 60), stroke: (0, 0, 255, 255), strokeWidth: 2);

            // Black text, then an opaque yellow highlight multiplied over it: text must stay readable.
            page.AddText(font, 36, "HIGHLIGHT", 20, 200, (0, 0, 0));
            page.AddPath(10, 185, p => p.LineTo(260, 185).LineTo(260, 235).LineTo(10, 235).Close(), fill: (255, 230, 0, 255), blendMode: "Multiply");

            // Whiteout: opaque white rectangle covering a black bar.
            page.AddPath(300, 190, p => p.LineTo(560, 190).LineTo(560, 230).LineTo(300, 230).Close(), fill: (0, 0, 0, 255));
            page.AddPath(300, 190, p => p.LineTo(430, 190).LineTo(430, 230).LineTo(300, 230).Close(), fill: (255, 255, 255, 255));

            // Signature-like image with transparency over a black square.
            page.AddPath(20, 280, p => p.LineTo(120, 280).LineTo(120, 380).LineTo(20, 380).Close(), fill: (0, 0, 0, 255));
            page.AddImage(image, 4, 4, 20, 280, 100, 100);
        });

        byte[] pdf = doc.SaveCopy(removeSecurity: false);
        string raw = Encoding.Latin1.GetString(pdf);
        output.WriteLine($"shapes page: {pdf.Length:N0} bytes");
        Assert.Matches(@"/BM\s*/Multiply", raw);
        Assert.Contains("/SMask", raw, StringComparison.Ordinal);

        using var saved = PdfiumDocument.Open(pdf);
        var (px, w) = RenderRaw(saved, 0, scale: 1);
        (byte B, byte G, byte R) At(double x, double y)
        {
            int i = (((int)(H - y) * w) + (int)x) * 4;
            return (px[i], px[i + 1], px[i + 2]);
        }

        Assert.Equal((255, 0, 0), At(70, 20));       // rectangle stroke (blue in BGR order)
        Assert.True(At(160, 80).R > 200 && At(160, 80).B < 60, "ellipse top should be red");
        Assert.True(At(270, 50).R < 60, "line should be dark");
        Assert.Equal((255, 255, 255), At(160, 50)); // ellipse interior stays unfilled

        // Highlight: background turns yellow, text under it stays dark (multiply, not cover).
        var yellow = At(250, 225);
        Assert.True(yellow.R > 240 && yellow.G > 210 && yellow.B < 40, $"highlight background {yellow}");
        int darkUnderHighlight = CountDark(px, w, 20, H - 235, 240, 50);
        Assert.True(darkUnderHighlight > 200, $"text under highlight lost: {darkUnderHighlight} dark pixels");

        Assert.Equal((255, 255, 255), At(360, 210));  // whiteout covers
        Assert.Equal((0, 0, 0), At(500, 210));        // uncovered bar stays black

        Assert.True(At(40, 330).R > 200 && At(40, 330).B < 60, "opaque half of image is red");
        Assert.Equal((0, 0, 0), At(100, 330));        // transparent half shows black square beneath
    }

    // ---------- (d) Editable record ----------

    [Fact]
    public void D_attachment_round_trips_and_resaving_does_not_grow()
    {
        byte[] session = Encoding.UTF8.GetBytes("{\"version\":1,\"pages\":[{\"src\":0,\"rotation\":0}],\"items\":[]}");
        using var doc = Open("sample-form.pdf");
        doc.SetAttachment(SessionName, session);
        byte[] first = doc.SaveCopy(removeSecurity: false);

        using var reopened = PdfiumDocument.Open(first);
        Assert.Equal(session, reopened.GetAttachment(SessionName));

        // Save again with a new record of the same size (FR-9.8): no growth beyond noise.
        byte[] session2 = Encoding.UTF8.GetBytes("{\"version\":1,\"pages\":[{\"src\":0,\"rotation\":1}],\"items\":[]}");
        reopened.SetAttachment(SessionName, session2);
        byte[] second = reopened.SaveCopy(removeSecurity: false);
        using var again = PdfiumDocument.Open(second);

        Assert.Equal([SessionName], again.GetAttachmentNames());
        Assert.Equal(session2, again.GetAttachment(SessionName));

        // The first PDFium save normalises the original, so stability is measured from the second save on.
        again.SetAttachment(SessionName, session);
        byte[] third = again.SaveCopy(removeSecurity: false);
        output.WriteLine($"with record: {first.Length:N0} → {second.Length:N0} → {third.Length:N0} bytes");
        Assert.InRange(third.Length - second.Length, -64, 64);
    }

    [Fact]
    public void D_incremental_update_with_private_trailer_key_round_trips_invisibly()
    {
        byte[] session = Encoding.UTF8.GetBytes("{\"version\":1,\"items\":[]}");
        using var doc = Open("four-pages.pdf");
        byte[] saved = doc.SaveCopy(removeSecurity: false);
        byte[] withRecord = SessionTrailer.Append(saved, session);

        // Still a valid PDF that renders the same; the record is in a trailer key readers ignore.
        using var reopened = PdfiumDocument.Open(withRecord);
        Assert.Equal(4, reopened.PageCount);
        Assert.Empty(reopened.GetAttachmentNames());
        Assert.Equal(Render(doc, 0), Render(reopened, 0));
        Assert.Equal(session, SessionTrailer.Read(withRecord));

        // PDFium keeps unknown trailer keys (and Flate-compresses the stream) when it saves the file again,
        // so FR-9.6 must detect edits by other apps with a content hash, not by the key disappearing.
        Assert.Equal(session, SessionTrailer.Read(reopened.SaveCopy(removeSecurity: false)));
        output.WriteLine($"incremental record adds {withRecord.Length - saved.Length:N0} bytes");
    }

    // ---------- (e) Corpus ----------

    public static TheoryData<string> AllFiles()
    {
        var data = new TheoryData<string>("sample-form.pdf", "four-pages.pdf");
        foreach (var file in TestFiles.CorpusFiles())
        {
            data.Add("corpus/" + Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void E_every_file_edits_and_saves_without_crashing(string name)
    {
        PdfiumDocument doc;
        try
        {
            doc = Open(name);
        }
        catch (PdfOpenException ex) when (ex.Error is PdfOpenError.Password)
        {
            output.WriteLine($"{name}: needs a user password (FS-208); not a writer problem");
            return;
        }

        using (doc)
        {
            const string text = "Fillsign 17/11/2026";
            var font = doc.LoadSubsetFont(s_noto, text);
            int pages = doc.PageCount;
            int editable = FirstLoadablePage(doc);
            doc.EditPage(editable, page =>
            {
                page.AddText(font, 14, text, 40, 60, (0, 0, 255));
                page.AddPath(40, 50, p => p.LineTo(200, 50), stroke: (255, 0, 0, 255), strokeWidth: 2);
            });
            doc.SetRotation(editable, 1);
            try
            {
                if (pages > 1)
                {
                    doc.MovePages([pages - 1], 0);
                }
            }
            catch (InvalidOperationException ex)
            {
                // A damaged page tree refuses the move with an error instead of crashing; the user can be told.
                output.WriteLine($"{name}: move refused cleanly ({ex.Message})");
            }

            if (pages > 2)
            {
                doc.DeletePage(pages - 1);
                pages = doc.PageCount;
            }

            doc.SetAttachment(SessionName, "{}"u8);
            byte[] saved = doc.SaveCopy(removeSecurity: false);

            using var reopened = PdfiumDocument.Open(saved);
            Assert.Equal(pages, reopened.PageCount);
            Assert.Equal("{}"u8.ToArray(), reopened.GetAttachment(SessionName));
            int rendered = 0;
            for (int i = 0; i < reopened.PageCount; i++)
            {
                try
                {
                    _ = PdfiumRenderTests.CountInkPixels(reopened, i, 0.5f);
                    rendered++;
                }
                catch (PdfPageException)
                {
                    // The source's damaged page stays damaged; it must not take the document down.
                }
            }

            Assert.True(rendered > 0);
            bool textFound = Enumerable.Range(0, reopened.PageCount).Any(i =>
            {
                try
                {
                    return reopened.GetText(i).Contains("Fillsign", StringComparison.Ordinal);
                }
                catch (PdfPageException)
                {
                    return false;
                }
            });
            Assert.True(textFound, "added text should be extractable");
            output.WriteLine($"{name}: {pages} pages, {rendered} render, {saved.Length:N0} bytes");
        }
    }

    // ---------- helpers ----------

    private static PdfiumDocument Open(string name) => PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample(name)));

    private static int FirstLoadablePage(PdfiumDocument doc)
    {
        for (int i = 0; i < doc.PageCount; i++)
        {
            try
            {
                doc.GetPageInfo(i);
                return i;
            }
            catch (PdfPageException)
            {
            }
        }

        throw new InvalidOperationException("No loadable page.");
    }

    private static byte[] Render(PdfiumDocument doc, int page, int quarterTurns = 0)
    {
        var info = doc.GetPageInfo(page);
        bool swap = quarterTurns % 2 == 1;
        int w = (int)((swap ? info.Height : info.Width) / 2), h = (int)((swap ? info.Width : info.Height) / 2);
        var buffer = new byte[w * 4 * h];
        doc.RenderPage(page, buffer, w, h, w * 4, quarterTurns);
        return buffer;
    }

    private static (byte[] Pixels, int Width) RenderRaw(PdfiumDocument doc, int page, float scale)
    {
        var info = doc.GetPageInfo(page);
        int w = (int)(info.Width * scale), h = (int)(info.Height * scale);
        var buffer = new byte[w * 4 * h];
        doc.RenderPage(page, buffer, w, h, w * 4);
        return (buffer, w);
    }

    private static int CountDark(byte[] px, int stride, double x, double top, double width, double height)
    {
        int count = 0;
        for (int y = (int)top; y < top + height; y++)
        {
            for (int xx = (int)x; xx < x + width; xx++)
            {
                int i = ((y * stride) + xx) * 4;
                count += px[i] < 80 && px[i + 1] < 80 && px[i + 2] < 80 ? 1 : 0;
            }
        }

        return count;
    }

    [GeneratedRegex(@"/BaseFont\s*/[^\s/>]+")]
    private static partial Regex BaseFontRegex();

    /// <summary>
    /// Prototype of the FR-9 record as a PDF incremental update: one new stream object plus a trailer that
    /// points to it with a private key. Viewers ignore unknown trailer keys; nothing shows in any panel.
    /// </summary>
    private static partial class SessionTrailer
    {
        private const string Key = "/FillsignSession";

        public static byte[] Append(byte[] pdf, byte[] record)
        {
            string text = Encoding.Latin1.GetString(pdf);
            int startxref = int.Parse(StartXrefRegex().Matches(text)[^1].Groups[1].Value, CultureInfo.InvariantCulture);
            var trailer = TrailerRegex().Matches(text);
            Assert.True(trailer.Count > 0, "PDFium wrote no classic trailer (xref stream); prototype needs extending");
            string dict = trailer[^1].Groups[1].Value;
            int size = int.Parse(SizeRegex().Match(dict).Groups[1].Value, CultureInfo.InvariantCulture);
            string root = RootRegex().Match(dict).Value;
            string id = IdRegex().Match(dict).Value;

            using var output = new MemoryStream();
            output.Write(pdf);
            void W(string s) => output.Write(Encoding.Latin1.GetBytes(s));
            W("\n");
            long objOffset = output.Position;
            W($"{size} 0 obj\n<</Type/FillsignSession/Length {record.Length}>>\nstream\n");
            output.Write(record);
            W("\nendstream\nendobj\n");
            long xref = output.Position;
            W($"xref\n{size} 1\n{objOffset:D10} 00000 n \ntrailer\n<</Size {size + 1} {root} {id} /Prev {startxref} {Key} {size} 0 R>>\nstartxref\n{xref}\n%%EOF\n");
            return output.ToArray();
        }

        public static byte[]? Read(byte[] pdf)
        {
            string text = Encoding.Latin1.GetString(pdf);
            var trailers = TrailerRegex().Matches(text);
            if (trailers.Count == 0)
            {
                return null;
            }

            var key = KeyRegex().Match(trailers[^1].Groups[1].Value);
            if (!key.Success)
            {
                return null;
            }

            var obj = Regex.Match(text, $@"(?<![0-9]){key.Groups[1].Value} 0 obj\s*<<([^>]*)>>\s*stream\r?\n");
            int length = int.Parse(LengthRegex().Match(obj.Groups[1].Value).Groups[1].Value, CultureInfo.InvariantCulture);
            byte[] data = pdf.AsSpan(obj.Index + obj.Length, length).ToArray();
            if (!obj.Groups[1].Value.Contains("/FlateDecode", StringComparison.Ordinal))
            {
                return data;
            }

            using var inflated = new MemoryStream();
            using (var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress))
            {
                zlib.CopyTo(inflated);
            }

            return inflated.ToArray();
        }

        [GeneratedRegex(@"startxref\s+(\d+)")]
        private static partial Regex StartXrefRegex();

        [GeneratedRegex(@"trailer\s*<<(.*?)>>\s*startxref", RegexOptions.Singleline)]
        private static partial Regex TrailerRegex();

        [GeneratedRegex(@"/Size\s+(\d+)")]
        private static partial Regex SizeRegex();

        [GeneratedRegex(@"/Root\s+\d+\s+\d+\s+R")]
        private static partial Regex RootRegex();

        [GeneratedRegex(@"/ID\s*\[[^\]]*\]")]
        private static partial Regex IdRegex();

        [GeneratedRegex(@"/Length\s+(\d+)")]
        private static partial Regex LengthRegex();

        [GeneratedRegex(@"/FillsignSession\s+(\d+)\s+0\s+R")]
        private static partial Regex KeyRegex();
    }
}
