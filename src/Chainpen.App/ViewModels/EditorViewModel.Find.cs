using Chainpen.App.Localization;
using Chainpen.Core.Finding;
using Chainpen.Core.Geometry;
using Chainpen.Core.Model;
using Chainpen.Pdf.Export;
using Chainpen.Pdf.Pdfium;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chainpen.App.ViewModels;

/// <summary>One place the words were found: on which page, and the boxes to highlight there.</summary>
public sealed record FindHit(PageId Page, int PageIndex, TextMatch Match);

/// <summary>
/// Find in the document (FS-974, 🎨 F1–F4): Mod+F opens a bar in place of the bar under the toolbar, every match is
/// highlighted on the pages, and Return or Mod+G moves between them. It searches the pages' own text and the text
/// added in Chainpen. Scans have no text until FS-953 reads it, so the bar says how many pages couldn't be searched.
/// </summary>
public sealed partial class EditorViewModel
{
    // A source page's characters don't change while it's open, so each is read once, off the UI thread.
    private readonly Dictionary<int, IReadOnlyList<PdfCharacter>> _textBySourcePage = [];
    private readonly Lock _textLock = new();
    private CancellationTokenSource? _search;

    /// <summary>How long typing pauses before the document is searched, so each letter doesn't start a search.</summary>
    public static readonly TimeSpan FindDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Raised when the find box should take focus (Mod+F, or Find on the toolbar).</summary>
    public event Action? FindFocusRequested;

    /// <summary>Raised when find closes, so focus goes back to the page rather than nowhere.</summary>
    public event Action? FindClosed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindStatus), nameof(FindNote), nameof(HasFindNote))]
    public partial bool IsFinding { get; private set; }

    [ObservableProperty]
    public partial string FindQuery { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindStatus))]
    public partial IReadOnlyList<FindHit> FindHits { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindStatus), nameof(CurrentFindHit))]
    public partial int FindIndex { get; private set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindStatus))]
    public partial bool IsSearching { get; private set; }

    /// <summary>Page numbers with no text to search: scans and pictures.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindNote), nameof(HasFindNote))]
    public partial IReadOnlyList<int> PagesWithoutText { get; private set; } = [];

    public FindHit? CurrentFindHit => FindIndex >= 0 && FindIndex < FindHits.Count ? FindHits[FindIndex] : null;

    /// <summary>"2 of 5", Strings.Find_NoMatches or Strings.Find_Searching; empty until something's typed.</summary>
    public string FindStatus =>
        TextMatcher.Fold(FindQuery).Length == 0 ? string.Empty
        : IsSearching ? Strings.Find_Searching
        : FindHits.Count == 0 ? Strings.Find_NoMatches
        : Text.Format(Strings.Find_MatchOfCount, FindIndex + 1, FindHits.Count);

    /// <summary>Why some pages weren't searched (F2): they're scans or pictures, with no text in them yet.</summary>
    public string FindNote => PagesWithoutText.Count switch
    {
        0 => string.Empty,
        1 => Text.Format(Strings.Find_PageIsAScan, PagesWithoutText[0]),
        int count when count == Pages.Count => Strings.Find_AllPagesAreScans,
        int count => Text.Format(Strings.Find_SomePagesAreScans, count),
    };

    public bool HasFindNote => IsFinding && FindNote.Length > 0;

    /// <summary>Mod+F: opens the find bar (or goes back to it) with the box ready to type in.</summary>
    [RelayCommand]
    private void OpenFind()
    {
        FinishTyping();
        if (IsCheckingGaps)
        {
            StopChecking();
        }

        if (IsOrganising)
        {
            // Matches are shown on the pages, which Organise pages doesn't show.
            ShowEditContentCommand.Execute(null);
        }

        if (!IsFinding)
        {
            IsFinding = true;
            Search(keepPlace: false, delay: TimeSpan.Zero);
        }

        FindFocusRequested?.Invoke();
    }

    /// <summary>Esc or Close find: the highlights go; the words are kept for the next time.</summary>
    [RelayCommand]
    private void CloseFind()
    {
        if (!IsFinding)
        {
            return;
        }

        _search?.Cancel();
        IsFinding = false;
        IsSearching = false;
        ShowHits([], -1, []);
        FindClosed?.Invoke();
    }

    /// <summary>Return, Mod+G or Next: the next match, round to the first after the last.</summary>
    [RelayCommand]
    private void NextMatch() => StepMatch(+1);

    /// <summary>Shift+Return, Mod+Shift+G or Previous.</summary>
    [RelayCommand]
    private void PreviousMatch() => StepMatch(-1);

    private void StepMatch(int step)
    {
        if (!IsFinding)
        {
            OpenFind();
            return;
        }

        if (FindHits.Count == 0)
        {
            return;
        }

        FindIndex = ((FindIndex < 0 ? (step > 0 ? -1 : 0) : FindIndex) + step + FindHits.Count) % FindHits.Count;
        ShowCurrentHit();
    }

    partial void OnFindQueryChanged(string value)
    {
        if (IsFinding)
        {
            Search(keepPlace: false, delay: FindDelay);
        }
    }

    /// <summary>The document changed (an edit, pages moved or turned): the matches are looked for again, keeping the place.</summary>
    private void RefreshFind()
    {
        if (IsFinding)
        {
            Search(keepPlace: true, delay: FindDelay);
        }
    }

    /// <summary>The matches to highlight on one page.</summary>
    public IEnumerable<(TextMatch Match, bool IsCurrent)> FindHitsOn(PageId page)
    {
        FindHit? current = CurrentFindHit;
        foreach (FindHit hit in FindHits)
        {
            if (hit.Page == page)
            {
                yield return (hit.Match, ReferenceEquals(hit, current));
            }
        }
    }

    private void Search(bool keepPlace, TimeSpan delay) => _ = SearchAsync(keepPlace, delay);

    private async Task SearchAsync(bool keepPlace, TimeSpan delay)
    {
        _search?.Cancel();
        _search?.Dispose();
        var search = new CancellationTokenSource();
        _search = search;
        string query = FindQuery;
        if (TextMatcher.Fold(query).Length == 0)
        {
            IsSearching = false;
            ShowHits([], -1, []);
            return;
        }

        FindHit? before = CurrentFindHit;
        DocumentState state = State;
        IsSearching = true;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, search.Token).ConfigureAwait(true);
            }

            (List<FindHit> hits, List<int> withoutText) = await Task.Run(() => FindAll(state, query, search.Token), search.Token).ConfigureAwait(true);
            if (search.IsCancellationRequested)
            {
                return;
            }

            IsSearching = false;
            ShowHits(hits, ChooseHit(hits, keepPlace ? before : null), withoutText);
            if (!keepPlace)
            {
                ShowCurrentHit();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search, or find closed.
        }
    }

    /// <summary>Every match in page order, and the pages that have no text to search.</summary>
    private (List<FindHit> Hits, List<int> WithoutText) FindAll(DocumentState state, string query, CancellationToken cancel)
    {
        var hits = new List<FindHit>();
        var withoutText = new List<int>();
        for (int index = 0; index < state.Pages.Length; index++)
        {
            cancel.ThrowIfCancellationRequested();
            PageEntry entry = state.Pages[index];
            var onPage = new List<TextMatch>();
            if (entry.SourceIndex < Source.PageCount && Source.IsPageLoaded(entry.SourceIndex) && !Source.DamagedPages.Contains(entry.SourceIndex))
            {
                IReadOnlyList<PdfCharacter> text = TextOf(entry.SourceIndex);
                if (PageText.HasText(text))
                {
                    onPage.AddRange(PageText.Find(text, Source.GetPage(entry.SourceIndex), entry.Rotation, query));
                }
                else
                {
                    withoutText.Add(index + 1);
                }
            }

            onPage.AddRange(MatchesInAddedText(state, entry.Id, query));
            // Top to bottom, then left to right: the page's text and what was typed on it, in one reading order.
            hits.AddRange(onPage.OrderBy(match => Math.Round(match.Bounds.Y / 4)).ThenBy(match => match.Bounds.X).Select(match => new FindHit(entry.Id, index, match)));
        }

        return (hits, withoutText);
    }

    /// <summary>Matches in text typed in Chainpen, with a box round just the matching words of each line.</summary>
    private IEnumerable<TextMatch> MatchesInAddedText(DocumentState state, PageId page, string query)
    {
        foreach (TextItem text in state.ItemsOn(page).OfType<TextItem>())
        {
            string[] lines = text.Lines;
            double[] offsets = ItemGeometry.LineOffsets(text, Measurer);
            double lineHeight = text.FontSize * TextItem.LineHeight;
            for (int line = 0; line < lines.Length; line++)
            {
                foreach ((int start, int length) in TextMatcher.FindInLine(lines[line], query))
                {
                    double left = text.X + offsets[line] + Measurer.MeasureLineWidth(lines[line][..start], text.FontSize);
                    double width = Measurer.MeasureLineWidth(lines[line].Substring(start, length), text.FontSize);
                    yield return new TextMatch([new PageRect(left, text.Y + (line * lineHeight), width, lineHeight)]);
                }
            }
        }
    }

    private IReadOnlyList<PdfCharacter> TextOf(int sourceIndex)
    {
        lock (_textLock)
        {
            if (_textBySourcePage.TryGetValue(sourceIndex, out IReadOnlyList<PdfCharacter>? known))
            {
                return known;
            }
        }

        IReadOnlyList<PdfCharacter> text;
        try
        {
            text = Source.Pdf.GetCharacters(sourceIndex);
        }
        catch (PdfPageException)
        {
            text = [];
        }

        lock (_textLock)
        {
            _textBySourcePage[sourceIndex] = text;
        }

        return text;
    }

    /// <summary>
    /// Which match to make current: after an edit, the one nearest where you were; otherwise the first on or after the
    /// page in view, so searching doesn't jump back to page 1.
    /// </summary>
    private int ChooseHit(List<FindHit> hits, FindHit? before)
    {
        if (hits.Count == 0)
        {
            return -1;
        }

        if (before is not null)
        {
            int same = hits.FindIndex(hit => hit.Page == before.Page && Math.Abs(hit.Match.Bounds.Y - before.Match.Bounds.Y) < 1 && Math.Abs(hit.Match.Bounds.X - before.Match.Bounds.X) < 1);
            if (same >= 0)
            {
                return same;
            }

            int page = State.IndexOf(before.Page);
            int onOrAfter = hits.FindIndex(hit => hit.PageIndex >= page);
            return onOrAfter >= 0 ? onOrAfter : 0;
        }

        int fromHere = hits.FindIndex(hit => hit.PageIndex >= CurrentPageIndex);
        return fromHere >= 0 ? fromHere : 0;
    }

    private void ShowHits(IReadOnlyList<FindHit> hits, int index, IReadOnlyList<int> withoutText)
    {
        FindHits = hits;
        FindIndex = index;
        PagesWithoutText = withoutText;
        foreach (PageViewModel page in Pages)
        {
            page.FindMatchCount = hits.Count(hit => hit.Page == page.Id);
        }

        OnPropertyChanged(nameof(FindStatus));
        ItemsChanged?.Invoke();
    }

    private void ShowCurrentHit()
    {
        if (CurrentFindHit is { } hit)
        {
            ScrollToAreaRequested?.Invoke(hit.PageIndex, hit.Match.Bounds);
        }

        OnPropertyChanged(nameof(FindStatus));
        ItemsChanged?.Invoke();
    }
}
