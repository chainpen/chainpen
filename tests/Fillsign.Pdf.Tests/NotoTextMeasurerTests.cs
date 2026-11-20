using Fillsign.Pdf.Fonts;

namespace Fillsign.Pdf.Tests;

public sealed class NotoTextMeasurerTests
{
    [Fact]
    public void Widths_scale_with_the_font_size_and_ignore_kerning()
    {
        NotoTextMeasurer measurer = NotoTextMeasurer.Instance;
        double at12 = measurer.MeasureLineWidth("AVAILABLE Wave", 12);
        Assert.Equal(at12 * 2, measurer.MeasureLineWidth("AVAILABLE Wave", 24), 6);

        // Unkerned: a pair is exactly the sum of its letters, as the export lays it out.
        Assert.Equal(measurer.MeasureLineWidth("A", 30) + measurer.MeasureLineWidth("V", 30), measurer.MeasureLineWidth("AV", 30), 6);
        Assert.Equal(0, measurer.MeasureLineWidth(string.Empty, 12));
    }
}
