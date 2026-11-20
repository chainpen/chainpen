using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Core.Session;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Tests;

/// <summary>
/// Checks Core's PDF-facing logic against real PDFium: <see cref="PageTransform"/> (FS-102) and the session update
/// (FS-107) on the corpus.
/// </summary>
public sealed class CoreIntegrationTests(ITestOutputHelper output)
{
    public static TheoryData<int, int> AllRotations()
    {
        var data = new TheoryData<int, int>();
        for (int rotate = 0; rotate < 4; rotate++)
        {
            for (int extra = 0; extra < 4; extra++)
            {
                data.Add(rotate, extra);
            }
        }

        return data;
    }

    /// <summary>
    /// Draws a square through PageTransform on a cropped, rotated page, renders it with the extra rotation, and finds
    /// the square exactly where the editor would show it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRotations))]
    public void Content_drawn_through_the_transform_lands_where_the_editor_shows_it(int rotate, int extra)
    {
        using var doc = PdfiumDocument.CreateNew();
        doc.AddBlankPage(0, 400, 300);
        doc.SetCropBox(0, 50, 70, 350, 270); // 300 × 200, offset
        doc.SetRotation(0, rotate);
        var transform = new PageTransform(doc.GetPageInfo(0).ToSourcePage(), Rotation.FromQuarterTurns(extra));

        // A 10 × 10 square at display (20, 10): its corners mapped one by one into PDF space.
        PagePoint[] corners = [new(20, 10), new(30, 10), new(30, 20), new(20, 20)];
        var pdf = corners.Select(transform.ToPdf).ToArray();
        doc.EditPage(0, page => page.AddPath(
            (float)pdf[0].X,
            (float)pdf[0].Y,
            p => p.LineTo((float)pdf[1].X, (float)pdf[1].Y).LineTo((float)pdf[2].X, (float)pdf[2].Y).LineTo((float)pdf[3].X, (float)pdf[3].Y).Close(),
            fill: (0, 0, 0, 255)));

        int width = (int)transform.DisplaySize.Width, height = (int)transform.DisplaySize.Height;
        var info = doc.GetPageInfo(0);
        bool swapped = extra % 2 == 1;
        Assert.Equal(width, (int)Math.Round(swapped ? info.Height : info.Width));

        var pixels = new byte[width * 4 * height];
        doc.RenderPage(0, pixels, width, height, width * 4, extra);
        var dark = Enumerable.Range(0, width * height).Where(i => pixels[i * 4] < 128).ToList();
        Assert.InRange(dark.Count, 80, 121);
        double cx = dark.Average(i => i % width) + 0.5, cy = dark.Average(i => i / width) + 0.5;
        output.WriteLine($"/Rotate {rotate * 90} + {extra * 90}: centre ({cx:F1}, {cy:F1})");
        Assert.InRange(cx, 24, 26);
        Assert.InRange(cy, 14, 16);
    }

    public static TheoryData<string> AllFiles()
    {
        var data = new TheoryData<string>("sample-form.pdf", "four-pages.pdf");
        foreach (string file in TestFiles.CorpusFiles())
        {
            data.Add("corpus/" + Path.GetFileName(file));
        }

        return data;
    }

    /// <summary>
    /// The session update is appended to what the PDFium writer saves, for every corpus file (classic tables and xref
    /// streams). PDFium must still open it unchanged, and Fillsign must find the record again.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFiles))]
    public void Session_update_is_invisible_and_found_again(string name)
    {
        byte[] saved;
        int pages;
        IReadOnlyList<string> attachments;
        try
        {
            using var source = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample(name)));
            pages = source.PageCount;
            attachments = source.GetAttachmentNames();
            saved = source.SaveCopy(removeSecurity: true);
        }
        catch (PdfOpenException ex) when (ex.Error is PdfOpenError.Password)
        {
            output.WriteLine($"{name}: needs a password (FS-208)");
            return;
        }

        var document = DocumentState.ForSourcePages(pages);
        byte[] working = SessionStore.Append(saved, ByteStamp.Of(saved), pages, document);

        using (var reopened = PdfiumDocument.Open(working))
        {
            Assert.Equal(pages, reopened.PageCount);
            Assert.Equal(attachments, reopened.GetAttachmentNames()); // nothing new shows in attachment panels
        }

        SessionLookup lookup = SessionStore.Find(working);
        Assert.Equal(SessionStatus.Intact, lookup.Status);
        Assert.Equal(document, lookup.Record!.Document);

        // Resaved by another PDFium-based app: the key survives, but the change is detected.
        using var other = PdfiumDocument.Open(working);
        Assert.NotEqual(SessionStatus.Intact, SessionStore.Find(other.SaveCopy(removeSecurity: false)).Status);
        output.WriteLine($"{name}: +{working.Length - saved.Length:N0} bytes");
    }
}
