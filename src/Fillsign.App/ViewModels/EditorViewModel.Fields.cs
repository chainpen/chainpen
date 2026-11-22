using System.Collections.Immutable;
using Fillsign.App.Localization;
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
    public FieldEdit? EditingField
    {
        get;
        private set
        {
            field = value;
            _fieldEditText = null;
        }
    }

    // What's in the field's text box so far. The view reports each change, so Save, Export, Print and Close keep it
    // without waiting for the box to lose focus.
    private string? _fieldEditText;

    /// <summary>The view calls this as the person types into a form field, with everything in its box.</summary>
    public void UpdateFieldEdit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (EditingField is not null)
        {
            _fieldEditText = text;
        }
    }

    /// <summary>Keeps what's been typed into a form field so far, if one is being typed into.</summary>
    public void FinishFieldEdit()
    {
        if (EditingField is { } editing)
        {
            CommitFieldEdit(_fieldEditText ?? editing.Value);
        }
    }

    /// <summary>Raised when a list or drop-down field was clicked: the view offers its options next to it.</summary>
    public event Action<FieldHit, string>? FieldChoiceRequested;

    /// <summary>The editable field at a point on a page, topmost first; null when there's none, or it's read-only.</summary>
    public FieldHit? FieldAt(PageId page, PagePoint point) =>
        TopFieldAt(page, point) is { Field: { IsReadOnly: false, Kind: not FormFieldKind.PushButton } } hit ? hit : null;

    /// <summary>
    /// Whether the field at a point can't be filled here: one the form fills in itself (a total), or a button that
    /// runs the form's scripts (FS-965).
    /// </summary>
    public bool IsOverReadOnlyField(PageId page, PagePoint point) => WhyNotFillable(page, point) is not null;

    /// <summary>
    /// What a click on a field that can't be filled would say, so the pointer can say it first (UX review 9): a
    /// button looks clickable but does nothing here.
    /// </summary>
    public string? WhyNotFillable(PageId page, PagePoint point) =>
        Tool is not (EditorTool.Select or EditorTool.Text) ? null : TopFieldAt(page, point)?.Field switch
        {
            { Kind: FormFieldKind.PushButton } => Strings.Editor_FormButtonNote,
            { IsReadOnly: true } => Strings.Editor_ReadOnlyFieldNote,
            _ => null,
        };


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
    /// Whether a check box is ticked. One field can have several boxes with their own on-values ("Yes" and "No"); then
    /// only the box the field's value names is. A value that names none of them (the command line's "Yes" for a box
    /// the form calls "On") stands for the box the file already ticks, or else the field's first, as when it's filled.
    /// </summary>
    public bool IsTicked(FormField box) => IsTicked(box, State);

    private bool IsTicked(FormField box, DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(box);
        string value = state.Fields.TryGetValue(box.Name, out string? filled) ? filled : box.Value;
        if (value.Length == 0 || value == PdfiumDocument.FieldOff)
        {
            return false;
        }

        if (value == box.OnValue)
        {
            return true;
        }

        FormField[] boxes = [.. FieldsNamed(box.Name).Where(other => other.Kind == FormFieldKind.CheckBox)];
        if (boxes.Any(other => other.OnValue == value))
        {
            return false;
        }

        return boxes.Any(other => other.IsChecked) ? box.IsChecked : boxes.Length == 0 || boxes[0].OnValue == box.OnValue;
    }

    /// <summary>
    /// A check box, radio button or list reached with Tab: the page draws a ring round it, and Space or Enter fills it
    /// as a click would. Text fields reached with Tab open for typing instead (<see cref="EditingField"/>).
    /// </summary>
    public FieldHit? FocusedField { get; private set; }

    /// <summary>
    /// A click on a field, with nothing of Fillsign's on top. Select and Text fill any field; Tick and Cross fill the
    /// form's own check boxes and radio buttons, rather than drawing a mark over them that the form wouldn't know about.
    /// Returns whether the click was used.
    /// </summary>
    private bool PressField(PageId page, PagePoint point)
    {
        if (IsViewOnly || TopFieldAt(page, point) is not { } hit)
        {
            return false;
        }

        bool isBox = hit.Field.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio;
        if (Tool is not (EditorTool.Select or EditorTool.Text) && !(Tool is EditorTool.Tick or EditorTool.Cross && isBox && !hit.Field.IsReadOnly))
        {
            return false;
        }

        // Say why nothing happens, instead of ignoring the click.
        if (hit.Field.IsReadOnly)
        {
            Tell(Strings.Editor_ReadOnlyFieldNote);
            Changed();
            return true;
        }

        if (hit.Field.Kind == FormFieldKind.PushButton)
        {
            Tell(Strings.Editor_FormButtonNote);
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
                OnPropertyChanged(nameof(FieldAnnouncement));
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
                SetField(field.Name, IsTicked(field) ? PdfiumDocument.FieldOff : on);
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
            case FormFieldKind.Choice when !field.Options.IsEmpty || field.IsEditable:
                FieldChoiceRequested?.Invoke(hit, FieldValue(field));
                break;
            case FormFieldKind.Signature:
                _ = SignIntoFieldAsync(hit);
                break;
            default:
                return false;
        }

        Changed();
        return true;
    }

    /// <summary>A drop-down that takes typing (FS-964): "Type an answer…" opens it for typing like a text field.</summary>
    public void TypeIntoField(FieldHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        SetFocusedField(null);
        EditingField = new FieldEdit(hit, FieldValue(hit.Field), SelectsAll: true);
        OnPropertyChanged(nameof(EditingField));
        Changed();
    }

    /// <summary>
    /// A signature field (FS-962): choose or make a signature as Sign does, and it's placed inside the field's box,
    /// as large as fits, centred. It's an ordinary signature item afterwards: move it, resize it or delete it.
    /// </summary>
    private async Task SignIntoFieldAsync(FieldHit hit)
    {
        // A field named for initials ("Initials", "Initial here") asks for initials; any other, a signature.
        SignatureKind kind = hit.Field.DisplayName.Contains("initial", StringComparison.OrdinalIgnoreCase)
            || hit.Field.Name.Contains("initial", StringComparison.OrdinalIgnoreCase)
            ? SignatureKind.Initials
            : SignatureKind.Signature;
        if (await _host.ChooseSignatureAsync(kind).ConfigureAwait(true) is not { } chosen)
        {
            return;
        }

        const double Padding = 2;
        PageRect box = hit.Box;
        double ratio = chosen.Image.PixelHeight / (double)Math.Max(1, chosen.Image.PixelWidth);
        double width = Math.Max(ItemEdits.SignatureMinWidth, Math.Min(box.Width - (2 * Padding), (box.Height - (2 * Padding)) / ratio));
        double height = width * ratio;
        var item = new SignatureItem(ItemId.New(), hit.Page, box.Centre.X - (width / 2), box.Centre.Y - (height / 2), chosen.Kind, chosen.Image, width, height);
        SetFocusedField(null);
        Change(ItemEdits.Add(State, item));
        SwitchTool(EditorTool.Select);
        Selection = new ItemSelection([item.Id]);
        Changed();
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
        if (FieldsNamed(name).FirstOrDefault() is { Kind: FormFieldKind.Signature or FormFieldKind.PushButton })
        {
            return;
        }

        bool isSame = State.Fields.TryGetValue(name, out string? current)
            ? current == value
            : FieldsNamed(name).FirstOrDefault() is { } field && FieldValue(field) == value && field.Kind != FormFieldKind.Radio;
        // A name or value longer than the working copy reads back would save, then open with nothing editable.
        if (isSame || value.Length > DocumentState.MaxFieldValueLength || name.Length > DocumentState.MaxFieldNameLength)
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

    /// <summary>
    /// Tab in a text field: keeps what was typed, then moves on as <see cref="MoveFieldFocus"/> does. False past the
    /// last field (or before the first), so the view moves focus on from the page.
    /// </summary>
    public bool CommitFieldEditAndMove(string text, bool backwards)
    {
        if (EditingField is not { } editing)
        {
            return false;
        }

        CommitFieldEdit(text);
        return MoveFieldFocus(editing.Hit, backwards);
    }

    /// <summary>
    /// Tab on the page, as in a browser: from the field last used to the next one in the PDF's own order (Shift+Tab:
    /// the previous one), of any kind, on to the following pages; or, when no field is in use, into the first field
    /// (last, going back) of the page in view. Past the last field it goes back to the first (UX review 5); false only when there's no field to go to.
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
        if (at < 0 || order.Count == 0)
        {
            SetFocusedField(null);
            return false;
        }

        // Past the last field, back to the first (Shift+Tab: first to last), so Tab stays on the form; F6 leaves it,
        // and the last field's hint says so (WCAG 2.1.2).
        int next = (at + (backwards ? order.Count - 1 : 1)) % order.Count;
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
        OnPropertyChanged(nameof(FieldAnnouncement));
        OnPropertyChanged(nameof(ContextHint));
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
                .Where(field => field is { IsReadOnly: false, Kind: not FormFieldKind.PushButton })
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

    /// <summary>
    /// What a screen reader says about the field Tab reached (NFR-5): what it asks, what kind of control it is, and
    /// its state, e.g. "Membership, radio button Family, 2 of 3, chosen". Empty when no field is focused. Text fields
    /// are named on their text box instead (<see cref="FieldBoxName"/>).
    /// </summary>
    public string FieldAnnouncement => FocusedField is { } focused ? Describe(focused) : string.Empty;

    /// <summary>
    /// The bar's words while a form field is in use: which field, and the keys that work in it. Null when none is.
    /// </summary>
    public string? FieldHint
    {
        get
        {
            if (EditingField is { } editing)
            {
                string enter = editing.Hit.Field.IsMultiline ? Strings.Editor_EnterForANewLine : Strings.Editor_EnterToFinish;
                return Text.Format(Strings.Editor_TypingInFieldHint, editing.Hit.Field.DisplayName, enter, TabHint(editing.Hit));
            }

            if (FocusedField is not { Field: var focused })
            {
                return null;
            }

            string keys = focused.Kind switch
            {
                FormFieldKind.CheckBox => Strings.Editor_FieldKeysCheckBox,
                FormFieldKind.Radio => Strings.Editor_FieldKeysRadio,
                FormFieldKind.Choice when focused.IsMultiSelect => Strings.Editor_FieldKeysMultiSelect,
                FormFieldKind.Choice when focused.IsList => Strings.Editor_FieldKeysList,
                FormFieldKind.Choice => Strings.Editor_FieldKeysDropDown,
                FormFieldKind.Signature => Strings.Editor_FieldKeysSignature,
                _ => Strings.Editor_FieldKeysFill,
            };
            return Text.Format(Strings.Editor_FocusedFieldHint, focused.DisplayName, keys, TabHint(FocusedField!));
        }
    }

    /// <summary>The accessible name of a text field's box: what it asks, and its limit if it has one.</summary>
    public static string FieldBoxName(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        string kind = field.Kind == FormFieldKind.Choice ? Strings.Editor_FieldKindDropDownTyped
            : field.IsMultiline ? Strings.Editor_FieldKindTextSeveralLines
            : Strings.Editor_FieldKindText;
        return field.MaxLength > 0
            ? Text.Format(Strings.Editor_FieldBoxNameWithLimit, field.DisplayName, kind, field.MaxLength)
            : Text.Format(Strings.Editor_FieldBoxName, field.DisplayName, kind);
    }

    private string Describe(FieldHit hit)
    {
        FormField field = hit.Field;
        string value = FieldValue(field);
        switch (field.Kind)
        {
            case FormFieldKind.CheckBox:
                return Text.Format(IsTicked(field) ? Strings.Editor_FieldCheckBoxTicked : Strings.Editor_FieldCheckBoxNotTicked, AccessibleName(hit));
            case FormFieldKind.Radio:
                List<FieldHit> group = FieldsInDocumentOrder().FindAll(other => other.Field.Name == field.Name);
                int position = group.FindIndex(other => other.Page == hit.Page && other.Box == hit.Box) + 1;
                return Text.Format(
                    value == field.OnValue ? Strings.Editor_FieldRadioChosen : Strings.Editor_FieldRadioNotChosen,
                    field.DisplayName,
                    field.OnValue,
                    position,
                    group.Count);
            case FormFieldKind.Choice when field.IsMultiSelect && !field.Options.IsEmpty:
                IReadOnlySet<string> chosen = PdfiumDocument.SplitChoices(value);
                string row = field.Options[Math.Clamp(FocusedOption, 0, field.Options.Length - 1)];
                return Text.Format(
                    chosen.Contains(row) ? Strings.Editor_FieldMultiSelectTicked : Strings.Editor_FieldMultiSelectNotTicked,
                    field.DisplayName,
                    row,
                    chosen.Count,
                    field.Options.Length);
            case FormFieldKind.Choice:
                return value.Length > 0
                    ? Text.Format(field.IsList ? Strings.Editor_FieldList : Strings.Editor_FieldDropDown, field.DisplayName, value)
                    : Text.Format(field.IsList ? Strings.Editor_FieldListNothingChosen : Strings.Editor_FieldDropDownNothingChosen, field.DisplayName);
            case FormFieldKind.Signature:
                return Text.Format(Strings.Editor_FieldSignature, field.DisplayName);
            default:
                return value.Length > 0
                    ? Text.Format(Strings.Editor_FieldText, field.DisplayName, value)
                    : Text.Format(Strings.Editor_FieldTextEmpty, field.DisplayName);
        }
    }

    /// <summary>
    /// A field's name for screen readers. A check box also says the question its row answers, read from the words in
    /// front of the row ("Contact me by: Email"), as sighted people see it; a radio button's group name already does.
    /// </summary>
    public string AccessibleName(FieldHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        FormField field = hit.Field;
        if (field.Kind != FormFieldKind.CheckBox)
        {
            return field.DisplayName;
        }

        PageEntry? entry = State.Pages.FirstOrDefault(candidate => candidate.Id == hit.Page);
        if (entry is null || entry.SourceIndex >= Source.PageCount)
        {
            return field.DisplayName;
        }

        // The row's first box: the words before a later one would be the previous box's own label.
        var transform = new PageTransform(Source.GetPage(entry.SourceIndex), Rotation.None);
        PageRect first = Source.FormFields(entry.SourceIndex)
            .Where(other => other.Kind == FormFieldKind.CheckBox)
            .Select(other => BoxOf(other, transform))
            .Where(box => Math.Abs(box.Centre.Y - BoxOf(field, transform).Centre.Y) <= Math.Max(4, box.Height / 2))
            .MinBy(box => box.X);
        string key = $"{entry.SourceIndex}:{first.X:0.#}:{first.Y:0.#}";
        if (!_rowQuestions.TryGetValue(key, out string? question))
        {
            question = Pdf.Export.FormSpotReader.WordsBefore(Source.Pdf, entry.SourceIndex, Source.GetPage(entry.SourceIndex), first);
            _rowQuestions[key] = question;
        }

        return question.Length == 0 || string.Equals(question, field.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? field.DisplayName
            : Text.Format(Strings.Editor_FieldQuestionAndName, question, field.DisplayName);
    }

    private readonly Dictionary<string, string> _rowQuestions = new(StringComparer.Ordinal);

    /// <summary>What Tab does from here: the next field, or, on the last, back to the first, with F6 to leave the form.</summary>
    private string TabHint(FieldHit hit)
    {
        List<FieldHit> order = TabStops();
        bool isLast = order.Count > 1 && order[^1].Page == hit.Page && order[^1].Box == hit.Box;
        return isLast ? Strings.Editor_TabBackToFirstField : Strings.Editor_TabForNextField;
    }

    /// <summary>For screen readers: fills a field as a click or Space would (ticks a box, opens signing).</summary>
    public void Activate(FieldHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (!IsViewOnly && !hit.Field.IsReadOnly)
        {
            ActivateField(hit, point: null);
        }
    }

    /// <summary>For screen readers: moves to a field as Tab would, scrolling it into view.</summary>
    public void FocusField(FieldHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        FocusOn(hit);
    }

    /// <summary>
    /// The fields on the page in view and the pages either side, in the form's order: what the page offers screen
    /// readers, without building hundreds of entries on long forms.
    /// </summary>
    public IReadOnlyList<FieldHit> FieldsNearCurrentPage()
    {
        if (State.Pages.Length == 0)
        {
            return [];
        }

        int current = Math.Clamp(CurrentPageIndex, 0, State.Pages.Length - 1);
        var near = new HashSet<PageId>(State.Pages.Skip(Math.Max(0, current - 1)).Take(3).Select(entry => entry.Id));
        return FieldsInDocumentOrder().FindAll(hit => near.Contains(hit.Page));
    }

    /// <summary>With Tick or Cross, the pointer is over one of the form's own boxes: the click fills it, so no preview mark.</summary>
    private bool IsOverBoxField(PageId page, PagePoint point) =>
        FieldAt(page, point) is { Field.Kind: FormFieldKind.CheckBox or FormFieldKind.Radio };

    /// <summary>The document has fields to fill, so the bar and the opening note point at them.</summary>
    public bool HasFillableFields => HasFormFields();

    /// <summary>
    /// The bar under the toolbar on a form (UX review 2): how many fields there are and how to start, then how many are
    /// filled. It used to be a note over the page, which covered the next fields and repeated this bar.
    /// </summary>
    private string FormHint()
    {
        List<FieldHit> stops = TabStops();
        int total = stops.Count;
        int filled = stops.Count(IsFilled);
        if (filled == 0)
        {
            return Text.Plural(total, Strings.Editor_FieldsToFill_One, Strings.Editor_FieldsToFill_Other);
        }

        return filled == total
            ? Text.Format(Strings.Editor_AllFieldsFilled, total)
            : Text.Format(Strings.Editor_FieldsFilledOfTotal, filled, total);
    }

    /// <summary>Whether a field has an answer: text or a choice, a ticked box, a chosen button, or a signature placed on it.</summary>
    private bool IsFilled(FieldHit hit) => hit.Field.Kind switch
    {
        FormFieldKind.CheckBox => FieldValue(hit.Field) is { Length: > 0 } ticked && ticked != PdfiumDocument.FieldOff,
        FormFieldKind.Radio => FieldValue(hit.Field) is { Length: > 0 } chosen && chosen != PdfiumDocument.FieldOff,
        FormFieldKind.Signature => IsSigned(hit),
        _ => FieldValue(hit.Field).Length > 0,
    };

    /// <summary>
    /// Signature fields on a page with nothing signed on them yet (UX review 4). Many forms draw no box for them, so
    /// the page shows them while editing; it's never printed or exported.
    /// </summary>
    public IEnumerable<FieldHit> UnsignedSignatureFields(PageId page)
    {
        PageEntry? entry = State.Pages.FirstOrDefault(candidate => candidate.Id == page);
        if (IsViewOnly || entry is null || entry.SourceIndex >= Source.PageCount)
        {
            return [];
        }

        var transform = new PageTransform(Source.GetPage(entry.SourceIndex), entry.Rotation);
        return Source.FormFields(entry.SourceIndex)
            .Where(field => field is { Kind: FormFieldKind.Signature, IsReadOnly: false })
            .Select(field => new FieldHit(field, page, BoxOf(field, transform)))
            .Where(hit => !IsSigned(hit))
            .ToList();
    }

    /// <summary>A signature field counts as signed once a signature or initials sit on it.</summary>
    private bool IsSigned(FieldHit hit) =>
        State.ItemsOn(hit.Page).OfType<SignatureItem>().Any(item => new PageRect(item.X, item.Y, item.Width, item.Height).Intersects(hit.Box));

    /// <summary>Whether any page in the document has a field to fill (not counting buttons).</summary>
    private bool HasFormFields() =>
        State.Pages.Any(entry => entry.SourceIndex < Source.PageCount
            && Source.FormFields(entry.SourceIndex).Any(field => field.Kind != FormFieldKind.PushButton));

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
