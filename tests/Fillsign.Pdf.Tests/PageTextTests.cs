using Fillsign.Core.Finding;
using Fillsign.Core.Model;
using Fillsign.Pdf.Export;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Tests;

/// <summary>FS-974: a page's own text found where it's shown, the right way up or turned.</summary>
public sealed class PageTextTests
{
    [Fact]
    public void Words_on_a_real_page_are_found_inside_the_page_where_they_are_shown()
    {
        using PdfiumDocument pdf = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample("sample-form.pdf")));
        PdfPageInfo info = pdf.GetPageInfo(0);
        Assert.Equal(0, info.Rotation);
        var page = new SourcePage(new PdfBox(info.CropBox.Left, info.CropBox.Bottom, info.CropBox.Right, info.CropBox.Top), Rotation.None);
        IReadOnlyList<PdfCharacter> text = pdf.GetCharacters(0);
        Assert.True(PageText.HasText(text));

        TextMatch upright = Assert.Single(PageText.Find(text, page, Rotation.None, "over 18"));
        PageSize size = page.DisplaySize;
        Assert.InRange(upright.Bounds.X, 0, size.Width);
        Assert.InRange(upright.Bounds.Bottom, 0, size.Height);
        Assert.Single(upright.Boxes);

        // Turned a quarter to the right, the same words sit in the turned page's coordinates: a tall, thin box.
        TextMatch turned = Assert.Single(PageText.Find(text, page, Rotation.None.Clockwise(), "over 18"));
        Assert.Single(turned.Boxes);
        Assert.True(turned.Bounds.Height > turned.Bounds.Width, "the words run down the turned page");
        Assert.InRange(turned.Bounds.Right, 0, size.Height);
    }
}
