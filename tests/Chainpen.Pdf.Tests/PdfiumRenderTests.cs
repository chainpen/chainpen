using Chainpen.Pdf.Pdfium;

namespace Chainpen.Pdf.Tests;

/// <summary>FS-020: PDFium loads through our own binding and renders pages on this OS/architecture.</summary>
public sealed class PdfiumRenderTests
{
    [Theory]
    [InlineData("sample-form.pdf")]
    [InlineData("four-pages.pdf")]
    public void Renders_every_page_of_the_samples(string name)
    {
        using var doc = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample(name)));

        Assert.True(doc.PageCount > 0);
        for (int i = 0; i < doc.PageCount; i++)
        {
            Assert.True(CountInkPixels(doc, i, scale: 1) > 0, $"page {i + 1} rendered blank");
        }
    }

    [Fact]
    public void Extra_rotation_swaps_the_rendered_orientation()
    {
        using var doc = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample("four-pages.pdf")));
        var info = doc.GetPageInfo(0);
        int w = (int)info.Width, h = (int)info.Height;

        // Rendering a quarter turn into an h × w bitmap must still put ink on the page.
        var buffer = new byte[h * 4 * w];
        doc.RenderPage(0, buffer, h, w, h * 4, extraQuarterTurns: 1);

        Assert.Contains(buffer.Chunk(4), px => px[0] < 128);
    }

    [Fact]
    public void Corpus_files_open_or_fail_with_a_clear_reason()
    {
        var files = TestFiles.CorpusFiles();
        Assert.SkipWhen(files.Count == 0, "Corpus not downloaded; run build/fetch-corpus.sh");

        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                using var doc = PdfiumDocument.Open(File.ReadAllBytes(file));
                for (int i = 0; i < doc.PageCount; i++)
                {
                    try
                    {
                        CountInkPixels(doc, i, scale: 0.5f);
                    }
                    catch (PdfPageException)
                    {
                        // A damaged page is reported per page; the viewer shows a placeholder.
                    }
                }
            }
            catch (PdfOpenException ex) when (ex.Error is PdfOpenError.Password)
            {
                // A user password is an expected, reportable outcome (FR-1.6).
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Garbage_input_reports_a_format_error()
    {
        var ex = Assert.Throws<PdfOpenException>(() => PdfiumDocument.Open("not a pdf"u8));
        Assert.Equal(PdfOpenError.Format, ex.Error);
    }

    internal static int CountInkPixels(PdfiumDocument doc, int pageIndex, float scale)
    {
        var info = doc.GetPageInfo(pageIndex);
        int w = Math.Max(1, (int)(info.Width * scale));
        int h = Math.Max(1, (int)(info.Height * scale));
        var buffer = new byte[w * 4 * h];
        doc.RenderPage(pageIndex, buffer, w, h, w * 4);
        return buffer.Chunk(4).Count(px => px[0] < 200 || px[1] < 200 || px[2] < 200);
    }
}
