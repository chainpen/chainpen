using Fillsign.Core.Editing;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Tests.Editing;

public sealed class ResizeTests
{
    private static readonly ITextMeasurer s_measurer = ApproximateTextMeasurer.Instance;
    private static readonly PageId s_page = PageId.New();

    [Theory]
    [InlineData(Corner.BottomRight, 230, 190, 100, 100, 130, 90)]
    [InlineData(Corner.TopLeft, 70, 80, 70, 80, 130, 80)]
    [InlineData(Corner.TopRight, 210, 90, 100, 90, 110, 70)]
    [InlineData(Corner.BottomLeft, 90, 150, 90, 100, 110, 50)]
    public void Dragging_a_corner_keeps_the_opposite_corner_fixed(Corner corner, double toX, double toY, double x, double y, double width, double height)
    {
        ShapeItem shape = TestData.Rectangle(s_page, x: 100, y: 100, w: 100, h: 60);
        var resized = (ShapeItem)ItemEdits.ResizeFromCorner(shape, corner, new PagePoint(toX, toY), s_measurer);
        Assert.Equal((x, y, width, height), (resized.X, resized.Y, resized.Width, resized.Height));
    }

    [Fact]
    public void Signatures_keep_their_proportions_from_the_top_left_corner()
    {
        SignatureItem signature = TestData.Signature(s_page); // 120 × 40 at (200, 300)
        var resized = (SignatureItem)ItemEdits.ResizeFromCorner(signature, Corner.TopLeft, new PagePoint(140, 290), s_measurer);
        Assert.Equal(180, resized.Width, 6);
        Assert.Equal(60, resized.Height, 6);
        Assert.Equal((320 - 180, 340 - 60), (resized.X, resized.Y));
    }

    [Fact]
    public void A_corner_cant_be_dragged_past_the_opposite_one()
    {
        ShapeItem shape = TestData.Rectangle(s_page, x: 100, y: 100, w: 100, h: 60);
        var resized = (ShapeItem)ItemEdits.ResizeFromCorner(shape, Corner.BottomRight, new PagePoint(50, 50), s_measurer);
        Assert.Equal((100, 100), (resized.X, resized.Y));
        Assert.True(resized.Width > 0 && resized.Height > 0);
    }

    [Fact]
    public void Signature_size_steps_keep_proportions_within_limits()
    {
        SignatureItem signature = TestData.Signature(s_page);
        var bigger = (SignatureItem)ItemEdits.BumpSize(signature, +1);
        Assert.Equal(144, bigger.Width, 6);
        Assert.Equal(signature.Height * 1.2, bigger.Height, 6);

        SignatureItem item = signature;
        for (int i = 0; i < 40; i++)
        {
            item = (SignatureItem)ItemEdits.BumpSize(item, -1);
        }

        Assert.Equal(ItemEdits.SignatureMinWidth, item.Width, 6);
    }
}
