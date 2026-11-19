using Fillsign.Core.Editing;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Tests.Editing;

public sealed class ClipboardAndSelectionTests
{
    private static readonly ApproximateTextMeasurer s_measurer = ApproximateTextMeasurer.Instance;

    /// <summary>REQUIREMENTS §7 scenario 7: box-select three ticks, move, copy/paste ×3, delete, undo, redo.</summary>
    [Fact]
    public void Scenario_7_box_select_move_paste_delete_undo_redo()
    {
        var history = new UndoHistory();
        DocumentState state = DocumentState.ForSourcePages(1);
        PageId page = state.Pages[0].Id;
        foreach (int y in (int[])[100, 130, 160])
        {
            state = ItemEdits.Add(state, TestData.Tick(page, 50, y));
        }

        state = ItemEdits.Add(state, TestData.Text(page, 300, 500)); // outside the box

        Selection selection = Selections.SelectInBox(Selection.None, state, page, new PageRect(40, 90, 40, 100), additive: false, s_measurer);
        var selected = Assert.IsType<ItemSelection>(selection).Items;
        Assert.Equal(3, selected.Count);

        DocumentState before = state;
        state = ItemEdits.MoveGroup(state, selected, 20, 10, _ => TestData.A4, s_measurer);
        history.Record(before, state);
        Assert.All(state.Items.Where(i => selected.Contains(i.Id)), item => Assert.Equal(70, item.X));

        var clipboard = new Clipboard();
        clipboard.CopyItems(state, selected);
        var pastes = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            before = state;
            state = clipboard.PasteItems(state, page, TestData.A4, s_measurer, out var pasted);
            history.Record(before, state);
            Assert.Equal(3, pasted.Length);
            pastes.Add(state.FindItem(pasted[0])!.X);
        }

        Assert.Equal([82, 94, 106], pastes); // each paste shifts a little further
        Assert.Equal(13, state.Items.Count);

        before = state;
        state = ItemEdits.Remove(state, selected);
        history.Record(before, state);
        Assert.Equal(10, state.Items.Count);

        DocumentState undone = history.Undo(state)!;
        Assert.Equal(13, undone.Items.Count);
        Assert.Equal(state, history.Redo(undone));
    }

    /// <summary>REQUIREMENTS §7 scenario 8: copy and paste two pages with their edits.</summary>
    [Fact]
    public void Scenario_8_copy_and_paste_pages()
    {
        DocumentState state = DocumentState.ForSourcePages(2);
        state = ItemEdits.Add(state, TestData.Tick(state.Pages[0].Id));
        state = ItemEdits.Add(state, TestData.Text(state.Pages[1].Id));

        Selection selection = Selections.SelectAllPages(state);
        var clipboard = new Clipboard();
        clipboard.CopyPages(state, ((PageSelection)selection).Pages);
        state = clipboard.PastePages(state, state.Pages.Length, out var pasted);

        Assert.Equal(4, state.Pages.Length);
        Assert.Equal([0, 1, 0, 1], state.Pages.Select(p => p.SourceIndex));
        Assert.Equal(4, state.Items.Count);
        Assert.Single(state.ItemsOn(pasted[0]));
        Assert.Single(state.ItemsOn(pasted[1]));
    }

    [Fact]
    public void Pasting_onto_another_page_keeps_the_position()
    {
        DocumentState state = DocumentState.ForSourcePages(2);
        MarkItem tick = TestData.Tick(state.Pages[0].Id, 40, 60);
        state = ItemEdits.Add(state, tick);
        var clipboard = new Clipboard();
        clipboard.CopyItems(state, TestData.Set(tick.Id));
        state = clipboard.PasteItems(state, state.Pages[1].Id, TestData.A4, s_measurer, out var pasted);
        Item copy = state.FindItem(pasted[0])!;
        Assert.Equal((40.0, 60.0), (copy.X, copy.Y));

        // A paste elsewhere doesn't count towards the step down on the copied page: the next one there steps once.
        state = clipboard.PasteItems(state, state.Pages[0].Id, TestData.A4, s_measurer, out pasted);
        Assert.Equal(40 + Clipboard.PasteOffset, state.FindItem(pasted[0])!.X);
    }

    [Fact]
    public void Pasted_items_stay_on_the_page()
    {
        DocumentState state = DocumentState.ForSourcePages(1);
        MarkItem tick = TestData.Tick(state.Pages[0].Id, 595 - 14, 842 - 14);
        state = ItemEdits.Add(state, tick);
        var clipboard = new Clipboard();
        clipboard.CopyItems(state, TestData.Set(tick.Id));
        state = clipboard.PasteItems(state, tick.Page, TestData.A4, s_measurer, out var pasted);
        Assert.Equal((581.0, 828.0), (state.FindItem(pasted[0])!.X, state.FindItem(pasted[0])!.Y));
    }

    [Fact]
    public void Item_clicks_replace_or_toggle()
    {
        ItemId a = ItemId.New(), b = ItemId.New();
        Selection s = Selections.SelectItem(Selection.None, a, SelectMode.Replace);
        s = Selections.SelectItem(s, b, SelectMode.Toggle);
        Assert.Equal(2, ((ItemSelection)s).Items.Count);
        s = Selections.SelectItem(s, a, SelectMode.Toggle);
        Assert.Equal([b], ((ItemSelection)s).Items);
        Assert.True(Selections.SelectItem(s, b, SelectMode.Toggle).IsEmpty);
        Assert.Equal([a], ((ItemSelection)Selections.SelectItem(s, a, SelectMode.Replace)).Items);
    }

    [Fact]
    public void Page_clicks_support_toggle_and_range()
    {
        DocumentState state = DocumentState.ForSourcePages(5);
        PageId[] ids = [.. state.Pages.Select(p => p.Id)];
        Selection s = Selections.SelectPage(Selection.None, state, ids[1], SelectMode.Replace);
        s = Selections.SelectPage(s, state, ids[3], SelectMode.Range);
        Assert.Equal(ids[1..4].ToHashSet(), ((PageSelection)s).Pages.ToHashSet());
        s = Selections.SelectPage(s, state, ids[2], SelectMode.Toggle);
        Assert.Equal(2, ((PageSelection)s).Pages.Count);
    }

    [Fact]
    public void Selecting_items_after_pages_starts_over()
    {
        DocumentState state = TestData.FourPagesWithItems();
        Selection pages = Selections.SelectAllPages(state);
        Selection items = Selections.SelectAllOnPage(state, state.Pages[0].Id);
        Assert.IsType<PageSelection>(pages);
        Assert.Equal(2, Assert.IsType<ItemSelection>(items).Items.Count);
        Assert.IsType<ItemSelection>(Selections.SelectItem(pages, state.Items[0].Id, SelectMode.Toggle));
    }

    [Fact]
    public void Pruning_drops_deleted_ids()
    {
        DocumentState state = TestData.FourPagesWithItems();
        Selection s = new ItemSelection([state.Items[0].Id, ItemId.New()]);
        Assert.Single(((ItemSelection)Selections.Prune(s, state)).Items);
        Assert.True(Selections.Prune(new ItemSelection([ItemId.New()]), state).IsEmpty);
    }

    [Fact]
    public void Colour_and_size_apply_to_everything_selected()
    {
        DocumentState state = TestData.FourPagesWithItems();
        var all = state.Items.Select(i => i.Id).ToHashSet();
        DocumentState red = ItemEdits.SetColor(state, all, InkColor.Red);
        Assert.Equal(InkColor.Red, red.Items.OfType<TextItem>().Single().Color);
        Assert.Equal(InkColor.Red, red.Items.OfType<InkItem>().Single().Color);

        DocumentState bigger = ItemEdits.BumpSize(state, all, +1);
        Assert.Equal(13, bigger.Items.OfType<TextItem>().Single().FontSize);
        Assert.Equal(16, bigger.Items.OfType<MarkItem>().Single().Size);
        Assert.Equal(3, bigger.Items.OfType<ShapeItem>().Single().StrokeWidth);
        Assert.Equal(1.5, ItemEdits.BumpSize(state, all, -1).Items.OfType<InkItem>().Single().StrokeWidth);
    }

    [Fact]
    public void Resizing_keeps_signature_proportions_and_scales_text()
    {
        var page = PageId.New();
        var signature = (SignatureItem)ItemEdits.Resize(TestData.Signature(page), 240, 10, s_measurer);
        Assert.Equal((240.0, 80.0), (signature.Width, signature.Height));

        var shape = (ShapeItem)ItemEdits.Resize(TestData.Rectangle(page), 200, 10, s_measurer);
        Assert.Equal((200.0, 10.0), (shape.Width, shape.Height));

        TextItem text = TestData.Text(page);
        var resized = (TextItem)ItemEdits.Resize(text, 1, 2 * 12 * TextItem.LineHeight, s_measurer);
        Assert.Equal(24, resized.FontSize);
    }
}
