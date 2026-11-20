using System.Collections.Immutable;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Editing;

/// <summary>What Mod+C or Mod+X put on the clipboard (FR-7.1).</summary>
public closed record ClipboardContent;

public sealed record ItemsClip(ImmutableArray<Item> Items) : ClipboardContent;

public sealed record PagesClip(ImmutableArray<PageClip> Pages) : ClipboardContent;

/// <summary>
/// The app's clipboard model: holds the copied content and counts pastes, so each paste onto the page the items came
/// from shifts a little further (FR-7.2).
/// </summary>
public sealed class Clipboard
{
    /// <summary>How far each repeated paste on the same page shifts, in points.</summary>
    public const double PasteOffset = 12;

    private int _pasteCount;

    public ClipboardContent? Content { get; private set; }

    public void CopyItems(DocumentState document, IReadOnlySet<ItemId> ids)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        Set(new ItemsClip([.. document.Items.Where(item => ids.Contains(item.Id))]));
    }

    public void CopyPages(DocumentState document, IReadOnlySet<PageId> pages)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        Set(new PagesClip([.. document.Pages.Where(entry => pages.Contains(entry.Id)).Select(entry => new PageClip(entry, [.. document.ItemsOn(entry.Id)]))]));
    }

    public void Set(ClipboardContent content)
    {
        Content = content;
        _pasteCount = 0;
    }

    /// <summary>
    /// Pastes copied items onto <paramref name="target"/> (the page in view). Copies landing on the page they came
    /// from shift by <see cref="PasteOffset"/> per paste; all copies stay on the page. Returns the new item ids.
    /// </summary>
    public DocumentState PasteItems(DocumentState document, PageId target, PageSize targetSize, ITextMeasurer measurer, out ImmutableArray<ItemId> pasted)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Content is not ItemsClip clip || clip.Items.IsEmpty)
        {
            pasted = [];
            return document;
        }

        // Only pastes back onto the copied page step down and right; elsewhere the copy lands where it was.
        bool isSamePage = clip.Items.All(item => item.Page == target);
        if (isSamePage)
        {
            _pasteCount++;
        }

        double offset = isSamePage ? PasteOffset * _pasteCount : 0;
        var copies = ItemEdits.CopiesOn(clip.Items, target)
            .Select(item =>
            {
                PageRect box = ItemGeometry.Bounds(item, measurer);
                PagePoint position = ItemGeometry.ClampToPage(box with { X = box.X + offset, Y = box.Y + offset }, targetSize);
                return ItemGeometry.MoveTo(item, position);
            })
            .ToImmutableArray();
        pasted = [.. copies.Select(item => item.Id)];
        return document.WithItems(document.Items.AddRange(copies));
    }

    /// <summary>Pastes copied pages at a position (after the selected pages or the page in view).</summary>
    public DocumentState PastePages(DocumentState document, int at, out ImmutableArray<PageId> pasted)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Content is not PagesClip clip || clip.Pages.IsEmpty)
        {
            pasted = [];
            return document;
        }

        return PageEdits.InsertCopies(document, clip.Pages, at, out pasted);
    }
}
