using System.Collections.Immutable;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fillsign.Core.Editing;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Pdf.Fonts;

namespace Fillsign.App.ViewModels;

/// <summary>
/// Phase 3: tools, items and selection (FR-3, FR-5). Pointer input arrives in page points from the page view; the
/// view draws what this exposes. Drags change the state live and record one undo step when they end.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>The first signature is placed 150 pt wide and the first initials 50 pt wide (E3, FS-405).</summary>
    public const double SignatureWidth = 150;
    public const double InitialsWidth = 50;

    /// <summary>Width the next signature is placed at: the size the last one was resized to, so repeat signing matches.</summary>
    public double DefaultSignatureWidth { get; private set; } = SignatureWidth;

    /// <summary>Width the next initials are placed at, remembered the same way as <see cref="DefaultSignatureWidth"/>.</summary>
    public double DefaultInitialsWidth { get; private set; } = InitialsWidth;

    /// <summary>Keyboard placing cursor steps in points (NFR-4: every drag and click has a keyboard way).</summary>
    public const double CursorStep = 4;
    public const double CursorLargeStep = 20;

    private readonly Clipboard _clipboard = new();
    private Gesture? _gesture;

    // Typing (FR-3.1): the state before the text was added or opened, so finishing records one undo step.
    private DocumentState? _typingBefore;

    // Freehand strokes join into this drawing until the tool, colour or width changes (FS-305).
    private ItemId? _joiningInk;

    public ITextMeasurer Measurer { get; } = NotoTextMeasurer.Instance;

    /// <summary>Raised when items, the selection or a preview changes, so the page view redraws.</summary>
    public event Action? ItemsChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextMode), nameof(ToolHint), nameof(ToolLabel), nameof(IsShapeTool))]
    [NotifyPropertyChangedFor(nameof(OptionsHaveColour), nameof(OptionsHaveSize), nameof(SizeLabel), nameof(SizeTip), nameof(CurrentColour))]
    public partial EditorTool Tool { get; private set; } = EditorTool.Select;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolHint), nameof(ToolLabel), nameof(OptionsHaveColour), nameof(OptionsHaveSize), nameof(SizeLabel), nameof(SizeTip))]
    public partial ShapeKind ShapeKind { get; private set; } = ShapeKind.Rectangle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextMode), nameof(SelectionLabel), nameof(SelectionHint), nameof(OptionsHaveColour), nameof(OptionsHaveSize))]
    [NotifyPropertyChangedFor(nameof(SizeLabel), nameof(SizeTip), nameof(CurrentColour), nameof(SelectedIds))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectionCommand), nameof(DuplicateSelectionCommand))]
    public partial Selection Selection { get; private set; } = Selection.None;

    /// <summary>Colour for new items, and the last colour chosen (FR-5.5). Blue by default (FR-3.9).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentColour))]
    public partial InkColor DefaultColour { get; private set; } = Platform.AppSettings.Load().Ink;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel), nameof(SizeTip))]
    public partial double DefaultFontSize { get; private set; } = TextItem.DefaultFontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel), nameof(SizeTip))]
    public partial double DefaultMarkSize { get; private set; } = MarkItem.DefaultSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel), nameof(SizeTip))]
    public partial double DefaultStrokeWidth { get; private set; } = InkItem.DefaultStrokeWidth;

    /// <summary>The text item being typed into, shown by the view as an in-place text box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextMode), nameof(SizeLabel), nameof(SizeTip), nameof(CurrentColour), nameof(OptionsHaveColour), nameof(OptionsHaveSize))]
    public partial ItemId? TypingItem { get; private set; }

    /// <summary>The signature being placed (E4); follows the pointer until clicked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextMode), nameof(PlacingText))]
    public partial (SignatureImage Image, SignatureKind Kind)? PlacingSignature { get; private set; }

    /// <summary>Where a click would land: the pointer over a page, or the keyboard cursor (arrow keys).</summary>
    public (PageId Page, PagePoint Point)? Cursor { get; private set; }

    /// <summary>True when <see cref="Cursor"/> was moved with the keyboard, so the view draws a crosshair.</summary>
    public bool IsKeyboardCursor { get; private set; }

    /// <summary>A shape or stroke being drawn, before it's added.</summary>
    public Item? Preview { get; private set; }

    /// <summary>The selection box being dragged, and the items it touches so far (E5).</summary>
    public (PageId Page, PageRect Box)? SelectionBox { get; private set; }

    public ImmutableHashSet<ItemId> BoxCandidates { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextMode), nameof(BoxSelectingText))]
    public partial int BoxCandidateCount { get; private set; }

    /// <summary>
    /// "2 items deleted" (FR-7.11, FS-707): shown in a toast with Undo, which stays at least 10 seconds and while
    /// it has the pointer or focus; cleared by the next action.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    public partial string? Notice { get; private set; }

    /// <summary>The toast offers Undo: after a change (a deletion), not after a note that just tells you something.</summary>
    public bool NoticeOffersUndo { get; private set; }

    private bool _isTelling;

    /// <summary>A toast that only informs, with no Undo (a read-only field, a form's fields on opening).</summary>
    private void Tell(string message)
    {
        _isTelling = true;
        Notice = message;
        _isTelling = false;
    }

    partial void OnNoticeChanged(string? value)
    {
        NoticeOffersUndo = value is not null && !_isTelling;
        OnPropertyChanged(nameof(NoticeOffersUndo));
    }

    public bool HasToast => Notice is not null;

    /// <summary>Closes the toast (its close button, or after its time is up).</summary>
    [RelayCommand]
    private void DismissNotice() => Notice = null;

    public ImmutableHashSet<ItemId> SelectedIds => Selection is ItemSelection items ? items.Items : [];

    public bool IsShapeTool => Tool == EditorTool.Shape;

    public bool IsDrawingGesture => _gesture is not null;

    public ContextMode ContextMode =>
        PlacingSignature is not null ? ContextMode.Placing
        : TypingItem is not null ? ContextMode.Typing
        : BoxCandidateCount > 0 || SelectionBox is not null ? ContextMode.BoxSelecting
        : Tool == EditorTool.Draw ? ContextMode.Drawing
        : !Selection.IsEmpty ? ContextMode.Selection
        : Tool == EditorTool.Select ? ContextMode.Hint
        : ContextMode.ToolOptions;

    public string ToolLabel => Tool switch
    {
        EditorTool.Shape => ShapeName(ShapeKind),
        EditorTool.Draw => "Pen",
        _ => Tool.ToString(),
    };

    /// <summary>What the current tool does and how to stop (FR-3.10).</summary>
    public string ToolHint => Tool switch
    {
        EditorTool.Text => "Click where the text should start, then type",
        EditorTool.Tick => "Click each box to tick it · Tick stays on until you choose another tool · Esc to stop",
        EditorTool.Cross => "Click each box to cross it · Cross stays on until you choose another tool · Esc to stop",
        EditorTool.Date => $"Click to add today’s date ({Platform.DateFormats.Label(Platform.DateFormats.Current)}) · Esc to cancel",
        EditorTool.Shape => "Drag on the page to draw it. Click instead for a standard size · Esc to stop",
        EditorTool.Draw => "Draw on the page · strokes join into one drawing · Done or Esc to finish",
        // FS-950: on a form with fields, filling them is the main way in; the tools add your own on top.
        _ when HasFillableFields && !IsViewOnly => FormHint(),
        _ => "Choose a tool, then click on the page: Tick for boxes, Text for lines, Sign for your signature.",
    };

    public string PlacingText => PlacingSignature?.Kind == SignatureKind.Initials ? "Click where your initials should go." : "Click where your signature should go.";

    public string BoxSelectingText => BoxCandidateCount == 1 ? "Selecting: 1 item in the box" : $"Selecting: {BoxCandidateCount} items in the box";

    public string SelectionLabel => DescribeSelection([.. SelectedItems()]);

    public string SelectionHint
    {
        get
        {
            var items = SelectedItems().ToList();
            return items switch
            {
                [TextItem] => "Double-click to edit · drag to move · drag a corner to resize · arrow keys nudge",
                [SignatureItem or InkItem or PictureItem] => "Drag to move · drag a corner to resize (keeps its shape) · arrow keys nudge",
                [_] => "Drag to move · drag a corner to resize · arrow keys nudge",
                _ => "Drag to move them together · Shift-click to add or remove · arrow keys nudge",
            };
        }
    }

    /// <summary>Whether the options show colour swatches (not for signatures, highlight or whiteout).</summary>
    public bool OptionsHaveColour => TypingItem is not null || (Selection.IsEmpty
        ? Tool is EditorTool.Text or EditorTool.Tick or EditorTool.Cross or EditorTool.Date or EditorTool.Draw
            || (Tool == EditorTool.Shape && !IsFilledShape(ShapeKind))
        : SelectedItems().Any(HasColour));

    public bool OptionsHaveSize => TypingItem is not null || (Selection.IsEmpty
        ? Tool is not (EditorTool.Select or EditorTool.Sign) && !(Tool == EditorTool.Shape && IsFilledShape(ShapeKind))
        : SelectedItems().Any(item => item is not ShapeItem { IsFilled: true }));

    /// <summary>The colour the swatches show as chosen.</summary>
    public InkColor CurrentColour => (TypingItem is { } typing ? State.FindItem(typing) : SelectedItems().FirstOrDefault(HasColour)) switch
    {
        TextItem text => text.Color,
        MarkItem mark => mark.Color,
        ShapeItem shape => shape.Color,
        InkItem ink => ink.Color,
        _ => DefaultColour,
    };

    /// <summary>Sizes are words; the exact value is in <see cref="SizeTip"/> (INTERACTION-PATTERNS §3).</summary>
    public string SizeLabel => DescribeSize().Label;

    public string SizeTip => DescribeSize().Tip;

    // ----- Tools -----

    [RelayCommand]
    private async Task SelectToolAsync(EditorTool tool)
    {
        if (tool == EditorTool.Sign)
        {
            await StartSigningAsync().ConfigureAwait(true);
            return;
        }

        SwitchTool(tool);
    }

    /// <summary>Sign ▾ → Initials (UX review 7): as Sign, but the dialog makes initials.</summary>
    [RelayCommand]
    private Task AddInitialsAsync() => StartSigningAsync(SignatureKind.Initials);

    [RelayCommand]
    private void ChooseShape(ShapeKind kind)
    {
        ShapeKind = kind;
        SwitchTool(EditorTool.Shape);
    }

    /// <summary>Starts placing a signature or initials (E4). Phase 4's dialog supplies the image.</summary>
    public void BeginPlacingSignature(SignatureImage image, SignatureKind kind)
    {
        ArgumentNullException.ThrowIfNull(image);
        SwitchTool(EditorTool.Sign);
        PlacingSignature = (image, kind);
        Changed();
    }

    private async Task StartSigningAsync(SignatureKind kind = SignatureKind.Signature)
    {
        if (IsViewOnly)
        {
            return;
        }

        if (await _host.ChooseSignatureAsync(kind).ConfigureAwait(true) is { } chosen)
        {
            BeginPlacingSignature(chosen.Image, chosen.Kind);
        }
    }

    private void SwitchTool(EditorTool tool)
    {
        // View only (FS-214): only Select, which can't change anything here.
        if (IsViewOnly)
        {
            tool = EditorTool.Select;
        }

        FinishTyping();
        CancelGesture();
        _joiningInk = null;
        PlacingSignature = null;
        Notice = null;
        IsNudgingTools = false;
        Cursor = null;
        if (tool != EditorTool.Select)
        {
            // Drawing tools pass over existing items, so a leftover selection would only get in the way (FR-3.8).
            Selection = Selection.None;
        }

        Tool = tool;
        Changed();
    }

    /// <summary>
    /// Esc: back out one level (FR-7.7). Cancels a drag, finishes typing, cancels placing, finishes drawing,
    /// clears the selection, returns to the Select tool, then stops checking for gaps. Returns false when there's
    /// nothing to back out of.
    /// </summary>
    public bool BackOut()
    {
        if (EditingField is not null)
        {
            CancelFieldEdit();
        }
        else if (FocusedField is not null)
        {
            LeaveFields();
        }
        else if (_gesture is not null)
        {
            CancelGesture();
        }
        else if (TypingItem is not null)
        {
            FinishTyping();
        }
        else if (PlacingSignature is not null)
        {
            SwitchTool(EditorTool.Select);
        }
        else if (Tool == EditorTool.Draw)
        {
            FinishDrawing();
        }
        else if (!Selection.IsEmpty)
        {
            Selection = Selection.None;
        }
        else if (Tool != EditorTool.Select)
        {
            SwitchTool(EditorTool.Select);
        }
        else if (IsCheckingGaps)
        {
            StopChecking();
        }
        else
        {
            return false;
        }

        Changed();
        return true;
    }

    /// <summary>The pen's Done: the drawing so far is selected, ready to move or resize (E6).</summary>
    [RelayCommand]
    private void FinishDrawing()
    {
        ItemId? drawing = _joiningInk;
        SwitchTool(EditorTool.Select);
        if (drawing is { } id && State.FindItem(id) is not null)
        {
            Selection = new ItemSelection([id]);
        }

        Changed();
    }

    [RelayCommand]
    private void CancelPlacing() => SwitchTool(EditorTool.Select);

    // ----- Pointer input, in page points -----

    /// <summary>A press on page <paramref name="pageIndex"/>. <paramref name="tolerance"/> is the grab distance in points.</summary>
    public void PointerPressed(int pageIndex, PagePoint point, bool isShift, bool isCommand, int clickCount, double tolerance)
    {
        Notice = null;
        IsNudgingTools = false;
        IsKeyboardCursor = false;
        if (pageIndex < 0 || pageIndex >= State.Pages.Length)
        {
            return;
        }

        PageId page = State.Pages[pageIndex].Id;
        if (EditingField is not null)
        {
            // A click outside the field's text box finishes it; the view passes what was typed first.
            return;
        }

        // FS-950: a click on one of the PDF's own fields fills it, unless something of Fillsign's is on top.
        if (TypingItem is null && ItemHitTest.TopmostAt(State, page, point, tolerance, Measurer) is null && PressField(page, point))
        {
            return;
        }

        LeaveFields();

        if (TypingItem is { } typing)
        {
            // A click outside finishes typing (E2); a click inside belongs to the text box.
            FinishTyping();
            if (Tool != EditorTool.Text || State.FindItem(typing) is not null)
            {
                Changed();
                return;
            }
        }

        switch (Tool)
        {
            case EditorTool.Select:
                PressWithSelect(page, point, isShift || isCommand, clickCount, tolerance);
                break;
            case EditorTool.Text:
                if (ItemHitTest.TopmostAt(State, page, point, tolerance, Measurer) is TextItem existing)
                {
                    BeginTyping(existing, State);
                }
                else
                {
                    AddText(page, point, string.Empty, startTyping: true);
                }

                break;
            case EditorTool.Tick or EditorTool.Cross:
                PlaceMark(page, point, Tool == EditorTool.Tick ? MarkKind.Tick : MarkKind.Cross);
                break;
            case EditorTool.Date:
            {
                TextItem date = AddText(page, point, Platform.DateFormats.Format(DateTime.Now), startTyping: false);

                // A form needs one date, so go back to Select with the date selected, ready to nudge into place (FR-3.3).
                SwitchTool(EditorTool.Select);
                Selection = new ItemSelection([date.Id]);
                break;
            }
            case EditorTool.Sign:
                PlaceSignature(page, point);
                break;
            case EditorTool.Shape:
                _gesture = new DrawingShape(page, point);
                Preview = ItemGeometry.MakeShape(ItemId.New(), page, ShapeKind, point, point, DefaultColour, DefaultStrokeWidth);
                break;
            case EditorTool.Draw:
                if (_joiningInk is { } ink && State.FindItem(ink)?.Page != page)
                {
                    _joiningInk = null;
                }

                _gesture = new DrawingInk(page, [point]);
                Preview = ItemGeometry.MakeInk(ItemId.New(), page, [[point]], DefaultColour, DefaultStrokeWidth);
                break;
        }

        Changed();
    }

    /// <summary>
    /// A drag on a resize handle of the one selected item (FR-5.4). <paramref name="grabbedAt"/> is where the handle
    /// was pressed; the corner keeps the same distance from the pointer, so it doesn't jump.
    /// </summary>
    public void BeginResize(Corner corner, PagePoint grabbedAt)
    {
        if (SelectedItems().ToList() is not [var item])
        {
            return;
        }

        PageRect box = ItemGeometry.Bounds(item, Measurer);
        var cornerPoint = new PagePoint(
            corner is Corner.TopLeft or Corner.BottomLeft ? box.X : box.Right,
            corner is Corner.TopLeft or Corner.TopRight ? box.Y : box.Bottom);
        Notice = null;
        _gesture = new Resizing(item, corner, cornerPoint - grabbedAt, State);
    }

    /// <summary>Pointer movement. During a drag, <paramref name="pageIndex"/> is the page the drag started on.</summary>
    public void PointerMoved(int pageIndex, PagePoint point)
    {
        if (pageIndex >= 0 && pageIndex < State.Pages.Length)
        {
            Cursor = (State.Pages[pageIndex].Id, point);
            IsKeyboardCursor = false;
        }

        // Just moving over the page changes only what follows the pointer: no need to redraw the pages or refresh
        // the bars, which on a slow machine falls behind the mouse and leaves a trail.
        if (_gesture is null)
        {
            HoverChanged?.Invoke();
            return;
        }

        switch (_gesture)
        {
            case MovingItems moving:
            {
                double dx = point.X - moving.Start.X, dy = point.Y - moving.Start.Y;
                if (!moving.HasMoved && Math.Abs(dx) < ItemGeometry.ClickTolerance && Math.Abs(dy) < ItemGeometry.ClickTolerance)
                {
                    break;
                }

                _gesture = moving with { HasMoved = true };
                ApplyLive(ItemEdits.MoveGroup(moving.Before, SelectedIds, dx, dy, PageSizeOf, Measurer));
                break;
            }

            case BoxSelecting box:
            {
                PageRect rect = PageRect.FromCorners(box.Start, point);
                SelectionBox = (box.Page, rect);
                BoxCandidates = [.. State.ItemsOn(box.Page).Where(item => ItemGeometry.Bounds(item, Measurer).Intersects(rect)).Select(item => item.Id)];
                BoxCandidateCount = BoxCandidates.Count;
                OnPropertyChanged(nameof(ContextMode));
                break;
            }

            case Resizing resizing:
            {
                Item resized = ItemEdits.ResizeFromCorner(resizing.Original, resizing.Corner, point + resizing.GrabOffset, Measurer);
                PageRect bounds = ItemGeometry.Bounds(resized, Measurer);
                resized = ItemGeometry.MoveTo(resized, ItemGeometry.ClampToPage(bounds, PageSizeOf(resized.Page)));
                ApplyLive(ItemEdits.Replace(resizing.Before, resized));
                break;
            }

            case DrawingShape shape:
                Preview = ItemGeometry.MakeShape(ItemId.New(), shape.Page, ShapeKind, shape.Start, ClampToPage(shape.Page, point), DefaultColour, DefaultStrokeWidth);
                break;

            case DrawingInk ink:
            {
                PagePoint clamped = ClampToPage(ink.Page, point);
                PagePoint last = ink.Points[^1];
                if (Math.Abs(clamped.X - last.X) + Math.Abs(clamped.Y - last.Y) >= 0.5)
                {
                    ink.Points.Add(clamped);
                    Preview = ItemGeometry.MakeInk(ItemId.New(), ink.Page, [ink.Points], DefaultColour, DefaultStrokeWidth);
                }

                break;
            }
        }

        Changed();
    }

    public void PointerExited()
    {
        if (!IsKeyboardCursor)
        {
            Cursor = null;
            HoverChanged?.Invoke();
        }
    }

    public void PointerReleased(PagePoint point)
    {
        Gesture? gesture = _gesture;
        _gesture = null;
        switch (gesture)
        {
            case MovingItems { HasMoved: true } moving:
                Record(moving.Before);
                break;

            case MovingItems { WasSelected: true, IsAdditive: false } click:
                // A plain click on one of several selected items selects just that one.
                Selection = new ItemSelection([click.Item]);
                break;

            case BoxSelecting box:
                if (SelectionBox is { } rect && (rect.Box.Width >= ItemGeometry.ClickTolerance || rect.Box.Height >= ItemGeometry.ClickTolerance))
                {
                    Selection = Selections.SelectInBox(box.SelectionBefore, State, box.Page, rect.Box, box.IsAdditive, Measurer);
                }
                else if (!box.IsAdditive && box.SelectionBefore.IsEmpty)
                {
                    NudgeTowardsTools();
                }

                SelectionBox = null;
                BoxCandidates = [];
                BoxCandidateCount = 0;
                break;

            case Resizing resizing:
                Record(resizing.Before);
                // The next item of the same kind is placed at the size just chosen, as with the size buttons.
                if (State.FindItem(resizing.Original.Id) is { } resizedItem)
                {
                    RememberSizes([resizedItem]);
                }

                break;

            case DrawingShape shape:
            {
                Preview = null;
                ShapeItem item = ItemGeometry.MakeShape(ItemId.New(), shape.Page, ShapeKind, shape.Start, ClampToPage(shape.Page, point), DefaultColour, DefaultStrokeWidth);
                Change(ItemEdits.Add(State, item));

                // After drawing, the new shape is selected, ready to move or resize (FR-3.5).
                SwitchTool(EditorTool.Select);
                Selection = new ItemSelection([item.Id]);
                break;
            }

            case DrawingInk ink:
                Preview = null;
                AddStroke(ink.Page, ink.Points);
                break;
        }

        Changed();
    }

    // ----- Keyboard -----

    /// <summary>Arrow keys: nudge the selection (FR-5.6), or move the placing cursor for a placing tool.</summary>
    public bool Arrow(int dx, int dy, bool isLarge)
    {
        if (TypingItem is not null || _gesture is not null)
        {
            return false;
        }

        if (!Selection.IsEmpty)
        {
            double step = isLarge ? ItemEdits.LargeNudge : ItemEdits.SmallNudge;
            Change(ItemEdits.MoveGroup(State, SelectedIds, dx * step, dy * step, PageSizeOf, Measurer));
            return true;
        }

        if (Tool is EditorTool.Select or EditorTool.Draw || Pages.Count == 0)
        {
            return false;
        }

        PageViewModel current = Pages[CurrentPageIndex];
        (PageId page, PagePoint point) = Cursor is { } cursor && State.FindPage(cursor.Page) is not null
            ? cursor
            : (current.Id, new PagePoint(current.DisplaySize.Width / 2, current.DisplaySize.Height / 2));
        double move = isLarge ? CursorLargeStep : CursorStep;
        Cursor = (page, ClampToPage(page, new PagePoint(point.X + (dx * move), point.Y + (dy * move))));
        IsKeyboardCursor = true;
        Changed();
        return true;
    }

    /// <summary>Enter: place at the keyboard cursor, or edit the selected text.</summary>
    public bool Enter()
    {
        if (TypingItem is not null)
        {
            return false;
        }

        if (IsKeyboardCursor && Cursor is { } cursor)
        {
            int index = State.IndexOf(cursor.Page);
            PointerPressed(index, cursor.Point, isShift: false, isCommand: false, clickCount: 1, tolerance: 0);
            PointerReleased(cursor.Point);
            IsKeyboardCursor = Tool is EditorTool.Tick or EditorTool.Cross or EditorTool.Date;
            Changed();
            return true;
        }

        if (SelectedItems().ToList() is [TextItem text])
        {
            BeginTyping(text, State);
            Changed();
            return true;
        }

        return false;
    }

    // ----- Typing (FR-3.1) -----

    /// <summary>The text box changed: update the item live; finishing records the undo step.</summary>
    public void UpdateTyping(string text)
    {
        string typed = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (typed.Length > TextItem.MaxLength)
        {
            typed = typed[..TextItem.MaxLength];
        }

        if (TypingItem is { } id && State.FindItem(id) is TextItem item && item.Text != typed)
        {
            ApplyLive(ItemEdits.Replace(State, item with { Text = typed }));
        }
    }

    /// <summary>Finishes typing. Empty text is discarded (FR-3.1).</summary>
    public void FinishTyping()
    {
        if (TypingItem is not { } id || _typingBefore is not { } before)
        {
            return;
        }

        TypingItem = null;
        _typingBefore = null;
        if (State.FindItem(id) is TextItem { Text: var text } && string.IsNullOrWhiteSpace(text))
        {
            ApplyLive(ItemEdits.Remove(State, new HashSet<ItemId> { id }));
        }

        Record(before);
        Changed();
    }

    private void BeginTyping(TextItem item, DocumentState before)
    {
        Selection = Selection.None;
        _typingBefore = before;
        TypingItem = item.Id;
    }

    // ----- Selection actions -----

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelection()
    {
        var items = SelectedItems().ToList();
        if (items.Count == 0)
        {
            return;
        }

        Change(ItemEdits.Remove(State, SelectedIds));
        Selection = Selection.None;
        Notice = items.Count == 1 ? $"{ItemName(items[0])} deleted" : $"{items.Count} items deleted";
        Changed();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateSelection()
    {
        _clipboard.CopyItems(State, SelectedIds);
        Paste(SelectedItems().First().Page);
    }

    private bool HasSelection() => !Selection.IsEmpty;

    public void Copy()
    {
        if (!Selection.IsEmpty)
        {
            _clipboard.CopyItems(State, SelectedIds);
        }
    }

    public void Cut()
    {
        if (!Selection.IsEmpty)
        {
            Copy();
            DeleteSelection();
        }
    }

    /// <summary>Pastes onto the page in view (FR-7.2).</summary>
    public void Paste() => Paste(Pages.Count > 0 ? Pages[CurrentPageIndex].Id : default);

    public void SelectAllOnCurrentPage()
    {
        if (Pages.Count == 0)
        {
            return;
        }

        FinishTyping();
        SwitchTool(EditorTool.Select);
        Selection = Selections.SelectAllOnPage(State, Pages[CurrentPageIndex].Id);
        Changed();
    }

    /// <summary>A swatch: colours the selection or the text being typed, and becomes the default (FR-5.5).</summary>
    [RelayCommand]
    private void SetColour(InkColor colour)
    {
        if (DefaultColour != colour)
        {
            // Remembered for next time and for other windows' new documents.
            Platform.AppSettings.Update(settings => settings with { InkColour = colour.ToHex() });
        }

        DefaultColour = colour;
        _joiningInk = null;
        if (TypingItem is { } typing)
        {
            ApplyLive(ItemEdits.SetColor(State, new HashSet<ItemId> { typing }, colour));
        }
        else if (!Selection.IsEmpty)
        {
            Change(ItemEdits.SetColor(State, SelectedIds, colour));
        }

        OnPropertyChanged(nameof(CurrentColour));
        Changed();
    }

    [RelayCommand]
    private void Smaller() => StepSize(-1);

    [RelayCommand]
    private void Larger() => StepSize(+1);

    private void StepSize(int direction)
    {
        _joiningInk = null;
        if (TypingItem is { } typing)
        {
            ApplyLive(ItemEdits.BumpSize(State, new HashSet<ItemId> { typing }, direction));
            RememberSizes([State.FindItem(typing)!]);
        }
        else if (!Selection.IsEmpty)
        {
            Change(ItemEdits.BumpSize(State, SelectedIds, direction));
            RememberSizes(SelectedItems());
        }
        else
        {
            // No selection: change the size new items get.
            switch (Tool)
            {
                case EditorTool.Text or EditorTool.Date:
                    DefaultFontSize = Math.Clamp(DefaultFontSize + direction, TextItem.MinFontSize, TextItem.MaxFontSize);
                    break;
                case EditorTool.Tick or EditorTool.Cross:
                    DefaultMarkSize = Math.Clamp(DefaultMarkSize + (direction * 2), MarkItem.MinSize, MarkItem.MaxSize);
                    break;
                case EditorTool.Shape or EditorTool.Draw:
                {
                    var sample = new InkItem(ItemId.New(), default, 0, 0, 1, 1, [], DefaultColour, DefaultStrokeWidth);
                    DefaultStrokeWidth = ((InkItem)ItemEdits.BumpSize(sample, direction)).StrokeWidth;
                    break;
                }
            }
        }

        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(SizeTip));
        Changed();
    }

    // ----- Helpers -----

    public IEnumerable<Item> SelectedItems()
    {
        ImmutableHashSet<ItemId> ids = SelectedIds;
        return ids.IsEmpty ? [] : State.Items.Where(item => ids.Contains(item.Id));
    }

    public PageSize PageSizeOf(PageId page) => State.FindPage(page) is { } entry ? DisplaySizeOf(entry) : default;

    private void PressWithSelect(PageId page, PagePoint point, bool isAdditive, int clickCount, double tolerance)
    {
        Item? hit = ItemHitTest.TopmostAt(State, page, point, tolerance, Measurer);
        if (hit is TextItem text && clickCount >= 2)
        {
            BeginTyping(text, State);
            return;
        }

        if (hit is null)
        {
            Selection before = Selection;
            if (!isAdditive)
            {
                Selection = Selection.None;
            }

            _gesture = new BoxSelecting(page, point, isAdditive, before);
            return;
        }

        bool wasSelected = SelectedIds.Contains(hit.Id);
        if (isAdditive)
        {
            Selection = Selections.SelectItem(Selection, hit.Id, SelectMode.Toggle);
            if (wasSelected)
            {
                return; // Removed from the selection: nothing to drag.
            }
        }
        else if (!wasSelected)
        {
            Selection = new ItemSelection([hit.Id]);
        }

        _gesture = new MovingItems(hit.Id, point, State, wasSelected, isAdditive, HasMoved: false);
    }

    private TextItem AddText(PageId page, PagePoint point, string text, bool startTyping)
    {
        // The click marks the middle of the first line, where the caret appears.
        double lineHeight = DefaultFontSize * TextItem.LineHeight;
        var item = new TextItem(ItemId.New(), page, point.X, point.Y - (lineHeight / 2), text, DefaultFontSize, DefaultColour, DefaultBold);
        item = (TextItem)ItemGeometry.MoveTo(item, ItemGeometry.ClampToPage(ItemGeometry.Bounds(item, Measurer), PageSizeOf(page)));
        DocumentState before = State;
        if (startTyping)
        {
            ApplyLive(ItemEdits.Add(State, item));
            BeginTyping(item, before);
        }
        else
        {
            Change(ItemEdits.Add(State, item));
        }

        return item;
    }

    private void PlaceMark(PageId page, PagePoint point, MarkKind kind) => Change(ItemEdits.Add(State, MarkAt(page, point, kind)));

    /// <summary>
    /// The tick or cross a click at <paramref name="point"/> would place: centred where the click lands, so a click in
    /// a checkbox fills it (FR-3.2), and kept on the page.
    /// </summary>
    private MarkItem MarkAt(PageId page, PagePoint point, MarkKind kind)
    {
        double size = DefaultMarkSize;
        var box = new PageRect(point.X - (size / 2), point.Y - (size / 2), size, size);
        PagePoint position = ItemGeometry.ClampToPage(box, PageSizeOf(page));
        return new MarkItem(ItemId.New(), page, position.X, position.Y, kind, size, DefaultColour);
    }

    /// <summary>
    /// With Tick or Cross on, the mark a click would place, drawn faintly under the pointer (or the keyboard cursor) so
    /// you can see exactly where it will land.
    /// </summary>
    public MarkItem? MarkPreview =>
        Tool is EditorTool.Tick or EditorTool.Cross && Cursor is { } cursor && _gesture is null && TypingItem is null
            && !IsOverBoxField(cursor.Page, cursor.Point)
            ? MarkAt(cursor.Page, cursor.Point, Tool == EditorTool.Tick ? MarkKind.Tick : MarkKind.Cross)
            : null;

    private void PlaceSignature(PageId page, PagePoint point)
    {
        if (PlacingSignature is not { } placing)
        {
            return;
        }

        var item = SignatureAt(page, point, placing.Image, placing.Kind);
        Change(ItemEdits.Add(State, item));
        SwitchTool(EditorTool.Select);
        Selection = new ItemSelection([item.Id]);
    }

    /// <summary>A signature centred on <paramref name="centre"/> at the remembered width for its kind, kept on the page.</summary>
    public SignatureItem SignatureAt(PageId page, PagePoint centre, SignatureImage image, SignatureKind kind)
    {
        ArgumentNullException.ThrowIfNull(image);
        double width = kind == SignatureKind.Initials ? DefaultInitialsWidth : DefaultSignatureWidth;
        double height = width * image.PixelHeight / Math.Max(1, image.PixelWidth);
        var box = new PageRect(centre.X - (width / 2), centre.Y - (height / 2), width, height);
        PagePoint position = ItemGeometry.ClampToPage(box, PageSizeOf(page));
        return new SignatureItem(ItemId.New(), page, position.X, position.Y, kind, image, width, height);
    }

    private void AddStroke(PageId page, IReadOnlyList<PagePoint> points)
    {
        if (_joiningInk is { } id && State.FindItem(id) is InkItem ink && ink.Page == page)
        {
            Change(ItemEdits.Replace(State, ItemGeometry.AppendStrokes(ink, [points])));
            return;
        }

        InkItem drawing = ItemGeometry.MakeInk(ItemId.New(), page, [points], DefaultColour, DefaultStrokeWidth);
        Change(ItemEdits.Add(State, drawing));
        _joiningInk = drawing.Id;
    }

    private void Paste(PageId target)
    {
        if (target == default)
        {
            return;
        }

        DocumentState next = _clipboard.PasteItems(State, target, PageSizeOf(target), Measurer, out ImmutableArray<ItemId> pasted);
        if (pasted.IsEmpty)
        {
            return;
        }

        SwitchTool(EditorTool.Select);
        Change(next);
        Selection = new ItemSelection([.. pasted]);
        Changed();
    }

    private void CancelGesture()
    {
        switch (_gesture)
        {
            case MovingItems moving:
                ApplyLive(moving.Before);
                break;
            case Resizing resizing:
                ApplyLive(resizing.Before);
                break;
            case BoxSelecting box:
                Selection = box.SelectionBefore;
                break;
        }

        _gesture = null;
        Preview = null;
        SelectionBox = null;
        BoxCandidates = [];
        BoxCandidateCount = 0;
    }

    private void RememberSizes(IEnumerable<Item> items)
    {
        foreach (Item item in items)
        {
            switch (item)
            {
                case TextItem text:
                    DefaultFontSize = text.FontSize;
                    break;
                case MarkItem mark:
                    DefaultMarkSize = mark.Size;
                    break;
                case ShapeItem { IsFilled: false } shape:
                    DefaultStrokeWidth = shape.StrokeWidth;
                    break;
                case InkItem ink:
                    DefaultStrokeWidth = ink.StrokeWidth;
                    break;
                case SignatureItem { Kind: SignatureKind.Initials } initials:
                    DefaultInitialsWidth = initials.Width;
                    break;
                case SignatureItem signature:
                    DefaultSignatureWidth = signature.Width;
                    break;
            }
        }
    }

    private PagePoint ClampToPage(PageId page, PagePoint point)
    {
        PageSize size = PageSizeOf(page);
        return new PagePoint(Math.Clamp(point.X, 0, size.Width), Math.Clamp(point.Y, 0, size.Height));
    }

    /// <summary>Records one undo step from <paramref name="before"/> to the current state.</summary>
    private void Record(DocumentState before)
    {
        _history.Record(before, State);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(DocumentStatus));
    }

    /// <summary>Shows a state without recording it (during a drag or while typing).</summary>
    private void ApplyLive(DocumentState next)
    {
        if (!IsViewOnly)
        {
            ApplyState(next);
        }
    }

    /// <summary>
    /// Tab on the page (FS-716): selects the next item in reading order (pages in order, then top to bottom and left
    /// to right) and brings it into view. Returns false past the last item, or before the first going backwards, so
    /// focus can move on.
    /// </summary>
    public bool SelectNextItem(bool backwards)
    {
        // Items whose tops are within a few points share a row, so a tick and its label read left to right.
        const double RowTolerance = 6;
        Item[] ordered =
        [
            .. State.Pages.SelectMany(page => State.ItemsOn(page.Id)
                .Select(item => (Item: item, Box: ItemGeometry.Bounds(item, Measurer)))
                .OrderBy(entry => Math.Round(entry.Box.Y / RowTolerance))
                .ThenBy(entry => entry.Box.X)
                .Select(entry => entry.Item)),
        ];
        if (ordered.Length == 0)
        {
            return false;
        }

        int current = SelectedItems().ToList() is [var only] ? Array.FindIndex(ordered, item => item.Id == only.Id) : -1;
        int next = current < 0 ? (backwards ? ordered.Length - 1 : 0) : current + (backwards ? -1 : 1);
        if (next < 0 || next >= ordered.Length)
        {
            return false;
        }

        Item chosen = ordered[next];
        SwitchTool(EditorTool.Select);
        Selection = new ItemSelection([chosen.Id]);
        int pageIndex = State.IndexOf(chosen.Page);
        if (pageIndex >= 0)
        {
            ScrollToAreaRequested?.Invoke(pageIndex, ItemGeometry.Bounds(chosen, Measurer));
        }

        Changed();
        return true;
    }

    /// <summary>Raised when only what follows the pointer changed (the view repaints just that).</summary>
    public event Action? HoverChanged;

    private void Changed()
    {
        OnPropertyChanged(nameof(ContextMode));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(SizeTip));
        OnPropertyChanged(nameof(CurrentColour));
        OnPropertyChanged(nameof(OptionsHaveColour));
        OnPropertyChanged(nameof(OptionsHaveSize));
        OnPropertyChanged(nameof(FieldAnnouncement));
        OnPropertyChanged(nameof(ContextHint));
        OnTextStyleChanged();
        ItemsChanged?.Invoke();
    }

    private (string Label, string Tip) DescribeSize()
    {
        Item? item = TypingItem is { } typing ? State.FindItem(typing) : SelectedItems().FirstOrDefault(i => i is not ShapeItem { IsFilled: true });
        return item switch
        {
            TextItem text => FontSizeWords(text.FontSize),
            MarkItem mark => MarkSizeWords(mark.Size),
            SignatureItem signature => SignatureSizeWords(signature.Width),
            ShapeItem shape => StrokeWords(shape.StrokeWidth),
            InkItem ink => StrokeWords(ink.StrokeWidth),
            null when Selection.IsEmpty => Tool switch
            {
                EditorTool.Text or EditorTool.Date => FontSizeWords(DefaultFontSize),
                EditorTool.Tick or EditorTool.Cross => MarkSizeWords(DefaultMarkSize),
                EditorTool.Shape or EditorTool.Draw => StrokeWords(DefaultStrokeWidth),
                _ => (string.Empty, string.Empty),
            },
            _ => (string.Empty, string.Empty),
        };
    }

    private static (string, string) FontSizeWords(double size) =>
        (size <= 10 ? "Small" : size <= 14 ? "Medium" : "Large", $"{Points(size)} pt");

    private static (string, string) MarkSizeWords(double size) =>
        (size <= 11 ? "Small" : size <= 17 ? "Medium" : "Large", $"{Points(size)} pt");

    private static (string, string) SignatureSizeWords(double width) =>
        (width < 100 ? "Small" : width < 140 ? "Medium" : "Large", $"{Points(width)} pt wide");

    private static (string, string) StrokeWords(double width) =>
        (width <= 2 ? "Thin" : width <= 4 ? "Medium" : "Thick", $"{Points(width)} pt line");

    private static string Points(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.CurrentCulture);

    private static bool HasColour(Item item) => item is TextItem or MarkItem or InkItem or ShapeItem { IsFilled: false };

    private static bool IsFilledShape(ShapeKind kind) => kind is ShapeKind.Highlight or ShapeKind.Whiteout;

    public static string ShapeName(ShapeKind kind) => kind switch
    {
        ShapeKind.Rectangle => "Rectangle",
        ShapeKind.Ellipse => "Ellipse",
        ShapeKind.Line => "Line",
        ShapeKind.Arrow => "Arrow",
        ShapeKind.Highlight => "Highlight",
        ShapeKind.Whiteout => "Whiteout",
        _ => kind.ToString(),
    };

    private static string ItemName(Item item) => item switch
    {
        TextItem => "Text",
        MarkItem { Kind: MarkKind.Tick } => "Tick",
        MarkItem => "Cross",
        SignatureItem { Kind: SignatureKind.Initials } => "Initials",
        SignatureItem => "Signature",
        ShapeItem shape => ShapeName(shape.Kind),
        InkItem => "Drawing",
        PictureItem => "Picture",
    };

    /// <summary>"Signature selected", "3 ticks", "2 items" (E3, S2).</summary>
    private static string DescribeSelection(IReadOnlyList<Item> items)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        if (items.Count == 1)
        {
            return $"{ItemName(items[0])} selected";
        }

        var names = items.Select(ItemName).Distinct().ToList();
        if (names.Count > 1)
        {
            return $"{items.Count} items";
        }

        string plural = names[0] switch
        {
            "Cross" => "crosses",
            "Text" => "texts",
            "Initials" => "initials",
            "Whiteout" => "whiteouts",
            var name => name.ToLowerInvariant() + "s",
        };
        return $"{items.Count} {plural}";
    }

    // ----- Flags for the view -----

    public bool IsSelectTool => Tool == EditorTool.Select;

    public bool IsTextTool => Tool == EditorTool.Text;

    public bool IsTickTool => Tool == EditorTool.Tick;

    public bool IsCrossTool => Tool == EditorTool.Cross;

    public bool IsDateTool => Tool == EditorTool.Date;

    public bool IsSignTool => Tool == EditorTool.Sign;

    public bool IsDrawTool => Tool == EditorTool.Draw;

    public bool ShowsHint => ContextMode == ContextMode.Hint;

    public bool ShowsOptions => ContextMode is ContextMode.ToolOptions or ContextMode.Typing or ContextMode.Drawing or ContextMode.Selection;

    public bool ShowsSelectionActions => ContextMode == ContextMode.Selection;

    public bool ShowsDone => ContextMode == ContextMode.Drawing;

    public bool ShowsPlacing => ContextMode == ContextMode.Placing;

    public bool ShowsBoxSelecting => ContextMode == ContextMode.BoxSelecting;


    public bool IsBlackChosen => CurrentColour == InkColor.Black;

    public bool IsBlueChosen => CurrentColour == InkColor.Blue;

    public bool IsRedChosen => CurrentColour == InkColor.Red;

    public string OptionsTitle => ContextMode switch
    {
        ContextMode.Typing => "Typing",
        ContextMode.Drawing => "Pen",
        ContextMode.Selection => SelectionLabel,
        _ => ToolLabel,
    };

    public string OptionsHint => ContextMode switch
    {
        ContextMode.Typing => "Enter for a new line · Esc or click outside to finish",
        ContextMode.Selection => SelectionHint,
        _ => ToolHint,
    };

    /// <summary>"Line" for line widths (Thin / Medium / Thick), "Size" otherwise.</summary>
    public string SizeCaption => SizeTip.EndsWith("line", StringComparison.Ordinal) ? "Line" : "Size";

    private static readonly string[] s_contextDependents =
    [
        nameof(ShowsHint), nameof(ShowsOptions), nameof(ShowsSelectionActions), nameof(ShowsDone), nameof(ShowsPlacing),
        nameof(ShowsBoxSelecting), nameof(OptionsTitle), nameof(OptionsHint),
    ];

    private static readonly string[] s_toolDependents =
    [
        nameof(IsSelectTool), nameof(IsTextTool), nameof(IsTickTool), nameof(IsCrossTool), nameof(IsDateTool), nameof(IsSignTool),
        nameof(IsDrawTool), nameof(ContextHint),
    ];

    private static readonly string[] s_organiseBarDependents = [nameof(ShowsPageActions), nameof(ShowsOrganiseHint)];

    private static readonly string[] s_colourDependents = [nameof(IsBlackChosen), nameof(IsBlueChosen), nameof(IsRedChosen)];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        string[] dependents = e.PropertyName switch
        {
            nameof(ContextMode) => s_contextDependents,
            nameof(Tool) => s_toolDependents,
            nameof(CurrentColour) => s_colourDependents,
            nameof(SizeTip) => [nameof(SizeCaption)],
            nameof(Notice) or nameof(IsOrganising) or nameof(SelectedPages) => s_organiseBarDependents,
            _ => [],
        };
        foreach (string name in dependents)
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    // ----- Drags in progress -----

    private abstract record Gesture;

    private sealed record MovingItems(ItemId Item, PagePoint Start, DocumentState Before, bool WasSelected, bool IsAdditive, bool HasMoved) : Gesture;

    private sealed record BoxSelecting(PageId Page, PagePoint Start, bool IsAdditive, Selection SelectionBefore) : Gesture;

    private sealed record Resizing(Item Original, Corner Corner, PagePoint GrabOffset, DocumentState Before) : Gesture;

    private sealed record DrawingShape(PageId Page, PagePoint Start) : Gesture;

    private sealed record DrawingInk(PageId Page, List<PagePoint> Points) : Gesture;
}
