using Chainpen.Core.Model;

namespace Chainpen.Core.Editing;

/// <summary>
/// Undo and redo over immutable document snapshots (FR-7.10). Keeps at least <see cref="Capacity"/> steps; making a
/// new change clears the redo list.
/// </summary>
public sealed class UndoHistory
{
    public const int Capacity = 100;

    private readonly LinkedList<DocumentState> _undo = new();
    private readonly Stack<DocumentState> _redo = new();

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    /// <summary>Records the state from before a change. Unchanged states aren't recorded.</summary>
    public void Record(DocumentState before, DocumentState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (before.Equals(after))
        {
            return;
        }

        _undo.AddLast(before);
        if (_undo.Count > Capacity)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
    }

    /// <summary>Returns the state to go back to, or null when there's nothing to undo.</summary>
    public DocumentState? Undo(DocumentState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_undo.Last is not { } last)
        {
            return null;
        }

        _undo.RemoveLast();
        _redo.Push(current);
        return last.Value;
    }

    public DocumentState? Redo(DocumentState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!_redo.TryPop(out DocumentState? next))
        {
            return null;
        }

        _undo.AddLast(current);
        return next;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
