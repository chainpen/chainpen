using Chainpen.Core.Editing;
using Chainpen.Core.Model;

namespace Chainpen.Core.Tests.Editing;

public sealed class UndoHistoryTests
{
    [Fact]
    public void Undo_and_redo_walk_the_history()
    {
        var history = new UndoHistory();
        DocumentState s0 = DocumentState.ForSourcePages(1);
        DocumentState s1 = ItemEdits.Add(s0, TestData.Tick(s0.Pages[0].Id));
        history.Record(s0, s1);
        DocumentState s2 = ItemEdits.Add(s1, TestData.Tick(s0.Pages[0].Id));
        history.Record(s1, s2);

        Assert.Same(s1, history.Undo(s2));
        Assert.Same(s0, history.Undo(s1));
        Assert.Null(history.Undo(s0));
        Assert.Same(s1, history.Redo(s0));
        Assert.Same(s2, history.Redo(s1));
        Assert.Null(history.Redo(s2));
    }

    [Fact]
    public void A_new_change_clears_redo()
    {
        var history = new UndoHistory();
        DocumentState s0 = DocumentState.ForSourcePages(1);
        DocumentState s1 = ItemEdits.Add(s0, TestData.Tick(s0.Pages[0].Id));
        history.Record(s0, s1);
        history.Undo(s1);
        Assert.True(history.CanRedo);
        history.Record(s0, ItemEdits.Add(s0, TestData.Text(s0.Pages[0].Id)));
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Keeps_at_least_100_steps_and_drops_the_oldest()
    {
        var history = new UndoHistory();
        DocumentState state = DocumentState.ForSourcePages(1);
        var first = state;
        for (int i = 0; i < 150; i++)
        {
            DocumentState next = ItemEdits.Add(state, TestData.Tick(state.Pages[0].Id));
            history.Record(state, next);
            state = next;
        }

        Assert.Equal(UndoHistory.Capacity, history.UndoCount);
        for (int i = 0; i < UndoHistory.Capacity; i++)
        {
            state = history.Undo(state)!;
        }

        Assert.Equal(50, state.Items.Count);
        Assert.NotEqual(first, state);
    }

    [Fact]
    public void Unchanged_states_are_not_recorded()
    {
        var history = new UndoHistory();
        DocumentState s0 = DocumentState.ForSourcePages(2);
        history.Record(s0, new DocumentState([.. s0.Pages], [.. s0.Items]));
        Assert.False(history.CanUndo);
    }
}
