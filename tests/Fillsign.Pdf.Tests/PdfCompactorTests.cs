using System.Security.Cryptography;
using Fillsign.Pdf.Export;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Tests;

/// <summary>FS-967: saved files packed as compactly as the original, with nothing in them changed.</summary>
public sealed class PdfCompactorTests
{
    [Fact]
    public void A_form_saved_by_PDFium_shrinks_and_keeps_its_pages_and_fields()
    {
        byte[] saved;
        using (PdfiumDocument pdf = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample("form-fields.pdf"))))
        {
            pdf.FillFormFields(new Dictionary<string, string> { ["Name"] = "Jordan Lee", ["Membership"] = "Family" });
            saved = pdf.SaveCopy(removeSecurity: true);
        }

        byte[] compact = PdfCompactor.Compact(saved);
        Assert.True(compact.Length < saved.Length, $"{compact.Length} bytes isn't smaller than {saved.Length}");
        AssertSameDocument(saved, compact);
    }

    [Fact]
    public void Every_corpus_file_saved_and_compacted_renders_the_same_and_is_never_larger()
    {
        IReadOnlyList<string> files = TestFiles.CorpusFiles();
        Assert.SkipWhen(files.Count == 0, "Corpus not downloaded; run build/fetch-corpus.sh");

        var failures = new List<string>();
        long before = 0, after = 0;
        foreach (string file in files)
        {
            byte[] saved;
            try
            {
                using PdfiumDocument pdf = PdfiumDocument.Open(File.ReadAllBytes(file));
                saved = pdf.SaveCopy(removeSecurity: true);
            }
            catch (PdfOpenException)
            {
                continue; // Needs a password, or PDFium can't open it: nothing to compact.
            }

            byte[] compact = PdfCompactor.Compact(saved);
            before += saved.Length;
            after += compact.Length;
            try
            {
                Assert.True(compact.Length <= saved.Length, "larger");
                AssertSameDocument(saved, compact);
            }
            catch (Exception ex) when (ex is Xunit.Sdk.XunitException or PdfOpenException)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(after < before, $"the corpus didn't get smaller: {before} → {after} bytes");
    }

    [Fact]
    public void A_working_copy_built_on_a_compact_source_reopens_with_its_edits()
    {
        byte[] clean;
        using (PdfiumDocument pdf = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample("form-fields.pdf"))))
        {
            clean = PdfCompactor.Compact(pdf.SaveCopy(removeSecurity: true));
        }

        // An update on top, as the working copy adds one: PDFium appends a plain section after the compact one.
        byte[] updated;
        using (PdfiumDocument pdf = PdfiumDocument.Open(clean))
        {
            pdf.FillFormFields(new Dictionary<string, string> { ["Name"] = "Jordan Lee" });
            updated = pdf.SaveIncremental();
        }

        Assert.True(updated.AsSpan(0, clean.Length).SequenceEqual(clean), "the update keeps the compact original at the start");
        using PdfiumDocument reopened = PdfiumDocument.Open(updated);
        Assert.Equal("Jordan Lee", reopened.GetFormFields(0).Single(field => field.Name == "Name").Value);
    }

    [Fact]
    public void An_update_keeps_only_what_changed_and_the_original_bytes_before_it()
    {
        byte[] clean;
        using (PdfiumDocument pdf = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample("form-fields.pdf"))))
        {
            clean = PdfCompactor.Compact(pdf.SaveCopy(removeSecurity: true));
        }

        byte[] updated;
        using (PdfiumDocument pdf = PdfiumDocument.Open(clean))
        {
            pdf.FillFormFields(new Dictionary<string, string> { ["Name"] = "Jordan Lee" });
            updated = pdf.SaveIncremental();
        }

        byte[] compact = PdfCompactor.CompactUpdate(updated, clean.Length);
        Assert.True(compact.Length < updated.Length, $"{compact.Length} bytes isn't smaller than {updated.Length}");
        Assert.True(compact.AsSpan(0, clean.Length).SequenceEqual(clean), "the original stays at the start");
        AssertSameDocument(updated, compact);
        using PdfiumDocument reopened = PdfiumDocument.Open(compact);
        Assert.Equal("Jordan Lee", reopened.GetFormFields(0).Single(field => field.Name == "Name").Value);
    }

    [Fact]
    public void Every_corpus_file_updated_and_compacted_renders_the_same()
    {
        IReadOnlyList<string> files = TestFiles.CorpusFiles();
        Assert.SkipWhen(files.Count == 0, "Corpus not downloaded; run build/fetch-corpus.sh");

        var failures = new List<string>();
        long before = 0, after = 0;
        foreach (string file in files)
        {
            byte[] clean, updated;
            try
            {
                using (PdfiumDocument pdf = PdfiumDocument.Open(File.ReadAllBytes(file)))
                {
                    clean = PdfCompactor.Compact(pdf.SaveCopy(removeSecurity: true));
                }

                // A rotation and, where there's more than one page, a deletion: changed objects and freed ones.
                using PdfiumDocument changing = PdfiumDocument.Open(clean);
                changing.SetRotation(0, 1);
                if (changing.PageCount > 1)
                {
                    changing.DeletePage(changing.PageCount - 1);
                }

                updated = changing.SaveIncremental();
            }
            catch (Exception ex) when (ex is PdfOpenException or PdfPageException or InvalidOperationException)
            {
                continue; // Needs a password, or PDFium can't open or change it: nothing to compact.
            }

            byte[] compact = PdfCompactor.CompactUpdate(updated, clean.Length);
            before += updated.Length;
            after += compact.Length;
            try
            {
                Assert.True(compact.Length <= updated.Length, "larger");
                Assert.True(compact.AsSpan(0, clean.Length).SequenceEqual(clean), "the original changed");
                AssertSameDocument(updated, compact);
            }
            catch (Exception ex) when (ex is Xunit.Sdk.XunitException or PdfOpenException)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(after < before, $"the corpus's updates didn't get smaller: {before} → {after} bytes");
    }

    [Fact]
    public void Something_it_does_not_read_is_left_as_it_is()
    {
        byte[] notAPdf = "%PDF-1.4\nnot really a PDF\n"u8.ToArray();
        Assert.Same(notAPdf, PdfCompactor.Compact(notAPdf));
        Assert.Same(notAPdf, PdfCompactor.CompactUpdate(notAPdf, 9));
    }

    [Fact]
    public void A_file_nested_too_deeply_is_left_as_it_is_rather_than_ending_the_app()
    {
        // Each level of nesting is a call; a few hundred kilobytes of brackets would otherwise overflow the stack,
        // which ends the app with no chance to catch it.
        byte[] deep = ClassicPdf(new string('[', 200_000) + new string(']', 200_000));
        Assert.Same(deep, PdfCompactor.Compact(deep));
        Assert.Null(PdfCompactor.CompactKeepingOnlyUsed(deep));
    }

    [Fact]
    public void An_update_over_a_classic_table_stays_classic_and_keeps_only_what_changed()
    {
        // A file this small doesn't get smaller packed, so its clean source keeps PDFium's classic table and its PDF
        // 1.4 header. A cross-reference stream (PDF 1.5) in its update would be a feature the header says isn't used.
        byte[] clean;
        using (PdfiumDocument pdf = PdfiumDocument.Open(TestFiles.ClassicPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 23 >>\nstream\n0 0 1 rg 20 20 80 80 re f\nendstream")))
        {
            byte[] saved = pdf.SaveCopy(removeSecurity: true);
            clean = PdfCompactor.Compact(saved);
            Assert.Same(saved, clean);
        }

        byte[] updated;
        using (PdfiumDocument pdf = PdfiumDocument.Open(clean))
        {
            pdf.SetRotation(0, 1);
            updated = pdf.SaveIncremental();
        }

        byte[] compact = PdfCompactor.CompactUpdate(updated, clean.Length);
        Assert.True(compact.Length < updated.Length, $"{compact.Length} bytes isn't smaller than {updated.Length}");
        Assert.True(compact.AsSpan(0, clean.Length).SequenceEqual(clean), "the original changed");
        string update = System.Text.Encoding.Latin1.GetString(compact, clean.Length, compact.Length - clean.Length);
        Assert.Contains("\nxref\n", update, StringComparison.Ordinal);
        Assert.DoesNotContain("/XRef", update, StringComparison.Ordinal);
        Assert.DoesNotContain("/ObjStm", update, StringComparison.Ordinal);
        Assert.Contains("/Rotate", update, StringComparison.Ordinal);
        AssertSameDocument(updated, compact);
        using PdfiumDocument reopened = PdfiumDocument.Open(compact);
        Assert.Equal(1, reopened.PageCount);
    }

    /// <summary>A minimal PDF with a plain cross-reference table, as PDFium writes, holding one extra object.</summary>
    private static byte[] ClassicPdf(string extraObject) =>
        TestFiles.ClassicPdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>", extraObject);

    [Fact]
    public void A_file_with_no_update_after_the_original_is_left_as_it_is()
    {
        byte[] saved;
        using (PdfiumDocument pdf = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample("form-fields.pdf"))))
        {
            saved = pdf.SaveCopy(removeSecurity: true);
        }

        Assert.Same(saved, PdfCompactor.CompactUpdate(saved, saved.Length));
    }

    /// <summary>Same pages, each rendering to the same pixels, and the same form fields with the same values.</summary>
    private static void AssertSameDocument(byte[] expected, byte[] actual)
    {
        using PdfiumDocument a = PdfiumDocument.Open(expected);
        using PdfiumDocument b = PdfiumDocument.Open(actual);
        Assert.Equal(a.PageCount, b.PageCount);
        for (int page = 0; page < Math.Min(a.PageCount, 3); page++)
        {
            Assert.Equal(Render(a, page), Render(b, page));
            Assert.Equal(Fields(a, page), Fields(b, page));
        }
    }

    private static string Render(PdfiumDocument pdf, int page)
    {
        try
        {
            PdfPageInfo info = pdf.GetPageInfo(page);
            int width = Math.Max(1, (int)(info.Width / 2)), height = Math.Max(1, (int)(info.Height / 2));
            byte[] pixels = new byte[width * height * 4];
            pdf.RenderPage(page, pixels, width, height, width * 4);
            return Convert.ToHexString(SHA256.HashData(pixels));
        }
        catch (PdfPageException)
        {
            return "damaged"; // A damaged page should stay exactly as damaged.
        }
    }

    private static (string Name, string Value, bool IsChecked)[] Fields(PdfiumDocument pdf, int page)
    {
        try
        {
            return [.. pdf.GetFormFields(page).Select(field => (field.Name, field.Value, field.IsChecked))];
        }
        catch (PdfPageException)
        {
            return [];
        }
    }
}
