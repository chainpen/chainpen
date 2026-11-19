using System.Collections.Immutable;
using Fillsign.Core.Editing;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.App.ViewModels;

/// <summary>A form field under the pointer, with its box on the page as the editor shows it (page points).</summary>
public sealed record FieldHit(FormField Field, PageId Page, PageRect Box);

/// <summary>A text field being typed into, in a box over the field (FS-950).</summary>
/// <param name="SelectsAll">Reached with Tab, so its text starts selected and typing replaces it, as in a browser.</param>
public sealed record FieldEdit(FieldHit Hit, string Value, bool SelectsAll = false);

/// <summary>
/// What the arrow keys do in a multi-select list. As the WAI-ARIA list box pattern recommends for lists with several
/// choices, arrows move the outline without changing what's chosen, so Space ticks rows one by one.
/// </summary>
public enum ListKeys
{
    /// <summary>Plain arrows: move the outline. (In other fields, plain arrows change the choice.)</summary>
    Choose,

    /// <summary>⌘/Ctrl+arrows: move the outline too.</summary>
    MoveOnly,

    /// <summary>Shift+arrows: choose every row from where the selection started to here.</summary>
    Extend,
}

/// <summary>
/// FS-950: filling the PDF's own form fields. With Select or Text, a click on a field fills it as a PDF reader would:
/// check boxes tick, radio buttons choose, text fields take typing and lists offer their choices. Values are part of
/// the document state, so they undo, save in the working copy and go into the final copy with the fields still
/// working (FR-8.9).
/// </summary>
public sealed partial class EditorViewModel
{
    private ImmutableSortedDictionary<string, string>? _fieldsShown;

    /// <summary>The text field being typed into, if any. The view shows a text box over it.</summary>
    public FieldEdit? EditingField { get; private set; }

    /// <summary>Raised when a list or drop-down field was clicked: the view offers its options next to it.</summary>
    public event Action<FieldHit, string>? FieldChoiceRequested;

    /// <summary>The editable field at a point on a page, topmost first; null when there's none, or it's read-only.</summary>
    public FieldHit? FieldAt(PageId page, PagePoint point) => TopFieldAt(page, point) is { Field.IsReadOnly: false } hit ? hit : null;

    /// <summary>Whether the field at a point is one the form fills in itself, such as a total, so it can't be changed.</summary>
    public bool IsOverReadOnlyField(PageId page, PagePoint point) =>
        Tool is EditorTool.Select or EditorTool.Text && TopFieldAt(page, point) is { Field.IsReadOnly: true };

    /// <summary>The topmost field of any kind at a point.</summary>
    private FieldHit? TopFieldAt(PageId page, PagePoint point)
    {
        if (State.FindPage(page) is not { } entry || entry.SourceIndex >= Source.PageCount)
        {
            return null;
        }

        var transform = new PageTransform(Source.GetPage(entry.SourceIndex), entry.Rotation);
        IReadOnlyList<FormField> fields = Source.FormFields(entry.SourceIndex);
        for (int i = fields.Count - 1; i >= 0; i--)
        {
            FormField field = fields[i];
            PageRect box = BoxOf(field, transform);
            if (box.Contains(point))
            {
                return new FieldHit(field, page, box);
            }
        }

        return null;
    }

    /// <summary>The field's value now: what the person filled in, else what the file has.</summary>
    public string FieldValue(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return State.Fields.TryGetValue(field.Name, out string? value) ? value : field.Value;
    }

    /// <summary>
    /// A check box, radio button or list reached with Tab: the page draws a ring round it, and Space or Enter fills it
    /// as a click would. Text fields reached with Tab open for typing instead (<see cref="EditingField"/>).
    /// </summary>
    public FieldHit? FocusedField { get; private set; }

    /// <summary>A click on a field (Select or Text tool, nothing of Fillsign's on top). Returns whether it was used.</summary>
    private bool PressField(PageId page, PagePoint point)
    {
        if (IsViewOnly || Tool is not (EditorTool.Select or EditorTool.Text) || TopFieldAt(page, point) is not { } hit)
        {
            return false;
        }

        // Say why nothing happens, instead of ignoring the click.
        if (hit.Field.IsReadOnly)
        {
            Notice = "That field can’t be changed: the form fills it in itself.";
            Changed();
            return true;
        }

        return ActivateField(hit, point);
    }

    /// <summary>In a multi-select list reached with Tab: the row the arrow keys are on, which Space ticks or unticks.</summary>
    public int FocusedOption { get; private set; }

    /// <summary>
    /// Space or Enter on the field Tab reached: fills it as a click in its middle would. A multi-select list ticks or
    /// unticks the row the arrow keys are on; other lists and drop-downs open their menu.
    /// </summary>
    public bool ActivateFocusedField()
    {
        if (FocusedField is not { } focused)
        {
            return false;
        }

        if (focused.Field is { IsMultiSelect: true, Options.IsEmpty: false } list)
        {
            ChooseOption(list, list.Options[Math.Clamp(FocusedOption, 0, list.Options.Length - 1)]);
            _optionAnchor = FocusedOption;
            Changed();
            return true;
        }

        return ActivateField(focused, point: null);
    }

    // Where a Shift+arrow selection in a multi-select list started.
    private int _optionAnchor;

    /// <summary>
    /// Arrow keys on the field Tab reached (<paramref name="step"/> −1 for up or left, +1 for down or right), as in a
    /// browser: a radio button moves to the next one in its group, wrapping round, and chooses it; a drop-down or list
    /// changes its choice; a multi-select list moves its row outline, and with Shift chooses the rows passed over.
    /// Returns false when the field has nothing to move through.
    /// </summary>
    public bool MoveWithinField(int step, ListKeys keys = ListKeys.Choose)
    {
        if (FocusedField is not { } focused || IsViewOnly)
        {
            return false;
        }

        FormField field = focused.Field;
        switch (field.Kind)
        {
            case FormFieldKind.Radio:
                List<FieldHit> group = FieldsInDocumentOrder().FindAll(hit => hit.Field.Name == field.Name);
                int at = group.FindIndex(hit => hit.Page == focused.Page && hit.Box == focused.Box);
                FieldHit next = group[(((at + step) % group.Count) + group.Count) % group.Count];
                SetFocusedField(next);
                if (next.Field.OnValue.Length > 0)
                {
                    SetField(next.Field.Name, next.Field.OnValue);
                }

                ScrollToAreaRequested?.Invoke(State.IndexOf(next.Page), next.Box);
                break;
            case FormFieldKind.Choice when field.IsMultiSelect && !field.Options.IsEmpty:
                FocusedOption = Math.Clamp(FocusedOption + step, 0, field.Options.Length - 1);
                OnPropertyChanged(nameof(FocusedOption));
                if (keys != ListKeys.Extend)
                {
                    _optionAnchor = FocusedOption;
                }
                else
                {
                    int from = Math.Min(_optionAnchor, FocusedOption), to = Math.Max(_optionAnchor, FocusedOption);
                    SetField(field.Name, PdfiumDocument.JoinChoices(field.Options.Skip(from).Take(to - from + 1)));
                }

                break;
            case FormFieldKind.Choice when !field.Options.IsEmpty:
                int chosen = field.Options.IndexOf(FieldValue(field));
                int option = chosen < 0 ? (step > 0 ? 0 : field.Options.Length - 1) : Math.Clamp(chosen + step, 0, field.Options.Length - 1);
                SetField(field.Name, field.Options[option]);
                break;
            default:
                return false;
        }

        Changed();
        return true;
    }

    /// <summary>
    /// Fills a field the way a PDF reader does: a check box ticks or unticks, a radio button is chosen, a text field
    /// opens for typing, a list picks (or, for a multi-select list, toggles) the row clicked, and a drop-down offers
    /// its options. <paramref name="point"/> is where a click landed; null for the keyboard.
    /// </summary>
    private bool ActivateField(FieldHit hit, PagePoint? point)
    {
        FormField field = hit.Field;
        Selection = Selection.None;
        SetFocusedField(field.Kind == FormFieldKind.Text ? null : hit);
        switch (field.Kind)
        {
            case FormFieldKind.CheckBox:
                string on = field.OnValue.Length > 0 ? field.OnValue : "Yes";
                SetField(field.Name, FieldValue(field) == PdfiumDocument.FieldOff ? on : PdfiumDocument.FieldOff);
                break;
            case FormFieldKind.Radio when field.OnValue.Length > 0:
                SetField(field.Name, field.OnValue);
                break;
            case FormFieldKind.Text:
                EditingField = new FieldEdit(hit, FieldValue(field));
                OnPropertyChanged(nameof(EditingField));
                break;
            case FormFieldKind.Choice when point is { } at && ListRowAt(hit, at) is { } row:
                // A list shows its options as rows: the click picks the row under it, as in PDF readers.
                ChooseOption(field, field.Options[row]);
                break;
            case FormFieldKind.Choice when !field.Options.IsEmpty:
                FieldChoiceRequested?.Invoke(hit, FieldValue(field));
                break;
            default:
                return false;
        }

        Changed();
        return true;
    }

    /// <summary>
    /// An option picked from a list or drop-down. In a multi-select list it's added or, if already chosen, taken off;
    /// otherwise it replaces the choice.
    /// </summary>
    public void ChooseOption(FormField field, string option)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(option);
        if (!field.IsMultiSelect)
        {
            SetField(field.Name, option);
            return;
        }

        var chosen = new HashSet<string>(PdfiumDocument.SplitChoices(FieldValue(field)), StringComparer.Ordinal);
        if (!chosen.Remove(option))
        {
            chosen.Add(option);
        }

        SetField(field.Name, PdfiumDocument.JoinChoices(field.Options.Where(chosen.Contains)));
    }

    /// <summary>Fills a field; one undo step. Nothing happens if it already has that value.</summary>
    public void SetField(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        bool isSame = State.Fields.TryGetValue(name, out string? current)
            ? current == value
            : FieldsNamed(name).FirstOrDefault() is { } field && FieldValue(field) == value && field.Kind != FormFieldKind.Radio;
        if (isSame || value.Length > DocumentState.MaxFieldValueLength)
        {
            return;
        }

        Change(State.WithField(name, value));
        Changed();
    }

    /// <summary>Finishes typing into a text field, keeping what was typed.</summary>
    public void CommitFieldEdit(string text)
    {
        if (EditingField is not { } editing)
        {
            return;
        }

        EditingField = null;
        OnPropertyChanged(nameof(EditingField));
        SetField(editing.Hit.Field.Name, text);
    }

    /// <summary>Tab in a text field: keeps what was typed, then moves on as <see cref="MoveFieldFocus"/> does.</summary>
    public void CommitFieldEditAndMove(string text, bool backwards)
    {
        if (EditingField is not { } editing)
        {
            return;
        }

        CommitFieldEdit(text);
        MoveFieldFocus(editing.Hit, backwards);
    }

    /// <summary>
    /// Tab on the page, as in a browser: from the field last used to the next one in the PDF's own order (Shift+Tab:
    /// the previous one), of any kind, on to the following pages; or, when no field is in use, into the first field
    /// (last, going back) of the page in view. Returns false past the last field, so focus moves on from the page.
    /// </summary>
    public bool MoveFieldFocus(bool backwards)
    {
        if (FocusedField is { } focused)
        {
            return MoveFieldFocus(focused, backwards);
        }

        List<FieldHit> order = TabStops();
        PageId current = State.Pages[Math.Clamp(CurrentPageIndex, 0, State.Pages.Length - 1)].Id;
        List<FieldHit> onPage = order.FindAll(hit => hit.Page == current);
        if (onPage.Count == 0)
        {
            return false;
        }

        FocusOn(backwards ? onPage[^1] : onPage[0]);
        return true;
    }

    private bool MoveFieldFocus(FieldHit from, bool backwards)
    {
        List<FieldHit> order = TabStops();

        // A radio group is one stop, whichever of its buttons is focused.
        int at = order.FindIndex(hit => (hit.Page == from.Page && hit.Box == from.Box)
            || (from.Field.Kind == FormFieldKind.Radio && hit.Field.Kind == FormFieldKind.Radio && hit.Field.Name == from.Field.Name));
        int next = at < 0 ? -1 : at + (backwards ? -1 : 1);
        if (next < 0 || next >= order.Count)
        {
            SetFocusedField(null);
            return false;
        }

        FocusOn(order[next]);
        return true;
    }

    /// <summary>A text field opens for typing; any other field gets the focus ring.</summary>
    private void FocusOn(FieldHit target)
    {
        if (target.Field.Kind == FormFieldKind.Text)
        {
            SetFocusedField(null);
            EditingField = new FieldEdit(target, FieldValue(target.Field), SelectsAll: true);
            OnPropertyChanged(nameof(EditingField));
        }
        else
        {
            SetFocusedField(target);
        }

        ScrollToAreaRequested?.Invoke(State.IndexOf(target.Page), target.Box);
        Changed();
    }

    private void SetFocusedField(FieldHit? hit)
    {
        if (FocusedField == hit)
        {
            return;
        }

        // A multi-select list starts with its highlight on the first chosen row, else the top one.
        FocusedOption = hit?.Field is { IsMultiSelect: true } list
            ? Math.Max(0, list.Options.IndexOf(PdfiumDocument.SplitChoices(FieldValue(list)).FirstOrDefault() ?? string.Empty))
            : 0;
        _optionAnchor = FocusedOption;
        FocusedField = hit;
        OnPropertyChanged(nameof(FocusedField));
    }

    /// <summary>
    /// Every fillable field, page by page, in the order the PDF lists them. Form tools write that list in the tab
    /// order the author set, so it plays the part of a web page's order. (PDFium can't read a page's /Tabs setting.)
    /// </summary>
    private List<FieldHit> FieldsInDocumentOrder()
    {
        var order = new List<FieldHit>();
        foreach (PageEntry entry in State.Pages)
        {
            if (entry.SourceIndex >= Source.PageCount)
            {
                continue;
            }

            var transform = new PageTransform(Source.GetPage(entry.SourceIndex), entry.Rotation);
            order.AddRange(Source.FormFields(entry.SourceIndex)
                .Where(field => !field.IsReadOnly)
                .Select(field => new FieldHit(field, entry.Id, BoxOf(field, transform))));
        }

        return order;
    }

    /// <summary>
    /// The places Tab stops, as in a browser: every field, but a radio group once, where its first button is, on the
    /// chosen button (the first if none is chosen). The arrow keys move within the group.
    /// </summary>
    private List<FieldHit> TabStops()
    {
        List<FieldHit> fields = FieldsInDocumentOrder();
        var stops = new List<FieldHit>();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldHit hit in fields)
        {
            if (hit.Field.Kind != FormFieldKind.Radio)
            {
                stops.Add(hit);
            }
            else if (groups.Add(hit.Field.Name))
            {
                string value = FieldValue(hit.Field);
                stops.Add(fields.Find(button => button.Field.Kind == FormFieldKind.Radio && button.Field.Name == hit.Field.Name && button.Field.OnValue == value) ?? hit);
            }
        }

        return stops;
    }

    /// <summary>Stops typing into a text field without changing it (Esc).</summary>
    public void CancelFieldEdit()
    {
        if (EditingField is null)
        {
            return;
        }

        EditingField = null;
        OnPropertyChanged(nameof(EditingField));
    }

    /// <summary>Esc with a field focused, or a click elsewhere: the ring goes.</summary>
    public void LeaveFields() => SetFocusedField(null);

    /// <summary>Whether the pointer would fill a field here, so the view shows a hand.</summary>
    public bool IsOverField(PageId page, PagePoint point) =>
        !IsViewOnly && Tool is EditorTool.Select or EditorTool.Text && FieldAt(page, point) is not null;

    /// <summary>
    /// Keeps the pages on screen showing the filled values: after any change to them (including undo), the source's
    /// preview copy is filled and the pages drawn again.
    /// </summary>
    partial void OnStateChanged(DocumentState value)
    {
        if (ReferenceEquals(_fieldsShown, value.Fields) || (_fieldsShown is null && value.Fields.IsEmpty))
        {
            return;
        }

        _fieldsShown = value.Fields;
        if (Source.ShowFields(value.Fields))
        {
            Renderer.MarkStale([.. Enumerable.Range(0, Source.PageCount).Where(index => Source.FormFields(index).Count > 0)]);
        }
    }

    /// <summary>
    /// The option row clicked in a list field, when every option shows (no scrolling) on an upright page. Otherwise
    /// null, and the options are offered in a menu instead.
    /// </summary>
    private int? ListRowAt(FieldHit hit, PagePoint point)
    {
        FormField field = hit.Field;
        if (!field.IsList || field.Options.IsEmpty || State.FindPage(hit.Page) is not { } entry
            || (Source.GetPage(entry.SourceIndex).Rotate + entry.Rotation) != Rotation.None
            || field.Options.Length * field.ListRowHeight > hit.Box.Height)
        {
            return null;
        }

        const double Border = 1;
        int row = (int)Math.Floor((point.Y - hit.Box.Y - Border) / field.ListRowHeight);
        return row >= 0 && row < field.Options.Length ? row : null;
    }

    private IEnumerable<FormField> FieldsNamed(string name) =>
        Enumerable.Range(0, Source.PageCount).SelectMany(Source.FormFields).Where(field => field.Name == name);

    private static PageRect BoxOf(FormField field, PageTransform transform)
    {
        (double Left, double Bottom, double Right, double Top) rect = field.Rect;
        PagePoint a = transform.ToDisplay(rect.Left, rect.Bottom), b = transform.ToDisplay(rect.Right, rect.Top);
        return new PageRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
