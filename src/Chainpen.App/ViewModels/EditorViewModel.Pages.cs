using System.Collections.Immutable;
using Chainpen.App.Localization;
using Chainpen.Core.Editing;
using Chainpen.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chainpen.App.ViewModels;

/// <summary>
/// Phase 5: page selection and page actions (FR-6). One page selection is shared by the Pages panel and the Organise
/// pages view. Actions apply to every selected page, or from a page's own menu to that page when it isn't selected
/// (FR-6.9). Every action is one undo step.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>Organise pages (FR-6.2) instead of editing content.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingContent), nameof(ShowsPagesPanel))]
    public partial bool IsOrganising { get; set; }

    /// <summary>The Pages panel shows while editing content, unless hidden; Organise pages has its own grid.</summary>
    public bool ShowsPagesPanel => IsPagesPanelVisible && !IsOrganising;

    public bool IsEditingContent
    {
        get => !IsOrganising;
        set => IsOrganising = !value;
    }

    /// <summary>The selected pages (FR-6.3). Ids, so the selection follows pages as they move.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPageCount), nameof(HasPageSelection), nameof(PageSelectionLabel), nameof(OrganiseHint))]
    [NotifyCanExecuteChangedFor(nameof(RotatePagesLeftCommand), nameof(RotatePagesRightCommand), nameof(DuplicatePagesCommand),
        nameof(DeletePagesCommand), nameof(MovePagesToCommand), nameof(MovePagesUpCommand), nameof(MovePagesDownCommand))]
    public partial ImmutableHashSet<PageId> SelectedPages { get; private set; } = [];

    /// <summary>Why a page action couldn't happen (D9), shown next to the actions until the selection changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPageMessage))]
    public partial string? PageMessage { get; private set; }

    public bool HasPageMessage => PageMessage is not null;

    /// <summary>The blue selection bar with page actions (🎨 4).</summary>
    public bool ShowsPageActions => IsOrganising && HasPageSelection;

    public bool ShowsOrganiseHint => IsOrganising && !HasPageSelection;

    public int SelectedPageCount => SelectedPages.Count;

    public bool HasPageSelection => !SelectedPages.IsEmpty;

    /// <summary>"2 pages selected", "All 4 pages selected".</summary>
    public string PageSelectionLabel => SelectedPages.Count switch
    {
        int count when count != 1 && count == Pages.Count => Text.Format(Strings.Editor_AllPagesSelected, count),
        int count => Text.Plural(count, Strings.Editor_PagesSelected_One, Strings.Editor_PagesSelected_Other),
    };

    public string OrganiseHint =>
        Controls.KeysExtension.ForThisComputer(Text.Plural(Pages.Count, Strings.Editor_OrganiseHint_One, Strings.Editor_OrganiseHint_Other), OperatingSystem.IsMacOS());

    /// <summary>Raised when the page selection changes, so the page lists show it.</summary>
    public event Action? PageSelectionChanged;

    /// <summary>Sets the selection from a page list (clicks, Shift and Mod clicks, arrow keys).</summary>
    public void SetPageSelection(IEnumerable<PageId> pages)
    {
        ImmutableHashSet<PageId> next = [.. pages.Where(id => State.Pages.Any(entry => entry.Id == id))];
        if (next.SetEquals(SelectedPages))
        {
            return;
        }

        SelectedPages = next;
        PageMessage = null;
    }

    /// <summary>
    /// The pages an action from <paramref name="page"/>'s own menu applies to: the whole selection when that page is
    /// part of it, otherwise just that page. With no page (the selection bar), the selection.
    /// </summary>
    public ImmutableHashSet<PageId> TargetsFor(PageViewModel? page) =>
        page is null || SelectedPages.Contains(page.Id) ? SelectedPages : [page.Id];

    /// <summary>Moves pages (keeping their order) into a gap: 0 is before page 1, the page count is the end (FR-6.4).</summary>
    public void MovePages(IReadOnlySet<PageId> pages, int gap)
    {
        if (pages.Count == 0)
        {
            return;
        }

        DocumentState moved = PageEdits.Move(State, pages, Math.Clamp(gap, 0, State.Pages.Length));
        if (!moved.Pages.SequenceEqual(State.Pages))
        {
            Notice = null;
            Change(moved);
        }
    }

    [RelayCommand]
    private void ShowOrganise() => IsOrganising = true;

    [RelayCommand]
    private void ShowEditContent() => IsOrganising = false;

    /// <summary>Double-click in Organise pages: back to editing, at that page.</summary>
    [RelayCommand]
    private void OpenPage(PageViewModel page)
    {
        IsOrganising = false;
        GoToPage(page.Number);
    }

    [RelayCommand]
    private void SelectAllPages() => SetPageSelection(State.Pages.Select(entry => entry.Id));

    [RelayCommand]
    private void ClearPageSelection() => SetPageSelection([]);

    [RelayCommand(CanExecute = nameof(CanActOnPages))]
    private void RotatePagesLeft(PageViewModel? page) => RotatePages(TargetsFor(page), clockwise: false);

    [RelayCommand(CanExecute = nameof(CanActOnPages))]
    private void RotatePagesRight(PageViewModel? page) => RotatePages(TargetsFor(page), clockwise: true);

    /// <summary>Copies pages with their edits and rotation straight after the originals; the copies are selected (FR-6.7).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnPages))]
    private void DuplicatePages(PageViewModel? page)
    {
        ImmutableHashSet<PageId> targets = TargetsFor(page);
        if (targets.IsEmpty)
        {
            return;
        }

        Notice = null;
        Change(PageEdits.Duplicate(State, targets, out ImmutableArray<PageId> copies));
        SetPageSelection(copies);
    }

    /// <summary>
    /// Deletes pages and their edits, with "Page 5 deleted · Undo" (FR-6.6, FR-7.11). Deleting every page is refused
    /// with the reason (D9).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActOnPages))]
    private async Task DeletePagesAsync(PageViewModel? page)
    {
        ImmutableHashSet<PageId> targets = TargetsFor(page);
        if (targets.IsEmpty)
        {
            return;
        }

        int[] numbers = [.. Pages.Where(p => targets.Contains(p.Id)).Select(p => p.Number)];
        if (!PageEdits.TryDelete(State, targets, out DocumentState result))
        {
            string reason = Strings.Editor_DeselectThePageToKeep;
            if (IsOrganising && page is null)
            {
                PageMessage = reason;
                return;
            }

            await _host.ShowMessageAsync(Strings.Editor_CantDeleteEveryPage, Pages.Count == 1
                ? Strings.Editor_ADocumentNeedsAtLeastOnePage
                : reason).ConfigureAwait(true);
            return;
        }

        Change(result);
        Notice = numbers.Length == 1 ? Text.Format(Strings.Editor_PageDeleted, numbers[0]) : Text.Format(Strings.Editor_PagesDeleted, numbers.Length);
    }

    /// <summary>Moves pages one place up, keeping them together (FR-6.4, the non-drag way).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnPages))]
    private void MovePagesUp(PageViewModel? page)
    {
        ImmutableHashSet<PageId> targets = TargetsFor(page);
        int first = FirstIndexOf(targets);
        if (first > 0)
        {
            MovePages(targets, first - 1);
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnPages))]
    private void MovePagesDown(PageViewModel? page)
    {
        ImmutableHashSet<PageId> targets = TargetsFor(page);
        int last = LastIndexOf(targets);
        if (last >= 0 && last < State.Pages.Length - 1)
        {
            // The gap after the page that follows the last moved one.
            MovePages(targets, last + 2);
        }
    }

    /// <summary>"Move to…": asks where, then moves (FR-6.4).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnPages))]
    private async Task MovePagesToAsync(PageViewModel? page)
    {
        ImmutableHashSet<PageId> targets = TargetsFor(page);
        if (targets.IsEmpty)
        {
            return;
        }

        int? gap = await _host.AskMovePositionAsync(targets.Count, State.Pages.Length).ConfigureAwait(true);
        if (gap is int chosen)
        {
            MovePages(targets, chosen);
        }
    }

    /// <summary>The Pages panel and page menus can always act on their own page; the selection bar needs a selection.</summary>
    private bool CanActOnPages(PageViewModel? page) => page is not null || !SelectedPages.IsEmpty;

    /// <summary>Turns pages a quarter turn; shapes and drawings turn with them, text and marks stay upright (FR-6.5).</summary>
    private void RotatePages(ImmutableHashSet<PageId> targets, bool clockwise)
    {
        if (targets.IsEmpty)
        {
            return;
        }

        Notice = null;
        Change(PageEdits.Rotate(State, targets, clockwise, DisplaySizeOf, Measurer));
    }

    private int FirstIndexOf(ImmutableHashSet<PageId> pages) => State.Pages.ToList().FindIndex(entry => pages.Contains(entry.Id));

    private int LastIndexOf(ImmutableHashSet<PageId> pages) => State.Pages.ToList().FindLastIndex(entry => pages.Contains(entry.Id));

    partial void OnSelectedPagesChanged(ImmutableHashSet<PageId> value)
    {
        Notice = null;
        foreach (PageViewModel page in Pages)
        {
            page.IsSelected = value.Contains(page.Id);
        }

        PageSelectionChanged?.Invoke();
    }

    partial void OnIsOrganisingChanged(bool value)
    {
        FinishTyping();
        CancelGesture();
        PlacingSignature = null;
        PageMessage = null;
    }
}
