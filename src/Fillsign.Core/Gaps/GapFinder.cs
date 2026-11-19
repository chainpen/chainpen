using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Gaps;

public enum GapKind
{
    /// <summary>A question's boxes, none of them ticked.</summary>
    Unticked,

    /// <summary>A blank with nothing written in it.</summary>
    Empty,
}

/// <summary>A possible gap on one page, in that page's display coordinates.</summary>
/// <param name="PageNumber">1-based position in the document as it is now.</param>
public sealed record Gap(PageId Page, int PageNumber, GapKind Kind, PageRect Area, string Label, BlankKind BlankKind)
{
    /// <summary>A form field (FS-950): worded as a field ("'Email' is empty"), not as a printed line.</summary>
    public bool IsField { get; init; }

    /// <summary>For a form's radio group: the question it answers.</summary>
    public string Question { get; init; } = "";
}

/// <summary>Boxes on a page and how many are ticked, for "3 of 4 boxes ticked on page 1".</summary>
public sealed record PageBoxes(PageId Page, int PageNumber, int Ticked, int Total);

/// <summary>The possible gaps, and what was looked at.</summary>
/// <param name="PagesChecked">Pages with something added: only those are checked, as untouched pages are often meant for someone else.</param>
public sealed record GapReport(IReadOnlyList<Gap> Gaps, IReadOnlyList<PageBoxes> Boxes, int PagesChecked)
{
    public static GapReport None { get; } = new([], [], 0);
}

/// <summary>
/// Possible gaps before export (FR-8.3): boxes and blanks on the pages the person worked on that nothing was placed
/// in. Hints, never a verdict: the person decides whether each matters.
/// </summary>
public static class GapFinder
{
    // How near an item has to be to count as filling a box or a blank: ticks are often placed a little off.
    private const double BoxReach = 3;
    private const double BlankReach = 2;

    /// <param name="spotsOn">Each page's boxes and blanks, already in that page's display coordinates.</param>
    /// <param name="alsoWorkedOn">
    /// Pages worked on without adding items, such as filling the PDF's own form fields (FS-950); checked as well.
    /// </param>
    public static GapReport Find(
        DocumentState document,
        Func<PageEntry, IReadOnlyList<FormSpot>> spotsOn,
        ITextMeasurer measurer,
        IReadOnlySet<PageId>? alsoWorkedOn = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(spotsOn);
        ArgumentNullException.ThrowIfNull(measurer);

        var gaps = new List<Gap>();
        var boxes = new List<PageBoxes>();
        int checkedPages = 0;
        for (int index = 0; index < document.Pages.Length; index++)
        {
            PageEntry page = document.Pages[index];
            PageRect[] placed = [.. document.ItemsOn(page.Id).Select(item => ItemGeometry.Bounds(item, measurer))];
            if (placed.Length == 0 && alsoWorkedOn?.Contains(page.Id) != true)
            {
                continue;
            }

            checkedPages++;
            IReadOnlyList<FormSpot> spots = spotsOn(page);
            bool Covered(PageRect area, double reach) => placed.Any(box => box.Intersects(Inflate(area, reach)));

            CheckBoxSpot[] pageBoxes = [.. spots.OfType<CheckBoxSpot>()];
            bool[] ticked = [.. pageBoxes.Select(box => box.IsTicked || Covered(box.Area, BoxReach))];

            // The page's box count is for boxes read from its text; form fields are counted as questions instead.
            int[] printed = [.. Enumerable.Range(0, pageBoxes.Length).Where(i => !pageBoxes[i].IsField)];
            if (printed.Length > 0)
            {
                boxes.Add(new PageBoxes(page.Id, index + 1, printed.Count(i => ticked[i]), printed.Length));
            }

            foreach (IGrouping<int, int> question in Enumerable.Range(0, pageBoxes.Length).GroupBy(i => pageBoxes[i].Group))
            {
                if (question.Any(i => ticked[i]))
                {
                    continue;
                }

                CheckBoxSpot[] options = [.. question.Select(i => pageBoxes[i])];
                string label = string.Join(" / ", options.Select(option => option.Label).Where(text => text.Length > 0).Take(3));
                gaps.Add(new Gap(page.Id, index + 1, GapKind.Unticked, Union(options.Select(option => option.Area)), label, BlankKind.Text)
                {
                    IsField = options.All(option => option.IsField),
                    Question = options.Select(option => option.Question).FirstOrDefault(question => question.Length > 0) ?? "",
                });
            }

            foreach (BlankSpot blank in spots.OfType<BlankSpot>())
            {
                if (!Covered(blank.Area, BlankReach))
                {
                    gaps.Add(new Gap(page.Id, index + 1, GapKind.Empty, blank.Area, blank.Label, blank.Kind) { IsField = blank.IsField });
                }
            }
        }

        Gap[] ordered = [.. gaps.OrderBy(gap => gap.PageNumber).ThenBy(gap => gap.Area.Y).ThenBy(gap => gap.Area.X)];
        return new GapReport(ordered, boxes, checkedPages);
    }

    /// <summary>
    /// Moves spots found on a source page (display coordinates with its own /Rotate) onto the page as the document
    /// shows it now, turned by <paramref name="extra"/>.
    /// </summary>
    public static IReadOnlyList<FormSpot> Turn(IReadOnlyList<FormSpot> spots, SourcePage source, Rotation extra) =>
        Turn(spots, source, Rotation.None, extra);

    /// <summary>Moves spots from the page turned by <paramref name="from"/> to the page turned by <paramref name="to"/>.</summary>
    public static IReadOnlyList<FormSpot> Turn(IReadOnlyList<FormSpot> spots, SourcePage source, Rotation from, Rotation to)
    {
        ArgumentNullException.ThrowIfNull(spots);
        if (from == to)
        {
            return spots;
        }

        var before = new PageTransform(source, from);
        var after = new PageTransform(source, to);
        PageRect Move(PageRect area)
        {
            (double x1, double y1) = before.ToPdf(new PagePoint(area.X, area.Y));
            (double x2, double y2) = before.ToPdf(new PagePoint(area.Right, area.Bottom));
            return PageRect.FromCorners(after.ToDisplay(x1, y1), after.ToDisplay(x2, y2));
        }

        return [.. spots.Select(spot => spot with { Area = Move(spot.Area) })];
    }

    private static PageRect Inflate(PageRect area, double by) => new(area.X - by, area.Y - by, area.Width + (2 * by), area.Height + (2 * by));

    private static PageRect Union(IEnumerable<PageRect> areas)
    {
        PageRect[] all = [.. areas];
        double left = all.Min(a => a.X), top = all.Min(a => a.Y);
        return new PageRect(left, top, all.Max(a => a.Right) - left, all.Max(a => a.Bottom) - top);
    }
}
