using System.Collections.Immutable;
using Chainpen.Core.Geometry;
using Chainpen.Core.Model;

namespace Chainpen.Core.Editing;

/// <summary>Either items or pages are selected, never both.</summary>
public closed record Selection
{
    public static readonly Selection None = new NoSelection();

    public bool IsEmpty => this is NoSelection;
}

public sealed record NoSelection : Selection;

/// <summary>Selected items. Equality is by contents.</summary>
public sealed record ItemSelection(ImmutableHashSet<ItemId> Items) : Selection
{
    public bool Equals(ItemSelection? other) => other is not null && Items.SetEquals(other.Items);

    public override int GetHashCode() => Items.Count;
}

/// <summary>Selected pages, plus the page a Shift-click range starts from.</summary>
public sealed record PageSelection(ImmutableHashSet<PageId> Pages, PageId Anchor) : Selection
{
    public bool Equals(PageSelection? other) => other is not null && Anchor == other.Anchor && Pages.SetEquals(other.Pages);

    public override int GetHashCode() => HashCode.Combine(Pages.Count, Anchor);
}

/// <summary>How a click changes the selection.</summary>
public enum SelectMode
{
    /// <summary>Plain click: select only this.</summary>
    Replace,

    /// <summary>Mod-click (and Shift-click on items): add or remove this.</summary>
    Toggle,

    /// <summary>Shift-click on pages: select the range from the anchor.</summary>
    Range,
}

/// <summary>Selection rules (FR-5.1, 5.2, 6.3, 7.4).</summary>
public static class Selections
{
    public static Selection SelectItem(Selection current, ItemId item, SelectMode mode)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (mode == SelectMode.Replace || current is not ItemSelection existing)
        {
            return new ItemSelection([item]);
        }

        ImmutableHashSet<ItemId> removed = existing.Items.Remove(item);
        ImmutableHashSet<ItemId> items = removed.Count < existing.Items.Count ? removed : existing.Items.Add(item);
        return items.IsEmpty ? Selection.None : new ItemSelection(items);
    }

    /// <summary>
    /// Items touched by a dragged box on one page. <paramref name="additive"/> (Shift or Mod held) adds them to the
    /// current item selection.
    /// </summary>
    public static Selection SelectInBox(Selection current, DocumentState document, PageId page, PageRect box, bool additive, ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(document);
        var hits = document.ItemsOn(page).Where(item => ItemGeometry.Bounds(item, measurer).Intersects(box)).Select(item => item.Id);
        ImmutableHashSet<ItemId> items = additive && current is ItemSelection existing ? existing.Items.Union(hits) : [.. hits];
        return items.IsEmpty ? (additive ? current : Selection.None) : new ItemSelection(items);
    }

    /// <summary>Mod+A with no pages selected: everything on the page in view.</summary>
    public static Selection SelectAllOnPage(DocumentState document, PageId page)
    {
        ArgumentNullException.ThrowIfNull(document);
        ImmutableHashSet<ItemId> items = [.. document.ItemsOn(page).Select(item => item.Id)];
        return items.IsEmpty ? Selection.None : new ItemSelection(items);
    }

    public static Selection SelectPage(Selection current, DocumentState document, PageId page, SelectMode mode)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(document);
        if (current is not PageSelection existing || mode == SelectMode.Replace)
        {
            return new PageSelection([page], page);
        }

        if (mode == SelectMode.Toggle)
        {
            ImmutableHashSet<PageId> removed = existing.Pages.Remove(page);
            ImmutableHashSet<PageId> pages = removed.Count < existing.Pages.Count ? removed : existing.Pages.Add(page);
            return pages.IsEmpty ? Selection.None : new PageSelection(pages, page);
        }

        int from = document.IndexOf(existing.Anchor);
        int to = document.IndexOf(page);
        if (from < 0)
        {
            return new PageSelection([page], page);
        }

        int low = Math.Min(from, to), high = Math.Max(from, to);
        ImmutableHashSet<PageId> range = [.. document.Pages[low..(high + 1)].Select(entry => entry.Id)];
        return new PageSelection(range, existing.Anchor);
    }

    public static Selection SelectAllPages(DocumentState document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PageSelection([.. document.Pages.Select(entry => entry.Id)], document.Pages[0].Id);
    }

    /// <summary>Drops ids that no longer exist (after undo, delete or a page change).</summary>
    public static Selection Prune(Selection current, DocumentState document)
    {
        ArgumentNullException.ThrowIfNull(document);
        switch (current)
        {
            case ItemSelection items:
            {
                var alive = items.Items.Where(id => document.FindItem(id) is not null).ToImmutableHashSet();
                return alive.IsEmpty ? Selection.None : new ItemSelection(alive);
            }

            case PageSelection pages:
            {
                var alive = pages.Pages.Where(id => document.IndexOf(id) >= 0).ToImmutableHashSet();
                if (alive.IsEmpty)
                {
                    return Selection.None;
                }

                return new PageSelection(alive, alive.Contains(pages.Anchor) ? pages.Anchor : alive.First());
            }

            default:
                return Selection.None;
        }
    }
}
