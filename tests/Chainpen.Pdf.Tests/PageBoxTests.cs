using Chainpen.Core.Model;
using Chainpen.Pdf.Pdfium;

namespace Chainpen.Pdf.Tests;

/// <summary>
/// The page's box is where everything added to it is placed, so it has to be the box PDFium lays the page out in:
/// the crop box cut to the media box, wherever in the page tree they're set.
/// </summary>
public sealed class PageBoxTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";

    [Fact]
    public void A_page_size_set_on_the_page_tree_is_the_pages_box()
    {
        // A4 with its corner away from 0,0, set once on the tree as many PDF writers do; the page has no box of its own.
        using PdfiumDocument pdf = PdfiumDocument.Open(TestFiles.ClassicPdf(
            Catalog,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [10 20 605 862] >>",
            "<< /Type /Page /Parent 2 0 R >>"));

        PdfPageInfo info = pdf.GetPageInfo(0);
        Assert.Equal((10f, 20f, 605f, 862f), info.CropBox);
        Assert.Equal(new PageSize(info.Width, info.Height), info.ToSourcePage().DisplaySize);
        Assert.Equal(new PageSize(595, 842), info.ToSourcePage().DisplaySize);
    }

    [Fact]
    public void An_inherited_crop_box_and_rotation_give_the_size_shown()
    {
        using PdfiumDocument pdf = PdfiumDocument.Open(TestFiles.ClassicPdf(
            Catalog,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 595 842] /CropBox [50 60 350 460] /Rotate 90 >>",
            "<< /Type /Page /Parent 2 0 R >>"));

        PdfPageInfo info = pdf.GetPageInfo(0);
        Assert.Equal((50f, 60f, 350f, 460f), info.CropBox);
        Assert.Equal(new PageSize(400, 300), info.ToSourcePage().DisplaySize);
        Assert.Equal(new PageSize(info.Width, info.Height), info.ToSourcePage().DisplaySize);
    }

    [Fact]
    public void A_crop_box_is_cut_to_the_media_box_and_a_box_written_backwards_still_has_a_size()
    {
        using PdfiumDocument pdf = PdfiumDocument.Open(TestFiles.ClassicPdf(
            Catalog,
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /CropBox [-50 -50 500 500] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [595 842 0 0] >>"));

        Assert.Equal((0f, 0f, 300f, 400f), pdf.GetPageInfo(0).CropBox);
        Assert.Equal((0f, 0f, 595f, 842f), pdf.GetPageInfo(1).CropBox);
    }

    [Fact]
    public void Every_sample_and_corpus_page_has_the_size_PDFium_shows_it_at()
    {
        string[] files = [.. Directory.GetFiles(TestFiles.Samples, "*.pdf"), .. TestFiles.CorpusFiles()];
        var wrong = new List<string>();
        foreach (string file in files)
        {
            PdfiumDocument pdf;
            try
            {
                pdf = PdfiumDocument.Open(File.ReadAllBytes(file));
            }
            catch (PdfOpenException)
            {
                continue; // Needs a password, or PDFium can't open it: no pages to measure.
            }

            using (pdf)
            {
                for (int page = 0; page < pdf.PageCount; page++)
                {
                    PdfPageInfo info;
                    try
                    {
                        info = pdf.GetPageInfo(page);
                    }
                    catch (PdfPageException)
                    {
                        continue; // A damaged page is shown as a placeholder.
                    }

                    PageSize size = info.ToSourcePage().DisplaySize;
                    if (Math.Abs(size.Width - info.Width) > 0.01 || Math.Abs(size.Height - info.Height) > 0.01)
                    {
                        wrong.Add($"{Path.GetFileName(file)} page {page + 1}: box {size.Width}×{size.Height}, shown {info.Width}×{info.Height}");
                    }
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
