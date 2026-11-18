using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Tests.Geometry;

public sealed class ItemGeometryTests
{
    private static readonly PageId s_page = PageId.New();
    private static readonly ApproximateTextMeasurer s_measurer = ApproximateTextMeasurer.Instance;

    [Fact]
    public void A_click_makes_a_default_shape()
    {
        ShapeItem shape = ItemGeometry.MakeShape(ItemId.New(), s_page, ShapeKind.Rectangle, new(100, 100), new(101, 101), InkColor.Red, 2);
        Assert.Equal(new PageRect(100, 100, 80, 40), new PageRect(shape.X, shape.Y, shape.Width, shape.Height));

        ShapeItem line = ItemGeometry.MakeShape(ItemId.New(), s_page, ShapeKind.Line, new(100, 100), new(100, 100), InkColor.Red, 2);
        Assert.Equal(0, line.Height);

        ShapeItem highlight = ItemGeometry.MakeShape(ItemId.New(), s_page, ShapeKind.Highlight, new(100, 100), new(100, 100), InkColor.Red, 2);
        Assert.Equal(93, highlight.Y);
        Assert.Equal(14, highlight.Height);
    }

    [Fact]
    public void Dragging_up_and_left_flips_the_line()
    {
        ShapeItem arrow = ItemGeometry.MakeShape(ItemId.New(), s_page, ShapeKind.Arrow, new(200, 200), new(100, 150), InkColor.Red, 2);
        Assert.Equal((100.0, 150.0, 100.0, 50.0), (arrow.X, arrow.Y, arrow.Width, arrow.Height));
        Assert.True(arrow.FlipX && arrow.FlipY);
        var (start, end) = ItemPaths.LineEnds(arrow.Width, arrow.Height, arrow.FlipX, arrow.FlipY);
        Assert.Equal(new PagePoint(100, 50), start); // the drag started bottom-right
        Assert.Equal(new PagePoint(0, 0), end);
    }

    [Fact]
    public void Ink_points_are_normalised_to_their_box()
    {
        InkItem ink = ItemGeometry.MakeInk(ItemId.New(), s_page, [[new(10, 20), new(110, 70)], [new(60, 45)]], InkColor.Blue, 2);
        Assert.Equal((10.0, 20.0, 100.0, 50.0), (ink.X, ink.Y, ink.Width, ink.Height));
        Assert.Equal(new PagePoint(1, 1), ink.Strokes[0][1]);
        Assert.Equal(new PagePoint(0.5, 0.5), ink.Strokes[1][0]);
        Assert.Equal(new PagePoint(110, 70), ItemGeometry.InkToPageStrokes(ink)[0][1]);
    }

    [Fact]
    public void Appending_strokes_keeps_the_drawing_and_its_id()
    {
        InkItem ink = ItemGeometry.MakeInk(ItemId.New(), s_page, [[new(10, 10), new(20, 20)]], InkColor.Blue, 2);
        InkItem more = ItemGeometry.AppendStrokes(ink, [[new(0, 30), new(5, 35)]]);
        Assert.Equal(ink.Id, more.Id);
        Assert.Equal(2, more.Strokes.Length);
        Assert.Equal((0.0, 10.0, 20.0, 25.0), (more.X, more.Y, more.Width, more.Height));
    }

    [Fact]
    public void A_single_point_stroke_makes_no_division_by_zero()
    {
        InkItem dot = ItemGeometry.MakeInk(ItemId.New(), s_page, [[new(5, 5)]], InkColor.Blue, 2);
        Assert.Equal(new PagePoint(0, 0), dot.Strokes[0][0]);
        Assert.IsType<LineTo>(ItemPaths.InkPath(dot)[1]);
        Assert.Throws<ArgumentException>(() => ItemGeometry.MakeInk(ItemId.New(), s_page, [], InkColor.Blue, 2));
    }

    [Fact]
    public void Four_quarter_turns_bring_every_item_back()
    {
        foreach (Item item in (Item[])[TestData.Text(s_page), TestData.Tick(s_page), TestData.Signature(s_page), TestData.Rectangle(s_page), TestData.Ink(s_page)])
        {
            Item turned = item;
            PageSize page = TestData.A4;
            for (int i = 0; i < 4; i++)
            {
                turned = ItemGeometry.RotateWithPage(turned, clockwise: true, page, s_measurer);
                page = new PageSize(page.Height, page.Width);
            }

            Assert.Equal(item.X, turned.X, 6);
            Assert.Equal(item.Y, turned.Y, 6);
        }
    }

    [Fact]
    public void Turning_clockwise_then_back_is_a_no_op_for_shapes()
    {
        ShapeItem arrow = TestData.Rectangle(s_page) with { Kind = ShapeKind.Arrow };
        var turned = (ShapeItem)ItemGeometry.RotateWithPage(arrow, true, TestData.A4, s_measurer);
        Assert.Equal((arrow.Height, arrow.Width), (turned.Width, turned.Height)); // shapes turn with the page
        var back = (ShapeItem)ItemGeometry.RotateWithPage(turned, false, new PageSize(842, 595), s_measurer);
        Assert.Equal(arrow.X, back.X, 6);
        Assert.Equal(arrow.Y, back.Y, 6);
        Assert.Equal(arrow.FlipX, back.FlipX);
        Assert.Equal(arrow.FlipY, back.FlipY);
    }

    [Fact]
    public void Upright_items_stay_upright_and_follow_their_centre()
    {
        MarkItem tick = TestData.Tick(s_page, 0, 0);
        var turned = (MarkItem)ItemGeometry.RotateWithPage(tick, clockwise: true, TestData.A4, s_measurer);

        // Top-left corner of the page goes to the top-right after a clockwise turn.
        Assert.Equal(842 - tick.Size, turned.X, 6);
        Assert.Equal(0, turned.Y, 6);
        Assert.Equal(tick.Size, turned.Size);
    }

    [Fact]
    public void Group_moves_stop_at_the_page_edge()
    {
        PageRect[] boxes = [new(10, 10, 20, 20), new(500, 400, 50, 50)];
        Assert.Equal((-10.0, 5.0), ItemGeometry.ClampGroupMove(boxes, TestData.A4, -100, 5));
        Assert.Equal((45.0, 392.0), ItemGeometry.ClampGroupMove(boxes, TestData.A4, 100, 1000));
    }

    [Fact]
    public void Text_size_counts_every_line()
    {
        TextItem text = TestData.Text(s_page, text: "ab\nlonger line");
        PageSize size = ItemGeometry.TextSize(text, s_measurer);
        Assert.Equal(2 * 12 * TextItem.LineHeight, size.Height, 6);
        Assert.Equal(s_measurer.MeasureLineWidth("longer line", 12), size.Width, 6);
    }

    [Fact]
    public void Paths_have_the_expected_shape()
    {
        Assert.Equal(6, ItemPaths.ShapePath(TestData.Rectangle(s_page) with { Kind = ShapeKind.Ellipse }).Count);
        Assert.Equal(5, ItemPaths.ShapePath(TestData.Rectangle(s_page) with { Kind = ShapeKind.Arrow }).Count);
        Assert.Equal(5, ItemPaths.ShapePath(TestData.Rectangle(s_page) with { Kind = ShapeKind.Whiteout }).Count);
        MarkItem tick = TestData.Tick(s_page) with { Size = 48 };
        Assert.Equal(new MoveTo(new PagePoint(8, 25)), ItemPaths.MarkPath(tick)[0]);
        Assert.Equal(6, ItemPaths.MarkStrokeWidth(tick));

        // Three samples: a curve through the middle sample, ending on the last one.
        var ink = ItemPaths.InkPath(TestData.Ink(s_page));
        Assert.Collection(
            ink,
            c => Assert.IsType<MoveTo>(c),
            c => Assert.IsType<QuadTo>(c),
            c => Assert.Equal(new LineTo(new PagePoint(100, 12.5)), c),
            c => Assert.IsType<MoveTo>(c),
            c => Assert.IsType<LineTo>(c));
    }
}
