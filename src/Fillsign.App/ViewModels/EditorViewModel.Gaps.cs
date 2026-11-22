using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fillsign.App.Localization;
using Fillsign.Core.Gaps;
using Fillsign.Core.Model;
using Fillsign.Pdf.Export;

namespace Fillsign.App.ViewModels;

/// <summary>
/// Possible gaps before export (FS-609, FR-8.3, 🎨 6 and E10): boxes and blanks on the pages the person worked on
/// that nothing was placed in. Export lists them; Show me opens the checking mode, which steps through them with the
/// right tool ready and goes back to export when done. Hints, never a verdict.
/// </summary>
public sealed partial class EditorViewModel
{
    // Source pages don't change while a document is open, so each page's boxes and blanks are read once.
    private readonly Dictionary<int, IReadOnlyList<FormSpot>> _spotsBySourcePage = [];
    private readonly Lock _spotsLock = new();

    /// <summary>Raised to scroll a page's area into view: the page index and the area in its display coordinates.</summary>
    public event Action<int, PageRect>? ScrollToAreaRequested;

    /// <summary>The gap being looked at in the checking mode, outlined on the page; null when not checking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckingGaps), nameof(CheckingText), nameof(CheckingBadge))]
    public partial Gap? CheckingGap { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckingText))]
    private partial GapReport CheckingReport { get; set; } = GapReport.None;

    public bool IsCheckingGaps => CheckingGap is not null || _isCheckingDone;

    private bool _isCheckingDone;

    /// <summary>The checking bar's words: which gap, and what to do about it.</summary>
    public string CheckingText
    {
        get
        {
            if (CheckingGap is not { } gap)
            {
                return Strings.Editor_CheckingNoMoreGaps;
            }

            int position = CheckingReport.Gaps.ToList().FindIndex(other => SameGap(other, gap)) + 1;
            return position > 0
                ? Text.Format(Strings.Editor_CheckingGapOfGaps, position, CheckingReport.Gaps.Count, DescribeForChecking(gap))
                : Text.Format(Strings.Editor_CheckingGap, DescribeForChecking(gap));
        }
    }

    /// <summary>The short label beside the outlined gap ("Not ticked", "Empty").</summary>
    public string CheckingBadge => CheckingGap?.Kind == GapKind.Unticked ? Strings.Editor_GapBadgeNotTicked : Strings.Editor_GapBadgeEmpty;

    /// <summary>
    /// Finds the possible gaps now. Reads the text of pages with items on them, so it's run off the UI thread with the
    /// final copy.
    /// </summary>
    public GapReport FindGaps() => FindGaps(State);

    private GapReport FindGaps(DocumentState state) =>
        GapFinder.Find(
            state,
            entry => WithFields(GapFinder.Turn(SpotsOn(entry.SourceIndex), Source.GetPage(entry.SourceIndex), entry.Rotation), entry, state),
            Measurer,
            PagesWithFilledFields(state));

    // Groups for spots made from form fields, kept clear of the small numbers the page text's boxes use.
    private const int RadioGroups = 1_000_000;
    private const int CheckBoxRows = 2_000_000;

    /// <summary>
    /// The page's spots with its own form fields added (FS-950): empty text fields, drop-downs and signature fields
    /// are blanks; radio groups, and check boxes on the same row, are questions to tick. Boxes and blanks read from the
    /// page text that sit on a field are dropped, so nothing is pointed out twice.
    /// </summary>
    private IReadOnlyList<FormSpot> WithFields(IReadOnlyList<FormSpot> fromText, PageEntry entry, DocumentState state)
    {
        if (entry.SourceIndex >= Source.PageCount)
        {
            return fromText;
        }

        var transform = new Core.Geometry.PageTransform(Source.GetPage(entry.SourceIndex), entry.Rotation);
        var fields = Source.FormFields(entry.SourceIndex)
            .Where(field => field is { IsReadOnly: false, Kind: not Pdf.Pdfium.FormFieldKind.PushButton })
            .Select(field => (Field: field, Box: BoxOf(field, transform)))
            .ToList();
        if (fields.Count == 0)
        {
            return fromText;
        }

        string ValueOf(Pdf.Pdfium.FormField field) => state.Fields.TryGetValue(field.Name, out string? value) ? value : field.Value;
        var spots = new List<FormSpot>(fromText.Where(spot => !fields.Any(field => field.Box.Intersects(spot.Area))));
        var radioGroups = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<double>();
        foreach ((Pdf.Pdfium.FormField field, PageRect box) in fields)
        {
            switch (field.Kind)
            {
                case Pdf.Pdfium.FormFieldKind.Text or Pdf.Pdfium.FormFieldKind.Choice when ValueOf(field).Length == 0:
                    spots.Add(new BlankSpot(box, field.DisplayName, BlankKind.Text) { IsField = true });
                    break;
                case Pdf.Pdfium.FormFieldKind.Signature:
                    spots.Add(new BlankSpot(box, field.DisplayName, BlankKind.Signature) { IsField = true });
                    break;
                case Pdf.Pdfium.FormFieldKind.Radio:
                    int group = RadioGroups + radioGroups.GetValueOrDefault(field.Name, radioGroups.Count);
                    radioGroups.TryAdd(field.Name, radioGroups.Count);
                    spots.Add(new CheckBoxSpot(box, field.OnValue, group, ValueOf(field) == field.OnValue) { IsField = true, Question = field.DisplayName });
                    break;
                case Pdf.Pdfium.FormFieldKind.CheckBox:
                    int row = rows.FindIndex(y => Math.Abs(y - box.Centre.Y) <= Math.Max(4, box.Height / 2));
                    if (row < 0)
                    {
                        rows.Add(box.Centre.Y);
                        row = rows.Count - 1;
                    }

                    spots.Add(new CheckBoxSpot(box, field.DisplayName, CheckBoxRows + row, IsTicked(field, state)) { IsField = true });
                    break;
            }
        }

        return spots;
    }

    /// <summary>Pages with a form field filled in: they count as worked on, so their gaps are looked for too.</summary>
    private HashSet<PageId> PagesWithFilledFields(DocumentState state)
    {
        var pages = new HashSet<PageId>();
        if (state.Fields.IsEmpty)
        {
            return pages;
        }

        foreach (PageEntry entry in state.Pages)
        {
            if (entry.SourceIndex < Source.PageCount && Source.FormFields(entry.SourceIndex).Any(field => state.Fields.ContainsKey(field.Name)))
            {
                pages.Add(entry.Id);
            }
        }

        return pages;
    }

    private IReadOnlyList<FormSpot> SpotsOn(int sourceIndex)
    {
        lock (_spotsLock)
        {
            if (_spotsBySourcePage.TryGetValue(sourceIndex, out IReadOnlyList<FormSpot>? known))
            {
                return known;
            }
        }

        IReadOnlyList<FormSpot> spots = Source.IsPageLoaded(sourceIndex) && !Source.DamagedPages.Contains(sourceIndex)
            ? FormSpotReader.Read(Source.Pdf, sourceIndex, Source.GetPage(sourceIndex))
            : [];
        lock (_spotsLock)
        {
            _spotsBySourcePage[sourceIndex] = spots;
        }

        return spots;
    }

    /// <summary>Show me: closes export and steps to this gap with the tool for it ready (E10).</summary>
    public void StartChecking(GapReport report, int gapIndex)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (gapIndex < 0 || gapIndex >= report.Gaps.Count)
        {
            return;
        }

        // The checking bar takes the find bar's place.
        CloseFind();
        _isCheckingDone = false;
        CheckingReport = report;
        ShowGap(report.Gaps[gapIndex]);
    }

    /// <summary>Next gap: looks again, as the last one may have been filled, and moves to the next one still open.</summary>
    [RelayCommand]
    private void NextGap()
    {
        Gap? current = CheckingGap;
        FinishTyping();
        CheckingReport = FindGaps();
        IReadOnlyList<Gap> open = CheckingReport.Gaps;
        Gap? next = current is null
            ? (open.Count > 0 ? open[0] : null)
            : open.FirstOrDefault(gap => Comes(gap, after: current)) ?? open.FirstOrDefault(gap => !SameGap(gap, current));
        if (next is null)
        {
            _isCheckingDone = true;
            CheckingGap = null;
            SwitchTool(EditorTool.Select);
            return;
        }

        ShowGap(next);
    }

    /// <summary>Previous gap (UX review 8): looks again, then moves to the last gap still open before this one.</summary>
    [RelayCommand]
    private void PreviousGap()
    {
        Gap? current = CheckingGap;
        FinishTyping();
        CheckingReport = FindGaps();
        IReadOnlyList<Gap> open = CheckingReport.Gaps;
        Gap? previous = current is null
            ? (open.Count > 0 ? open[^1] : null)
            : open.LastOrDefault(gap => Comes(current, after: gap)) ?? open.LastOrDefault(gap => !SameGap(gap, current));
        if (previous is null)
        {
            _isCheckingDone = true;
            CheckingGap = null;
            SwitchTool(EditorTool.Select);
            return;
        }

        ShowGap(previous);
    }

    /// <summary>Back to export: leaves the checking mode and opens Export final copy again, with the gaps looked at afresh.</summary>
    [RelayCommand]
    private async Task BackToExportAsync()
    {
        StopChecking();
        await ExportCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    [RelayCommand]
    private void StopChecking()
    {
        _isCheckingDone = false;
        CheckingGap = null;
        CheckingReport = GapReport.None;
        OnPropertyChanged(nameof(IsCheckingGaps));
    }

    private void ShowGap(Gap gap)
    {
        // The tool that fills it is ready: Tick for boxes, Date for dates, Text for other blanks. A form's own field is
        // filled by clicking it with Select, so that stays as it is.
        EditorTool tool = gap.IsField ? EditorTool.Select
            : gap.Kind == GapKind.Unticked ? EditorTool.Tick
            : gap.BlankKind == BlankKind.Date ? EditorTool.Date
            : gap.BlankKind is BlankKind.Signature or BlankKind.Initials ? EditorTool.Select
            : EditorTool.Text;
        SwitchTool(tool);
        CheckingGap = gap;
        int index = State.IndexOf(gap.Page);
        if (index >= 0)
        {
            ScrollToAreaRequested?.Invoke(index, gap.Area);
        }

        Changed();
    }

    /// <summary>What the checking bar says about a gap.</summary>
    /// <summary>A form field's gap, worded as a field: "'Email' is empty", "'Membership' isn't answered".</summary>
    private static string DescribeField(Gap gap) => gap.Kind switch
    {
        GapKind.Unticked when gap.Question.Length > 0 => Text.Format(Strings.Editor_GapNotAnswered, gap.Question),
        GapKind.Unticked => gap.Label.Length > 0 ? Text.Format(Strings.Editor_GapNotTicked, gap.Label) : Strings.Editor_GapBoxNotTicked,
        _ when gap.BlankKind == BlankKind.Signature => Text.Format(Strings.Editor_GapNotSigned, gap.Label),
        _ => Text.Format(Strings.Editor_GapEmpty, gap.Label),
    };

    /// <summary><see cref="DescribeField"/> with the page it's on, each a whole sentence so translations can order it.</summary>
    private static string DescribeFieldOnPage(Gap gap) => gap.Kind switch
    {
        GapKind.Unticked when gap.Question.Length > 0 => Text.Format(Strings.Editor_GapNotAnsweredOnPage, gap.Question, gap.PageNumber),
        GapKind.Unticked => gap.Label.Length > 0
            ? Text.Format(Strings.Editor_GapNotTickedOnPage, gap.Label, gap.PageNumber)
            : Text.Format(Strings.Editor_GapBoxNotTickedOnPage, gap.PageNumber),
        _ when gap.BlankKind == BlankKind.Signature => Text.Format(Strings.Editor_GapNotSignedOnPage, gap.Label, gap.PageNumber),
        _ => Text.Format(Strings.Editor_GapEmptyOnPage, gap.Label, gap.PageNumber),
    };

    private static string DescribeForChecking(Gap gap) => gap.IsField
        ? gap switch
        {
            { Kind: GapKind.Unticked } => Text.Format(Strings.Editor_GapClickAnAnswer, DescribeField(gap)),
            { BlankKind: BlankKind.Signature } => Text.Format(Strings.Editor_GapClickToSign, DescribeField(gap)),
            _ => Text.Format(Strings.Editor_GapClickToFill, DescribeField(gap)),
        }
        : gap.Kind switch
    {
        GapKind.Unticked => Strings.Editor_GapCheckBoxNotTicked,
        _ => gap.BlankKind switch
        {
            BlankKind.Date => Strings.Editor_GapCheckDate,
            BlankKind.Signature => Strings.Editor_GapCheckSignature,
            BlankKind.Initials => Strings.Editor_GapCheckInitials,
            _ => gap.Label.Length > 0 ? Text.Format(Strings.Editor_GapCheckNothingAfter, gap.Label) : Strings.Editor_GapCheckEmptyLine,
        },
    };

    /// <summary>How export lists a gap: what it is and where.</summary>
    public static string DescribeForExport(Gap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        if (gap.IsField)
        {
            return DescribeFieldOnPage(gap);
        }

        return gap.Kind switch
        {
            GapKind.Unticked => Text.Format(Strings.Editor_GapUntickedOnPage, gap.PageNumber),
            _ => gap.BlankKind switch
            {
                BlankKind.Date => Text.Format(Strings.Editor_GapDateOnPage, gap.PageNumber),
                BlankKind.Signature => Text.Format(Strings.Editor_GapSignatureOnPage, gap.PageNumber),
                BlankKind.Initials => Text.Format(Strings.Editor_GapInitialsOnPage, gap.PageNumber),
                _ => gap.Label.Length > 0
                    ? Text.Format(Strings.Editor_GapNothingAfterOnPage, gap.Label, gap.PageNumber)
                    : Text.Format(Strings.Editor_GapEmptyLineOnPage, gap.PageNumber),
            },
        };
    }

    private static bool SameGap(Gap a, Gap b) => a.Page == b.Page && a.Kind == b.Kind && Math.Abs(a.Area.Y - b.Area.Y) < 1 && Math.Abs(a.Area.X - b.Area.X) < 1;

    private static bool Comes(Gap gap, Gap after) =>
        gap.PageNumber > after.PageNumber
        || (gap.PageNumber == after.PageNumber && (gap.Area.Y > after.Area.Y + 1 || (Math.Abs(gap.Area.Y - after.Area.Y) <= 1 && gap.Area.X > after.Area.X + 1)));
}
