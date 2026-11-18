using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fillsign.App.Rendering;
using Fillsign.Core.Editing;
using Fillsign.Core.Model;
using Fillsign.Pdf.Import;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.App.ViewModels;

/// <summary>One open document: the source PDF, the editable page list, and the viewer state (Phase 2).</summary>
public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    public const double MinZoom = 0.5;
    public const double MaxZoom = 3;

    /// <summary>Zoom steps for the buttons and Mod +/−, in the range FR-2.2 allows.</summary>
    public static readonly ImmutableArray<double> ZoomSteps = [0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3];

    private readonly IEditorHost _host;
    private readonly UndoHistory _history = new();
    private readonly CancellationTokenSource _loading = new();
    private readonly HashSet<PageId> _keptDamaged = [];
    private readonly DocumentState _initialState;

    public EditorViewModel(SourceDocument source, IEditorHost host)
    {
        Source = source;
        _host = host;
        Renderer = new PageRenderService(source);
        Renderer.PageFailed += OnPageFailed;
        Thumbnails = new ThumbnailRequests(Renderer);
        State = DocumentState.ForSourcePages(source.PageCount);
        _initialState = State;
        RebuildPages();
    }

    public SourceDocument Source { get; }

    public PageRenderService Renderer { get; }

    /// <summary>The thumbnails the Pages panel currently shows, rendered after the main view.</summary>
    public ThumbnailRequests Thumbnails { get; }

    public ObservableCollection<PageViewModel> Pages { get; } = [];

    /// <summary>Raised when the view should scroll a page into view (go to page, Page Up/Down).</summary>
    public event Action<int>? ScrollToPageRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentStatus), nameof(HasUnsavedChanges))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(RedoCommand))]
    public partial DocumentState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatus), nameof(CurrentPageNumber))]
    public partial int CurrentPageIndex { get; set; }

    [ObservableProperty]
    public partial bool IsPagesPanelVisible { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentStatus), nameof(LoadingText), nameof(ContextHint))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingText), nameof(LoadingFraction))]
    public partial int LoadedPageCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatus), nameof(DamagedStatus), nameof(HasDamagedPages))]
    public partial int DamagedPageCount { get; private set; }

    public string Title => Source.Name;

    public string ZoomText => Zoom.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", string.Empty, StringComparison.Ordinal);

    public int CurrentPageNumber => CurrentPageIndex + 1;

    public string PageStatus => $"Page {CurrentPageNumber} of {Pages.Count}";

    public bool HasDamagedPages => DamagedPageCount > 0;

    public string DamagedStatus => DamagedPageCount == 1 ? "1 page can’t be shown" : $"{DamagedPageCount} pages can’t be shown";

    public string LoadingText => $"Loading pages: {LoadedPageCount} of {Source.PageCount}";

    public double LoadingFraction => Source.PageCount == 0 ? 1 : (double)LoadedPageCount / Source.PageCount;

    public bool HasUnsavedChanges => !State.Equals(_initialState);

    /// <summary>The line under the file name (INTERACTION-PATTERNS §1).</summary>
    public string DocumentStatus => IsLoading
        ? $"Opening · {Source.PageCount} pages"
        : !HasUnsavedChanges ? "Working copy · no changes yet"
        : State.ContainsSignature ? "Working copy · not saved to file · contains your signature"
        : "Working copy · not saved to file";

    public string ContextHint => IsLoading
        ? "You can start on page 1 now. The rest of the pages are still loading."
        : ToolHint;

    /// <summary>Loads page sizes in the background so page 1 is usable straight away (FR-1.9).</summary>
    public async Task LoadAsync()
    {
        IsLoading = !Source.IsFullyLoaded;
        LoadedPageCount = Source.LoadedPageCount;
        try
        {
            await Task.Run(
                () => Source.LoadPages(
                    loaded => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        LoadedPageCount = loaded;
                        RebuildPages();
                    }),
                    _loading.Token),
                _loading.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IsLoading = false;
        LoadedPageCount = Source.PageCount;
        RebuildPages();
    }

    /// <summary>Pages the view should show as damaged (FS-209): unreadable and not yet kept as they are.</summary>
    public bool ShowsDamagedCard(PageViewModel page) => page.IsDamaged && !_keptDamaged.Contains(page.Id);

    public PageSize DisplaySizeOf(PageEntry entry) => Source.GetPage(entry.SourceIndex).DisplaySize.Turned(entry.Rotation);

    [RelayCommand]
    private void TogglePagesPanel() => IsPagesPanelVisible = !IsPagesPanelVisible;

    [RelayCommand]
    private void GoToPage(int number)
    {
        if (Pages.Count == 0)
        {
            return;
        }

        int index = Math.Clamp(number - 1, 0, Pages.Count - 1);
        ScrollToPageRequested?.Invoke(index);
    }

    [RelayCommand]
    private void NextPage() => GoToPage(CurrentPageNumber + 1);

    [RelayCommand]
    private void PreviousPage() => GoToPage(CurrentPageNumber - 1);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        FinishTyping();
        CancelGesture();
        if (_history.Undo(State) is { } previous)
        {
            ApplyState(previous);
        }
    }

    private bool CanUndo() => _history.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        FinishTyping();
        CancelGesture();
        if (_history.Redo(State) is { } next)
        {
            ApplyState(next);
        }
    }

    private bool CanRedo() => _history.CanRedo;

    /// <summary>D13 "Keep page as it is": the page goes into copies unchanged; the card goes away.</summary>
    [RelayCommand]
    private void KeepDamagedPage(PageViewModel page)
    {
        _keptDamaged.Add(page.Id);
        RebuildPages();
    }

    /// <summary>D13 "Remove page N". Undoable; refused with an explanation for the last page (D9).</summary>
    [RelayCommand]
    private async Task RemovePageAsync(PageViewModel page)
    {
        if (!PageEdits.TryDelete(State, new HashSet<PageId> { page.Id }, out DocumentState result))
        {
            await _host.ShowMessageAsync(
                "Can’t delete every page",
                "A document needs at least one page. Add another page first, or open a different document.").ConfigureAwait(true);
            return;
        }

        Change(result);
    }

    /// <summary>Adds pages from files at a position in the page list (FR-6.8), or at the end.</summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths, int? at = null)
    {
        var files = await Task.WhenAll(paths.Select(path => InputFile.ReadAsync(path, CancellationToken.None))).ConfigureAwait(true);
        PdfiumDocument pages;
        ImmutableArray<OpenProblem> skipped;
        try
        {
            (pages, skipped) = await DocumentLoader.OpenForAddingAsync(files, _host.AskPasswordAsync, CancellationToken.None).ConfigureAwait(true);
        }
        catch (DocumentOpenException ex)
        {
            await _host.ShowProblemsAsync(ex.Problems, 0).ConfigureAwait(true);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ImmutableArray<int> added;
        using (pages)
        {
            added = await Task.Run(() => Source.AppendFrom(pages)).ConfigureAwait(true);
        }

        Change(PageEdits.InsertSourcePages(State, added, at ?? State.Pages.Length));
        if (!skipped.IsEmpty)
        {
            await _host.ShowProblemsAsync(skipped, files.Length - skipped.Length).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        _loading.Cancel();
        Renderer.PageFailed -= OnPageFailed;
        Renderer.Dispose();
        Source.Dispose();
        _loading.Dispose();
    }

    private void Change(DocumentState next)
    {
        _history.Record(State, next);
        ApplyState(next);
    }

    private void ApplyState(DocumentState next)
    {
        bool havePagesChanged = !next.Pages.SequenceEqual(State.Pages);
        State = next;
        Selection = Selections.Prune(Selection, next);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        if (havePagesChanged)
        {
            RebuildPages();
        }
        else
        {
            // Items only: refresh the edit markers without re-laying out the pages.
            foreach (PageViewModel page in Pages)
            {
                page.HasEdits = State.HasItems(page.Id);
            }

            OnPropertyChanged(nameof(DocumentStatus));
        }

        ItemsChanged?.Invoke();
    }

    private void OnPageFailed(int sourceIndex)
    {
        Source.MarkDamaged(sourceIndex);
        RebuildPages();
    }

    /// <summary>Brings the page list in line with the state and the loaded page facts, replacing only what changed.</summary>
    private void RebuildPages()
    {
        var damaged = Source.DamagedPages;
        for (int i = 0; i < State.Pages.Length; i++)
        {
            PageEntry entry = State.Pages[i];
            bool isLoaded = Source.IsPageLoaded(entry.SourceIndex);
            PageSize size = DisplaySizeOf(entry);
            PageViewModel? existing = i < Pages.Count ? Pages[i] : null;
            bool isSame = existing is not null
                && existing.Entry == entry
                && existing.Number == i + 1
                && existing.IsLoaded == isLoaded
                && existing.DisplaySize == size;
            PageViewModel page = isSame ? existing! : new PageViewModel(entry, i + 1, size, isLoaded);
            page.IsDamaged = damaged.Contains(entry.SourceIndex);
            page.HasEdits = State.HasItems(entry.Id);
            page.IsCurrent = i == CurrentPageIndex;
            if (existing is null)
            {
                Pages.Add(page);
            }
            else if (!isSame)
            {
                Pages[i] = page;
            }
        }

        while (Pages.Count > State.Pages.Length)
        {
            Pages.RemoveAt(Pages.Count - 1);
        }

        CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, Pages.Count - 1));
        DamagedPageCount = Pages.Count(page => page.IsDamaged);
        OnPropertyChanged(nameof(PageStatus));
        OnPropertyChanged(nameof(DocumentStatus));
        PagesChanged?.Invoke();
    }

    /// <summary>Raised after the page list or page sizes change, so the page view re-lays out.</summary>
    public event Action? PagesChanged;

    partial void OnCurrentPageIndexChanged(int value)
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].IsCurrent = i == value;
        }
    }
}
