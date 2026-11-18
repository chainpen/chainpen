using System.Collections.Immutable;

namespace Fillsign.Core.Model;

/// <summary>
/// Everything the user can change and undo: the page list and the items. Immutable, so an undo snapshot is just a
/// reference (FR-7.10). The source PDF itself never changes while editing.
/// </summary>
public sealed record DocumentState(ImmutableArray<PageEntry> Pages, ImmutableList<Item> Items)
{
    public static DocumentState ForSourcePages(int count) =>
        new([.. Enumerable.Range(0, count).Select(PageEntry.ForSource)], []);

    public int IndexOf(PageId page)
    {
        for (int i = 0; i < Pages.Length; i++)
        {
            if (Pages[i].Id == page)
            {
                return i;
            }
        }

        return -1;
    }

    public PageEntry? FindPage(PageId page)
    {
        int index = IndexOf(page);
        return index < 0 ? null : Pages[index];
    }

    public IEnumerable<Item> ItemsOn(PageId page) => Items.Where(item => item.Page == page);

    public Item? FindItem(ItemId id) => Items.Find(item => item.Id == id);

    /// <summary>Whether a page has edits (shown in the pages overview, FR-6.1).</summary>
    public bool HasItems(PageId page) => Items.Exists(item => item.Page == page);

    public bool ContainsSignature => Items.Exists(item => item is SignatureItem);

    public DocumentState WithItems(ImmutableList<Item> items) => this with { Items = items };

    public DocumentState WithPages(ImmutableArray<PageEntry> pages) => this with { Pages = pages };

    // ImmutableArray/ImmutableList compare by reference; compare contents so "did anything change?" works.
    public bool Equals(DocumentState? other) =>
        other is not null && Pages.SequenceEqual(other.Pages) && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Pages.Length, Items.Count);
}
