using Fillsign.Core.Gaps;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Tests.Gaps;

/// <summary>FS-609: which boxes and blanks are still gaps, given what's been placed.</summary>
public sealed class GapFinderTests
{
    private static readonly ITextMeasurer s_measurer = ApproximateTextMeasurer.Instance;

    [Fact]
    public void Only_pages_with_something_added_are_checked()
    {
        var document = DocumentState.ForSourcePages(2);
        FormSpot[] spots = [Box(60, 100, group: 0, "Yes")];
        Assert.Empty(GapFinder.Find(document, _ => spots, s_measurer).Gaps);

        PageId second = document.Pages[1].Id;
        GapReport report = GapFinder.Find(document.WithItems([TestData.Tick(second, x: 300, y: 600)]), _ => spots, s_measurer);
        Gap gap = Assert.Single(report.Gaps);
        Assert.Equal((second, 2, GapKind.Unticked), (gap.Page, gap.PageNumber, gap.Kind));
        Assert.Equal(1, report.PagesChecked);
    }

    [Fact]
    public void A_tick_on_any_box_of_a_question_answers_it_and_boxes_are_counted_per_page()
    {
        var document = DocumentState.ForSourcePages(1);
        PageId page = document.Pages[0].Id;
        FormSpot[] spots = [Box(60, 100, group: 0, "Yes"), Box(120, 100, group: 0, "No"), Box(60, 140, group: 1, "I would like a copy")];

        // A tick placed a little off the "No" box still counts.
        MarkItem tick = TestData.Tick(page, x: 121, y: 98);
        GapReport report = GapFinder.Find(document.WithItems([tick]), _ => spots, s_measurer);
        Gap gap = Assert.Single(report.Gaps);
        Assert.Equal("I would like a copy", gap.Label);
        Assert.Equal(new PageBoxes(page, 1, 1, 3), Assert.Single(report.Boxes));
    }

    [Fact]
    public void A_blank_with_text_or_a_signature_in_it_is_filled()
    {
        var document = DocumentState.ForSourcePages(1);
        PageId page = document.Pages[0].Id;
        FormSpot[] spots =
        [
            new BlankSpot(new PageRect(150, 100, 300, 16), "Name", BlankKind.Text),
            new BlankSpot(new PageRect(150, 140, 300, 16), "Date", BlankKind.Date),
            new BlankSpot(new PageRect(150, 180, 300, 16), "Signature", BlankKind.Signature),
        ];
        var name = new TextItem(ItemId.New(), page, 155, 101, "Jordan Lee", 12, InkColor.Black);
        GapReport report = GapFinder.Find(document.WithItems([name]), _ => spots, s_measurer);
        Assert.Equal(["Date", "Signature"], report.Gaps.Select(gap => gap.Label));
        Assert.Equal([BlankKind.Date, BlankKind.Signature], report.Gaps.Select(gap => gap.BlankKind));
    }

    [Fact]
    public void Spots_turn_with_the_page()
    {
        var source = new SourcePage(new PdfBox(0, 0, 600, 800), Rotation.None);
        FormSpot[] spots = [Box(10, 20, group: 0, "Yes")];
        FormSpot turned = Assert.Single(GapFinder.Turn(spots, source, Rotation.FromQuarterTurns(1)));

        // A quarter turn clockwise: the top-left corner of an 800-tall page goes to the top-right of an 800-wide one.
        Assert.Equal(new PageRect(800 - 20 - 10, 10, 10, 10), turned.Area);
        FormSpot back = Assert.Single(GapFinder.Turn([turned], source, Rotation.FromQuarterTurns(1), Rotation.None));
        Assert.Equal(spots[0].Area, back.Area);
    }

    private static CheckBoxSpot Box(double x, double y, int group, string label) => new(new PageRect(x, y, 10, 10), label, group, IsTicked: false);
}
