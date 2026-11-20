using System.Collections.Immutable;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Editing;

/// <summary>Changes to the page list (FR-6). Items always stay with their page (FR-6.10).</summary>
public static class PageEdits
{
    /// <summary>
    /// Turns pages a quarter turn and turns their items with them (FR-6.5). <paramref name="displaySize"/> gives a
    /// page's display size with its current rotation.
    /// </summary>
    public static DocumentState Rotate(
        DocumentState document,
        IReadOnlySet<PageId> pages,
        bool clockwise,
        Func<PageEntry, PageSize> displaySize,
        ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(displaySize);
        var sizeBefore = document.Pages.Where(entry => pages.Contains(entry.Id)).ToDictionary(entry => entry.Id, displaySize);
        ImmutableArray<PageEntry> entries = [.. document.Pages.Select(entry => pages.Contains(entry.Id)
            ? entry with { Rotation = clockwise ? entry.Rotation.Clockwise() : entry.Rotation.CounterClockwise() }
            : entry)];
        ImmutableList<Item> items = document.Items.ConvertAll(item => sizeBefore.TryGetValue(item.Page, out PageSize size)
            ? ItemGeometry.RotateWithPage(item, clockwise, size, measurer)
            : item);
        return document with { Pages = entries, Items = items };
    }

    /// <summary>
    /// Removes pages and their items (FR-6.6). Returns false, leaving the document alone, when that would remove every
    /// page; the caller explains why.
    /// </summary>
    public static bool TryDelete(DocumentState document, IReadOnlySet<PageId> pages, out DocumentState result)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        ImmutableArray<PageEntry> remaining = [.. document.Pages.Where(entry => !pages.Contains(entry.Id))];
        if (remaining.IsEmpty)
        {
            result = document;
            return false;
        }

        result = document with { Pages = remaining, Items = document.Items.RemoveAll(item => pages.Contains(item.Page)) };
        return true;
    }

    /// <summary>
    /// Moves pages (keeping their relative order) so they land in the gap before the page currently at
    /// <paramref name="gap"/> (0 = top, Pages.Length = end) (FR-6.4).
    /// </summary>
    public static DocumentState Move(DocumentState document, IReadOnlySet<PageId> pages, int gap)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        if (gap < 0 || gap > document.Pages.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(gap), gap, "Gap must be between 0 and the page count.");
        }

        var moving = document.Pages.Where(entry => pages.Contains(entry.Id)).ToList();
        var rest = document.Pages.Where(entry => !pages.Contains(entry.Id)).ToList();
        int insertAt = document.Pages.Take(gap).Count(entry => !pages.Contains(entry.Id));
        rest.InsertRange(insertAt, moving);
        return document.WithPages([.. rest]);
    }

    /// <summary>
    /// Copies pages with their rotation and items, placed straight after the last of them (FR-6.7, FR-7.3).
    /// Returns the copies' ids.
    /// </summary>
    public static DocumentState Duplicate(DocumentState document, IReadOnlySet<PageId> pages, out ImmutableArray<PageId> copies)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        var originals = document.Pages.Where(entry => pages.Contains(entry.Id)).ToList();
        int after = document.Pages.Select((entry, index) => pages.Contains(entry.Id) ? index : -1).Max();
        if (after < 0)
        {
            copies = [];
            return document;
        }

        var clips = originals.Select(entry => new PageClip(entry, [.. document.ItemsOn(entry.Id)])).ToList();
        return InsertCopies(document, clips, after + 1, out copies);
    }

    /// <summary>Inserts copies of pages (with fresh page and item ids) at a position. Used by duplicate and paste.</summary>
    public static DocumentState InsertCopies(DocumentState document, IReadOnlyList<PageClip> clips, int at, out ImmutableArray<PageId> copies)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(clips);
        at = Math.Clamp(at, 0, document.Pages.Length);
        var entries = new List<PageEntry>();
        var items = new List<Item>();
        foreach (PageClip clip in clips)
        {
            var entry = clip.Page with { Id = PageId.New() };
            entries.Add(entry);
            items.AddRange(ItemEdits.CopiesOn(clip.Items, entry.Id));
        }

        copies = [.. entries.Select(entry => entry.Id)];
        return document with { Pages = document.Pages.InsertRange(at, entries), Items = document.Items.AddRange(items) };
    }

    /// <summary>Adds new source pages (from added files) at a position, with no items.</summary>
    public static DocumentState InsertSourcePages(DocumentState document, IEnumerable<int> sourceIndices, int at)
    {
        ArgumentNullException.ThrowIfNull(document);
        at = Math.Clamp(at, 0, document.Pages.Length);
        return document.WithPages(document.Pages.InsertRange(at, sourceIndices.Select(PageEntry.ForSource)));
    }
}

/// <summary>A page with its items, as copied or duplicated.</summary>
public sealed record PageClip(PageEntry Page, ImmutableArray<Item> Items);
