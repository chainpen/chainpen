using Fillsign.Core.Gaps;
using Fillsign.Core.Model;
using Fillsign.Pdf.Export;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Tests;

/// <summary>FS-609: boxes and blanks read from a real PDF, drawn boxes and sideways pages included.</summary>
public sealed class FormSpotReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_sample_forms_boxes_and_blanks_are_found_on_both_pages(int pageIndex)
    {
        using PdfiumDocument pdf = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample("sample-form.pdf")));
        IReadOnlyList<FormSpot> spots = FormSpotReader.Read(pdf, pageIndex, pdf.GetPageInfo(pageIndex).ToSourcePage());

        // The boxes are drawn squares, not characters; page 2's text runs down the page as it's displayed.
        Assert.Equal(["I agree to the terms", "Send me updates", "I am over 18"], spots.OfType<CheckBoxSpot>().Select(box => box.Label));
        Assert.Equal(3, spots.OfType<CheckBoxSpot>().Select(box => box.Group).Distinct().Count());
        Assert.Equal([("Name", BlankKind.Text), ("Signature", BlankKind.Signature)], spots.OfType<BlankSpot>().Select(blank => (blank.Label, blank.Kind)));
    }
}
