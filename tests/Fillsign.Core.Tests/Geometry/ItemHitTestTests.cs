using Fillsign.Core.Editing;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Tests.Geometry;

public sealed class ItemHitTestTests
{
    private static readonly ITextMeasurer s_measurer = ApproximateTextMeasurer.Instance;
    private static readonly PageId s_page = PageId.New();

    [Fact]
    public void Outlined_rectangles_are_grabbed_by_their_outline_only()
    {
        ShapeItem rectangle = TestData.Rectangle(s_page, x: 100, y: 100, w: 100, h: 60);
        Assert.True(ItemHitTest.Hits(rectangle, new PagePoint(100, 130), 2, s_measurer));
        Assert.True(ItemHitTest.Hits(rectangle, new PagePoint(150, 161.5), 2, s_measurer));
        Assert.False(ItemHitTest.Hits(rectangle, new PagePoint(150, 130), 2, s_measurer));
        Assert.False(ItemHitTest.Hits(rectangle, new PagePoint(150, 166), 2, s_measurer));
    }

    [Fact]
    public void Ellipses_are_grabbed_by_their_outline_only()
    {
        ShapeItem ellipse = TestData.Rectangle(s_page, x: 0, y: 0, w: 200, h: 100) with { Kind = ShapeKind.Ellipse };
        Assert.True(ItemHitTest.Hits(ellipse, new PagePoint(100, 1), 2, s_measurer));
        Assert.True(ItemHitTest.Hits(ellipse, new PagePoint(199, 50), 2, s_measurer));
        Assert.False(ItemHitTest.Hits(ellipse, new PagePoint(100, 50), 2, s_measurer));

        // The box corner is outside the ellipse.
        Assert.False(ItemHitTest.Hits(ellipse, new PagePoint(5, 5), 2, s_measurer));
    }

    [Fact]
    public void Highlights_and_whiteout_are_grabbed_anywhere_inside()
    {
        ShapeItem highlight = TestData.Rectangle(s_page, x: 0, y: 0, w: 100, h: 14) with { Kind = ShapeKind.Highlight };
        Assert.True(ItemHitTest.Hits(highlight, new PagePoint(50, 7), 0, s_measurer));
        Assert.False(ItemHitTest.Hits(highlight, new PagePoint(50, 20), 0, s_measurer));
    }

    [Fact]
    public void Lines_and_drawings_are_grabbed_near_their_strokes()
    {
        ShapeItem line = TestData.Rectangle(s_page, x: 0, y: 0, w: 100, h: 100) with { Kind = ShapeKind.Line };
        Assert.True(ItemHitTest.Hits(line, new PagePoint(50, 52), 2, s_measurer));
        Assert.False(ItemHitTest.Hits(line, new PagePoint(80, 20), 2, s_measurer));

        InkItem ink = TestData.Ink(s_page);
        PagePoint onStroke = new(ink.X + (0.25 * ink.Width), ink.Y + (0.5 * ink.Height));
        Assert.True(ItemHitTest.Hits(ink, onStroke, 1, s_measurer));
        Assert.False(ItemHitTest.Hits(ink, new PagePoint(ink.X + ink.Width, ink.Y + ink.Height), 1, s_measurer));
    }

    [Fact]
    public void A_tick_inside_a_rectangle_is_found_and_empty_space_inside_grabs_the_rectangle()
    {
        ShapeItem rectangle = TestData.Rectangle(s_page, x: 100, y: 100, w: 100, h: 60);
        MarkItem tick = TestData.Tick(s_page, x: 140, y: 120);
        var document = DocumentState.ForSourcePages(1) with { Items = [tick, rectangle] };
        Assert.Equal(tick, ItemHitTest.TopmostAt(document, s_page, new PagePoint(147, 127), 2, s_measurer));
        Assert.Equal(rectangle, ItemHitTest.TopmostAt(document, s_page, new PagePoint(120, 150), 2, s_measurer));
        Assert.Equal(rectangle, ItemHitTest.TopmostAt(document, s_page, new PagePoint(100, 150), 2, s_measurer));
        Assert.Null(ItemHitTest.TopmostAt(document, s_page, new PagePoint(220, 150), 2, s_measurer));
    }

    [Fact]
    public void Empty_space_inside_an_ellipse_grabs_it_but_not_inside_a_line_or_drawing_box()
    {
        ShapeItem ellipse = TestData.Rectangle(s_page, x: 0, y: 0, w: 200, h: 100) with { Kind = ShapeKind.Ellipse };
        ShapeItem line = TestData.Rectangle(s_page, x: 300, y: 0, w: 100, h: 100) with { Kind = ShapeKind.Line };
        var document = DocumentState.ForSourcePages(1) with { Items = [ellipse, line] };
        Assert.Equal(ellipse, ItemHitTest.TopmostAt(document, s_page, new PagePoint(100, 50), 2, s_measurer));
        Assert.Null(ItemHitTest.TopmostAt(document, s_page, new PagePoint(380, 20), 2, s_measurer));
    }

    [Fact]
    public void The_topmost_of_overlapping_items_is_hit()
    {
        MarkItem below = TestData.Tick(s_page, x: 10, y: 10);
        MarkItem above = TestData.Tick(s_page, x: 12, y: 12);
        var document = DocumentState.ForSourcePages(1) with { Items = [below, above] };
        Assert.Equal(above, ItemHitTest.TopmostAt(document, s_page, new PagePoint(18, 18), 0, s_measurer));
    }
}
