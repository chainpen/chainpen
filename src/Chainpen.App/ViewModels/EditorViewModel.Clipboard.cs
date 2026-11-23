using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Chainpen.App.Localization;
using Chainpen.Core.Editing;
using Chainpen.Core.Geometry;
using Chainpen.Core.Model;
using Chainpen.Core.Session;

namespace Chainpen.App.ViewModels;

/// <summary>What copying puts on the system clipboard: Chainpen's own items (if any) and plain text for other apps.</summary>
/// <param name="Items">Copied items in Chainpen's private format, or null when only pages were copied.</param>
/// <param name="Text">The copied text items' text, so pasting into another app gives the words.</param>
public sealed record SystemClip(byte[]? Items, string? Text);

/// <summary>
/// FS-702: the system clipboard (FR-7.1, 7.2, 7.14). Items go on it in Chainpen's private format, so they paste into
/// another Chainpen window, plus plain text. Text copied in another app pastes as a text item. Pages are copied within
/// this document only: they refer to its source pages.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>Chainpen's private clipboard format.</summary>
    public const string ItemsFormat = "x-chainpen-items";

    // Pasted text arrives at this size and is kept to a sensible length.
    private const int MaxPastedTextLength = 2_000;
    private static readonly ByteStamp s_noFile = new(1, new string('0', 64));

    // Identifies what this window last put on the clipboard, so repeated pastes keep shifting (FR-7.2).
    private byte[]? _lastCopied;

    /// <summary>Copies the selection (items, or pages when organising) and returns what to put on the system clipboard.</summary>
    public SystemClip? CopyToClipboard()
    {
        if (IsOrganising)
        {
            if (HasPageSelection)
            {
                _clipboard.CopyPages(State, SelectedPages);
            }

            return null;
        }

        if (Selection.IsEmpty)
        {
            return null;
        }

        Copy();
        ImmutableArray<Item> items = [.. State.Items.Where(item => SelectedIds.Contains(item.Id))];

        // Signatures stay in this window: the system clipboard is readable by every app and kept by clipboard history
        // and syncing, while saved signatures are encrypted at rest. Pasting here still brings them, from this
        // window's own clipboard, because the system clipboard's copy matches what was put there.
        _lastCopied = Serialise([.. items.Where(item => item is not SignatureItem)]);
        string text = string.Join(Environment.NewLine, items.OfType<TextItem>().Select(item => item.Text.Replace("\n", Environment.NewLine, StringComparison.Ordinal)));
        return new SystemClip(_lastCopied, text.Length > 0 ? text : null);
    }

    /// <summary>Cut: copy, then delete (pages show "N pages deleted · Undo").</summary>
    public async Task<SystemClip?> CutToClipboardAsync()
    {
        SystemClip? clip = CopyToClipboard();
        if (IsOrganising && HasPageSelection)
        {
            await DeletePagesCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
        else if (!IsOrganising && !Selection.IsEmpty)
        {
            DeleteSelection();
        }

        return clip;
    }

    /// <summary>
    /// Pastes (FR-7.2, 7.14): pages after the selected pages (or the page in view) when organising; otherwise Chainpen
    /// items from the clipboard, or another app's text as a text item on the page in view. Returns false when there was
    /// nothing it could paste.
    /// </summary>
    public bool PasteFromClipboard(byte[]? chainpenItems, string? text)
    {
        if (IsOrganising)
        {
            return PastePages();
        }

        if (Pages.Count == 0)
        {
            return false;
        }

        PageId target = Pages[CurrentPageIndex].Id;
        if (chainpenItems is not null)
        {
            // What this window copied keeps its paste count, so each paste shifts a little further.
            if (_lastCopied is null || !chainpenItems.AsSpan().SequenceEqual(_lastCopied))
            {
                if (Deserialise(chainpenItems) is not { IsEmpty: false } items)
                {
                    return false;
                }

                _clipboard.Set(new ItemsClip(items));
                _lastCopied = chainpenItems;
            }

            Paste(target);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            PasteText(target, text);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A picture from another app (FR-7.14, FS-715) goes in the middle of the page in view at up to 300 pt wide,
    /// keeping its shape, then is selected to move or resize. Returns false when it can't be read. When the document
    /// has no room for another picture it says so instead, and returns true.
    /// </summary>
    public bool PastePicture(byte[] imageFile)
    {
        ArgumentNullException.ThrowIfNull(imageFile);
        if (IsOrganising || IsViewOnly || Pages.Count == 0)
        {
            return false;
        }

        SignatureImage image;
        try
        {
            image = Chainpen.Pdf.Import.PictureImport.FromImageFile(imageFile);
        }
        catch (InvalidDataException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"The pasted picture couldn't be read. {ex.Message}");
            return false;
        }

        if (!SessionJson.HasRoomFor(State, image))
        {
            // Saved with more pictures than a working copy reads back, the file would open with nothing editable.
            Tell(Strings.Editor_NoRoomForAnotherPicture);
            return true;
        }

        PageId target = Pages[CurrentPageIndex].Id;
        PageSize page = PageSizeOf(target);

        // Screen pixels are about 0.75 pt; big pictures start smaller so they don't cover the page.
        double width = Math.Min(Math.Min(image.PixelWidth * 0.75, PictureItem.MaxStartWidth), page.Width * 0.8);
        double height = width * image.PixelHeight / Math.Max(1, image.PixelWidth);
        var picture = new PictureItem(ItemId.New(), target, (page.Width - width) / 2, (page.Height - height) / 2, image, width, height);
        SwitchTool(EditorTool.Select);
        Change(ItemEdits.Add(State, picture));
        Selection = new ItemSelection([picture.Id]);
        Changed();
        return true;
    }

    /// <summary>Another app's text becomes a text item near the top-left of the page in view, then is selected.</summary>
    private void PasteText(PageId target, string text)
    {
        string cleaned = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
        if (cleaned.Length > MaxPastedTextLength)
        {
            cleaned = cleaned[..MaxPastedTextLength];
        }

        var item = new TextItem(ItemId.New(), target, 72, 72, cleaned, DefaultFontSize, DefaultColour);
        PageRect box = ItemGeometry.Bounds(item, Measurer);
        item = (TextItem)ItemGeometry.MoveTo(item, ItemGeometry.ClampToPage(box, PageSizeOf(target)));
        FinishTyping();
        SwitchTool(EditorTool.Select);
        Change(State.WithItems(State.Items.Add(item)));
        Selection = new ItemSelection([item.Id]);
        Changed();
    }

    /// <summary>Pastes copied pages after the selected pages, or after the page in view (FR-7.2).</summary>
    private bool PastePages()
    {
        int at = SelectedPages.IsEmpty
            ? CurrentPageIndex + 1
            : State.Pages.ToList().FindLastIndex(entry => SelectedPages.Contains(entry.Id)) + 1;
        DocumentState next = _clipboard.PastePages(State, at, out ImmutableArray<PageId> pasted);
        if (pasted.IsEmpty)
        {
            return false;
        }

        Notice = null;
        Change(next);
        SetPageSelection(pasted);
        return true;
    }

    /// <summary>
    /// The items in the working copy's JSON format (docs/FILE-FORMAT.md), on one stand-in page, so the strict reader
    /// that checks working copies also checks whatever comes back off the clipboard.
    /// </summary>
    private static byte[] Serialise(ImmutableArray<Item> items)
    {
        var page = PageEntry.ForSource(0);
        ImmutableList<Item> onPage = [.. items.Select(item => ItemGeometry.MoveTo(item, new PagePoint(item.X, item.Y)) with { Page = page.Id })];
        return SessionJson.Write(new SessionRecord(s_noFile, s_noFile, 1, new DocumentState([page], onPage)));
    }

    /// <summary>Items from the clipboard, or null when it holds something that isn't a valid Chainpen clip.</summary>
    private static ImmutableArray<Item>? Deserialise(byte[] bytes)
    {
        try
        {
            return [.. SessionJson.Read(bytes).Document.Items];
        }
        catch (Exception ex) when (ex is SessionFormatException or JsonException or DecoderFallbackException)
        {
            System.Diagnostics.Trace.TraceWarning($"The clipboard's Chainpen items couldn't be read. {ex.Message}");
            return null;
        }
    }
}
