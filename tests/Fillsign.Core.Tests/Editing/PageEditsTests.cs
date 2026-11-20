using Fillsign.Core.Editing;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Tests.Editing;

public sealed class PageEditsTests
{
    private static readonly ApproximateTextMeasurer s_measurer = ApproximateTextMeasurer.Instance;

    private static PageSize SizeOf(PageEntry entry) => TestData.A4.Turned(entry.Rotation);

    [Fact]
    public void Page_edits_keep_the_values_typed_into_form_fields()
    {
        DocumentState start = DocumentState.ForSourcePages(3).WithField("Name", "Jordan Lee").WithField("Agree", "Yes");
        PageId first = start.Pages[0].Id;

        Assert.Equal(start.Fields, PageEdits.Rotate(start, TestData.Set(first), clockwise: true, SizeOf, s_measurer).Fields);
        Assert.True(PageEdits.TryDelete(start, TestData.Set(first), out DocumentState deleted));
        Assert.Equal(start.Fields, deleted.Fields);
        Assert.Equal(start.Fields, PageEdits.Duplicate(start, TestData.Set(first), out _).Fields);
        Assert.Equal(start.Fields, PageEdits.Move(start, TestData.Set(first), 3).Fields);
        Assert.Equal(start.Fields, PageEdits.InsertSourcePages(start, [0], 1).Fields);
    }

    /// <summary>REQUIREMENTS §7 scenario 5: tick A, rotate A, move D to the top, delete C → D, A (rotated), B.</summary>
    [Fact]
    public void Scenario_5_reorder_rotate_delete_and_undo()
    {
        var history = new UndoHistory();
        DocumentState start = DocumentState.ForSourcePages(4);
        PageId a = start.Pages[0].Id, c = start.Pages[2].Id, d = start.Pages[3].Id;

        DocumentState state = Apply(history, start, s => ItemEdits.Add(s, TestData.Tick(a, 100, 200)));
        state = Apply(history, state, s => PageEdits.Rotate(s, TestData.Set(a), clockwise: true, SizeOf, s_measurer));
        state = Apply(history, state, s => PageEdits.Move(s, TestData.Set(d), 0));
        Assert.True(PageEdits.TryDelete(state, TestData.Set(c), out DocumentState deleted));
        state = Apply(history, state, _ => deleted);

        Assert.Equal([3, 0, 1], state.Pages.Select(p => p.SourceIndex));
        Assert.Equal(90, state.Pages[1].Rotation.Degrees);
        var tick = Assert.Single(state.Items);
        Assert.Equal(a, tick.Page); // the tick stayed with page A

        // Its centre turned with the page: (107, 207) → (842 − 207, 107).
        Assert.Equal(842 - 207 - 7, tick.X, 6);
        Assert.Equal(107 - 7, tick.Y, 6);

        while (history.Undo(state) is { } previous)
        {
            state = previous;
        }

        Assert.Equal(start, state);
    }

    [Fact]
    public void Deleting_every_page_is_refused()
    {
        DocumentState state = DocumentState.ForSourcePages(2);
        Assert.False(PageEdits.TryDelete(state, state.Pages.Select(p => p.Id).ToHashSet(), out DocumentState result));
        Assert.Same(state, result);
    }

    [Fact]
    public void Deleting_a_page_removes_its_items()
    {
        DocumentState state = TestData.FourPagesWithItems();
        Assert.True(PageEdits.TryDelete(state, TestData.Set(state.Pages[0].Id), out DocumentState result));
        Assert.Equal(3, result.Items.Count);
        Assert.DoesNotContain(result.Items, item => item.Page == state.Pages[0].Id);
    }

    [Theory]
    [InlineData(0, new[] { 1, 3, 0, 2 })]
    [InlineData(4, new[] { 0, 2, 1, 3 })]
    [InlineData(2, new[] { 0, 1, 3, 2 })]
    [InlineData(1, new[] { 0, 1, 3, 2 })]
    public void Moving_a_group_keeps_its_order(int gap, int[] expected)
    {
        DocumentState state = DocumentState.ForSourcePages(4);
        DocumentState moved = PageEdits.Move(state, TestData.Set(state.Pages[1].Id, state.Pages[3].Id), gap);
        Assert.Equal(expected, moved.Pages.Select(p => p.SourceIndex));
    }

    [Fact]
    public void Move_rejects_a_gap_outside_the_document()
    {
        DocumentState state = DocumentState.ForSourcePages(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageEdits.Move(state, TestData.Set(state.Pages[0].Id), 3));
    }

    [Fact]
    public void Duplicates_go_after_the_selection_with_their_items_and_rotation()
    {
        DocumentState state = TestData.FourPagesWithItems();
        state = state.WithPages(state.Pages.SetItem(0, state.Pages[0] with { Rotation = Rotation.FromDegrees(90) }));
        PageId a = state.Pages[0].Id, b = state.Pages[1].Id;

        DocumentState result = PageEdits.Duplicate(state, TestData.Set(a, b), out var copies);

        Assert.Equal([0, 1, 0, 1, 2, 3], result.Pages.Select(p => p.SourceIndex));
        Assert.Equal(copies, [result.Pages[2].Id, result.Pages[3].Id]);
        Assert.Equal(90, result.Pages[2].Rotation.Degrees);
        Assert.Equal(state.ItemsOn(a).Count(), result.ItemsOn(copies[0]).Count());
        Assert.Equal(state.Items.Count + 3, result.Items.Count);
        Assert.Equal(result.Items.Count, result.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void Added_source_pages_go_where_they_were_dropped()
    {
        DocumentState state = DocumentState.ForSourcePages(2);
        DocumentState result = PageEdits.InsertSourcePages(state, [2, 3, 4, 5], 1);
        Assert.Equal([0, 2, 3, 4, 5, 1], result.Pages.Select(p => p.SourceIndex));
    }

    private static DocumentState Apply(UndoHistory history, DocumentState before, Func<DocumentState, DocumentState> change)
    {
        DocumentState after = change(before);
        history.Record(before, after);
        return after;
    }
}
