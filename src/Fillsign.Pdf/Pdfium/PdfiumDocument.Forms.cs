using System.Collections.Immutable;
using System.Runtime.InteropServices;
using static Fillsign.Pdf.Pdfium.PdfiumNative;

namespace Fillsign.Pdf.Pdfium;

/// <summary>The kinds of form field Fillsign knows (FS-950).</summary>
public enum FormFieldKind
{
    Text,
    CheckBox,
    Radio,
    Choice,

    /// <summary>A box for a signature (FS-962). Fillsign signs over it with a signature item; it has no value to fill.</summary>
    Signature,

    /// <summary>Reset, Print, Submit: they run the form's scripts, which Fillsign doesn't (FS-965). Never filled.</summary>
    PushButton,
}

/// <summary>
/// One widget of a form field on a page (FS-950). A field can have several widgets (radio buttons, or the same name
/// repeated); they share <see cref="Name"/> and its value.
/// </summary>
/// <param name="Rect">The widget's box in the page's unrotated PDF user space: left, bottom, right, top.</param>
/// <param name="Value">
/// The field's value: the text, the chosen option, or for buttons the on-value or "Off". A multi-select list holds its
/// chosen options one per line (<see cref="PdfiumDocument.JoinChoices"/>).
/// </param>
/// <param name="OnValue">For check boxes and radio buttons: the value this widget sets when chosen.</param>
/// <param name="IsList">A choice field shown as a list of rows (not a drop-down), picked by clicking a row.</param>
/// <param name="FontSize">The size the field's text is drawn at, in points; 0 when it's sized to fit.</param>
/// <param name="MaxLength">The most characters a text field takes (/MaxLen, FS-963); 0 for no limit.</param>
/// <param name="IsEditable">A drop-down that also takes a typed answer not in its list (FS-964).</param>
/// <param name="IsComb">A text field drawn as a row of boxes, one per character up to <paramref name="MaxLength"/>.</param>
/// <param name="Label">
/// What the field asks for, for people: its /TU (the tooltip in readers), or, when a document is opened in Fillsign,
/// the words printed beside it (<see cref="Import.FieldLabels"/>). Empty when there's neither.
/// </param>
public sealed record FormField(
    string Name,
    FormFieldKind Kind,
    (double Left, double Bottom, double Right, double Top) Rect,
    string Value,
    bool IsChecked,
    string OnValue,
    ImmutableArray<string> Options,
    bool IsReadOnly,
    bool IsMultiline,
    bool IsList = false,
    double FontSize = 0,
    bool IsMultiSelect = false,
    int MaxLength = 0,
    bool IsEditable = false,
    string Label = "",
    bool IsComb = false)
{
    /// <summary>The field's description if it has one, else its name, for gap hints and screen readers.</summary>
    public string DisplayName => Label.Length > 0 ? Label : Name;

    /// <summary>
    /// How tall each row of a list field is, as PDFium draws it: the font's line height, about 1.15 × the size (12 pt
    /// when the size is automatic).
    /// </summary>
    public double ListRowHeight => (FontSize > 0 ? FontSize : 12) * 1.15;
}

public sealed partial class PdfiumDocument
{
    /// <summary>The value an unticked check box or radio group has.</summary>
    public const string FieldOff = "Off";

    /// <summary>A multi-select list's value: its chosen options, one per line, in the list's order.</summary>
    public static string JoinChoices(IEnumerable<string> options) => string.Join('\n', options);

    public static IReadOnlySet<string> SplitChoices(string value) =>
        new HashSet<string>(value.Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private nint _form;
    private nint _formInfo;

    /// <summary>The fillable fields on a page, in the order the page lists them. Empty when it has none.</summary>
    public IReadOnlyList<FormField> GetFormFields(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            nint form = FormHandle();
            if (form == 0)
            {
                return [];
            }

            nint page = LoadPage(pageIndex);
            FORM_OnAfterLoadPage(page, form);
            try
            {
                var fields = new List<FormField>();
                int count = FPDFPage_GetAnnotCount(page);
                for (int index = 0; index < count; index++)
                {
                    nint annotation = FPDFPage_GetAnnot(page, index);
                    if (annotation == 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (ReadField(form, annotation) is { } field)
                        {
                            fields.Add(field);
                        }
                    }
                    finally
                    {
                        FPDFPage_CloseAnnot(annotation);
                    }
                }

                return fields;
            }
            finally
            {
                FORM_OnBeforeClosePage(page, form);
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Fills fields by name (FS-950) as a person would in a PDF reader, so PDFium writes their appearance too and the
    /// fields keep working. Text and choices take the value; a check box is ticked unless the value is "Off"; a radio
    /// button is chosen when the value is its on-value. Names the document doesn't have, and read-only fields, are
    /// skipped.
    /// </summary>
    public void FillFormFields(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return;
        }

        lock (PdfiumLibrary.Sync)
        {
            nint form = FormHandle();
            if (form == 0)
            {
                return;
            }

            var filled = new HashSet<string>(StringComparer.Ordinal);
            int pages = FPDF_GetPageCount(_document);
            for (int pageIndex = 0; pageIndex < pages; pageIndex++)
            {
                nint page = FPDF_LoadPage(_document, pageIndex);
                if (page == 0)
                {
                    continue;
                }

                FORM_OnAfterLoadPage(page, form);
                try
                {
                    FillPage(form, page, values, filled);
                }
                finally
                {
                    FORM_ForceToKillFocus(form);
                    FORM_OnBeforeClosePage(page, form);
                    FPDF_ClosePage(page);
                }
            }
        }
    }

    /// <summary>
    /// Makes every form field read-only, so readers show the answers but won't let them be changed: the usual way to
    /// lock a filled form. Links and the rest of the page are untouched. Returns how many fields it locked.
    /// </summary>
    public int LockFormFields()
    {
        lock (PdfiumLibrary.Sync)
        {
            nint form = FormHandle();
            if (form == 0)
            {
                return 0;
            }

            int locked = 0;
            int pages = FPDF_GetPageCount(_document);
            for (int pageIndex = 0; pageIndex < pages; pageIndex++)
            {
                nint page = FPDF_LoadPage(_document, pageIndex);
                if (page == 0)
                {
                    continue;
                }

                FORM_OnAfterLoadPage(page, form);
                try
                {
                    int count = FPDFPage_GetAnnotCount(page);
                    for (int index = 0; index < count; index++)
                    {
                        nint annotation = FPDFPage_GetAnnot(page, index);
                        if (annotation == 0)
                        {
                            continue;
                        }

                        try
                        {
                            if (FPDFAnnot_GetSubtype(annotation) == FPDF_ANNOT_WIDGET && FPDFAnnot_GetFormFieldType(form, annotation) > 0)
                            {
                                int flags = FPDFAnnot_GetFormFieldFlags(form, annotation);
                                if ((flags & FPDF_FORMFLAG_READONLY) == 0 && FPDFAnnot_SetFormFieldFlags(form, annotation, flags | FPDF_FORMFLAG_READONLY))
                                {
                                    locked++;
                                }
                            }
                        }
                        finally
                        {
                            FPDFPage_CloseAnnot(annotation);
                        }
                    }
                }
                finally
                {
                    FORM_OnBeforeClosePage(page, form);
                    FPDF_ClosePage(page);
                }
            }

            return locked;
        }
    }

    /// <summary>
    /// Removes the page's signature fields that overlap <paramref name="areas"/> (PDF user space): fields signed over
    /// with a signature image, which viewers would otherwise mark as unsigned. Returns how many it removed.
    /// </summary>
    public int RemoveSignatureFieldsUnder(int pageIndex, IReadOnlyList<Fillsign.Core.Model.PdfBox> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        lock (PdfiumLibrary.Sync)
        {
            nint form = FormHandle();
            if (form == 0)
            {
                return 0;
            }

            nint page = LoadPage(pageIndex);
            FORM_OnAfterLoadPage(page, form);
            int removed = 0;
            try
            {
                // From the end, so removing one doesn't move the ones still to look at.
                for (int index = FPDFPage_GetAnnotCount(page) - 1; index >= 0; index--)
                {
                    nint annotation = FPDFPage_GetAnnot(page, index);
                    if (annotation == 0)
                    {
                        continue;
                    }

                    bool isSignedOver;
                    try
                    {
                        isSignedOver = FPDFAnnot_GetSubtype(annotation) == FPDF_ANNOT_WIDGET
                            && FPDFAnnot_GetFormFieldType(form, annotation) == FPDF_FORMFIELD_SIGNATURE
                            && FPDFAnnot_GetRect(annotation, out FsRectF rect)
                            && areas.Any(area => Math.Min(rect.Left, rect.Right) < area.Right && Math.Max(rect.Left, rect.Right) > area.Left
                                && Math.Min(rect.Top, rect.Bottom) < area.Top && Math.Max(rect.Top, rect.Bottom) > area.Bottom);
                    }
                    finally
                    {
                        FPDFPage_CloseAnnot(annotation);
                    }

                    if (isSignedOver && FPDFPage_RemoveAnnot(page, index))
                    {
                        removed++;
                    }
                }
            }
            finally
            {
                FORM_OnBeforeClosePage(page, form);
                FPDF_ClosePage(page);
            }

            return removed;
        }
    }

    private static void FillPage(nint form, nint page, IReadOnlyDictionary<string, string> values, HashSet<string> filled)
    {
        int count = FPDFPage_GetAnnotCount(page);
        for (int index = 0; index < count; index++)
        {
            nint annotation = FPDFPage_GetAnnot(page, index);
            if (annotation == 0)
            {
                continue;
            }

            try
            {
                if (ReadField(form, annotation) is not { IsReadOnly: false } field || !values.TryGetValue(field.Name, out string? value))
                {
                    continue;
                }

                switch (field.Kind)
                {
                    // One widget sets a text or choice field everywhere it appears.
                    case FormFieldKind.Text when filled.Add(field.Name):
                        if (FORM_SetFocusedAnnot(form, annotation))
                        {
                            FORM_SelectAllText(form, page);
                            FORM_ReplaceSelection(form, page, field.MaxLength > 0 && value.Length > field.MaxLength ? value[..field.MaxLength] : value);
                            FORM_ForceToKillFocus(form);
                        }

                        break;

                    case FormFieldKind.Choice when field.IsMultiSelect && filled.Add(field.Name):
                        if (FORM_SetFocusedAnnot(form, annotation))
                        {
                            IReadOnlySet<string> chosen = SplitChoices(value);
                            for (int i = 0; i < field.Options.Length; i++)
                            {
                                FORM_SetIndexSelected(form, page, i, chosen.Contains(field.Options[i]));
                            }

                            FORM_ForceToKillFocus(form);
                        }

                        break;

                    case FormFieldKind.Choice when filled.Add(field.Name):
                        int option = field.Options.IndexOf(value);
                        if (option >= 0 && FORM_SetFocusedAnnot(form, annotation))
                        {
                            FORM_SetIndexSelected(form, page, option, true);
                            FORM_ForceToKillFocus(form);
                        }
                        else if (option < 0 && field.IsEditable && FORM_SetFocusedAnnot(form, annotation))
                        {
                            // A drop-down that takes typing (FS-964): the answer is typed into it.
                            FORM_SelectAllText(form, page);
                            FORM_ReplaceSelection(form, page, value);
                            FORM_ForceToKillFocus(form);
                        }

                        break;

                    // Buttons are clicked: checked state is read again each time, since a click on one widget
                    // changes the others in its field.
                    case FormFieldKind.CheckBox when FPDFAnnot_IsChecked(form, annotation) != (value != FieldOff):
                    case FormFieldKind.Radio when value == field.OnValue && !FPDFAnnot_IsChecked(form, annotation):
                        Click(form, page, field.Rect);
                        break;
                }
            }
            finally
            {
                FPDFPage_CloseAnnot(annotation);
            }
        }
    }

    /// <summary>
    /// Draws the page's form fields with their current values on top of a render (FS-950), the way PDF readers do:
    /// a plain page render leaves widgets to the form layer. Called with the lock held.
    /// </summary>
    private void DrawFormFields(nint page, nint bitmap, int width, int height, int rotate)
    {
        nint form = FormHandle();
        if (form == 0)
        {
            return;
        }

        FORM_OnAfterLoadPage(page, form);
        FPDF_FFLDraw(form, bitmap, page, 0, 0, width, height, rotate, FPDF_ANNOT);
        FORM_OnBeforeClosePage(page, form);
    }

    private static void Click(nint form, nint page, (double Left, double Bottom, double Right, double Top) rect)
    {
        double x = (rect.Left + rect.Right) / 2, y = (rect.Bottom + rect.Top) / 2;
        FORM_OnLButtonDown(form, page, 0, x, y);
        FORM_OnLButtonUp(form, page, 0, x, y);
        FORM_ForceToKillFocus(form);
    }

    private static FormField? ReadField(nint form, nint annotation)
    {
        if (FPDFAnnot_GetSubtype(annotation) != FPDF_ANNOT_WIDGET)
        {
            return null;
        }

        FormFieldKind? kind = FPDFAnnot_GetFormFieldType(form, annotation) switch
        {
            FPDF_FORMFIELD_TEXTFIELD => FormFieldKind.Text,
            FPDF_FORMFIELD_CHECKBOX => FormFieldKind.CheckBox,
            FPDF_FORMFIELD_RADIOBUTTON => FormFieldKind.Radio,
            FPDF_FORMFIELD_COMBOBOX or FPDF_FORMFIELD_LISTBOX => FormFieldKind.Choice,
            FPDF_FORMFIELD_SIGNATURE => FormFieldKind.Signature,
            FPDF_FORMFIELD_PUSHBUTTON => FormFieldKind.PushButton,
            _ => null,
        };
        if (kind is null || !FPDFAnnot_GetRect(annotation, out FsRectF rect))
        {
            return null;
        }

        string name = ReadString((buffer, bytes) => { unsafe { return FPDFAnnot_GetFormFieldName(form, annotation, (char*)buffer, bytes); } });
        if (name.Length == 0)
        {
            return null;
        }

        int flags = FPDFAnnot_GetFormFieldFlags(form, annotation);
        ImmutableArray<string> options = kind == FormFieldKind.Choice ? ReadOptions(form, annotation) : [];
        bool isButton = kind is FormFieldKind.CheckBox or FormFieldKind.Radio;
        bool isMultiSelect = kind == FormFieldKind.Choice && (flags & FPDF_FORMFLAG_CHOICE_MULTI_SELECT) != 0;
        string value = isMultiSelect
            ? JoinChoices(options.Where((_, index) => FPDFAnnot_IsOptionSelected(form, annotation, index)))
            : ReadString((buffer, bytes) => { unsafe { return FPDFAnnot_GetFormFieldValue(form, annotation, (char*)buffer, bytes); } });
        return new FormField(
            name,
            kind.Value,
            (Math.Min(rect.Left, rect.Right), Math.Min(rect.Top, rect.Bottom), Math.Max(rect.Left, rect.Right), Math.Max(rect.Top, rect.Bottom)),
            value,
            isButton && FPDFAnnot_IsChecked(form, annotation),
            isButton ? ReadString((buffer, bytes) => { unsafe { return FPDFAnnot_GetFormFieldExportValue(form, annotation, (char*)buffer, bytes); } }) : string.Empty,
            options,
            (flags & FPDF_FORMFLAG_READONLY) != 0,
            kind == FormFieldKind.Text && (flags & FPDF_FORMFLAG_TEXT_MULTILINE) != 0,
            kind == FormFieldKind.Choice && (flags & FPDF_FORMFLAG_CHOICE_COMBO) == 0,
            FPDFAnnot_GetFontSize(form, annotation, out float fontSize) ? fontSize : 0,
            isMultiSelect,
            kind == FormFieldKind.Text ? MaxLength(annotation) : 0,
            kind == FormFieldKind.Choice && (flags & FPDF_FORMFLAG_CHOICE_COMBO) != 0 && (flags & FPDF_FORMFLAG_CHOICE_EDIT) != 0,
            ReadString((buffer, bytes) => { unsafe { return FPDFAnnot_GetFormFieldAlternateName(form, annotation, (char*)buffer, bytes); } }),
            kind == FormFieldKind.Text && (flags & FPDF_FORMFLAG_TEXT_COMB) != 0 && MaxLength(annotation) > 0);
    }

    /// <summary>A text field's /MaxLen: on the widget when it is the field, else on the field above it.</summary>
    private static int MaxLength(nint annotation)
    {
        if (FPDFAnnot_GetNumberValue(annotation, "MaxLen", out float own))
        {
            return Math.Max(0, (int)own);
        }

        nint parent = FPDFAnnot_GetLinkedAnnot(annotation, "Parent");
        if (parent == 0)
        {
            return 0;
        }

        try
        {
            return FPDFAnnot_GetNumberValue(parent, "MaxLen", out float inherited) ? Math.Max(0, (int)inherited) : 0;
        }
        finally
        {
            FPDFPage_CloseAnnot(parent);
        }
    }

    private static ImmutableArray<string> ReadOptions(nint form, nint annotation)
    {
        int count = Math.Clamp(FPDFAnnot_GetOptionCount(form, annotation), 0, 1000);
        var options = ImmutableArray.CreateBuilder<string>(count);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            options.Add(ReadString((buffer, bytes) => { unsafe { return FPDFAnnot_GetOptionLabel(form, annotation, index, (char*)buffer, bytes); } }));
        }

        return options.MoveToImmutable();
    }

    /// <summary>Reads one of PDFium's UTF-16 strings: ask for the length, then fill a buffer of that size.</summary>
    private static string ReadString(Func<nint, CULong, CULong> read)
    {
        int bytes = checked((int)read(0, new CULong(0)).Value);
        if (bytes <= 2)
        {
            return string.Empty;
        }

        char[] buffer = new char[bytes / 2];
        unsafe
        {
            fixed (char* start = buffer)
            {
                // The buffer's own length in bytes: an odd count from PDFium would otherwise claim one byte too many.
                read((nint)start, new CULong((nuint)(buffer.Length * sizeof(char))));
            }
        }

        return new string(buffer, 0, buffer.Length - 1);
    }

    /// <summary>The form environment, made the first time it's needed; 0 when the document has no form.</summary>
    private nint FormHandle()
    {
        ObjectDisposedException.ThrowIf(_document == 0, this);
        if (_form != 0)
        {
            return _form;
        }

        unsafe
        {
            _formInfo = (nint)NativeMemory.AllocZeroed(FormFillInfoBytes);
            *(int*)_formInfo = 1;
        }

        _form = FPDFDOC_InitFormFillEnvironment(_document, _formInfo);
        return _form;
    }

    /// <summary>Ends the form environment before the document closes, as PDFium requires. Called with the lock held.</summary>
    private void CloseForm()
    {
        if (_form != 0)
        {
            FPDFDOC_ExitFormFillEnvironment(_form);
            _form = 0;
        }

        if (_formInfo != 0)
        {
            unsafe
            {
                NativeMemory.Free((void*)_formInfo);
            }

            _formInfo = 0;
        }
    }
}
