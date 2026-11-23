using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Chainpen.Pdf.Pdfium;

/// <summary>Interactive form fields (fpdf_formfill.h, fpdf_annot.h), for filling a PDF's own fields (FS-950).</summary>
internal static partial class PdfiumNative
{
    public const int FPDF_ANNOT_WIDGET = 20;

    // FPDFAnnot_GetFormFieldType.
    public const int FPDF_FORMFIELD_PUSHBUTTON = 1;
    public const int FPDF_FORMFIELD_CHECKBOX = 2;
    public const int FPDF_FORMFIELD_RADIOBUTTON = 3;
    public const int FPDF_FORMFIELD_COMBOBOX = 4;
    public const int FPDF_FORMFIELD_LISTBOX = 5;
    public const int FPDF_FORMFIELD_TEXTFIELD = 6;
    public const int FPDF_FORMFIELD_SIGNATURE = 7;

    // Field flags (/Ff): read-only for every field, multi-line for text.
    public const int FPDF_FORMFLAG_READONLY = 1 << 0;
    public const int FPDF_FORMFLAG_TEXT_MULTILINE = 1 << 12;
    public const int FPDF_FORMFLAG_TEXT_COMB = 1 << 24;
    public const int FPDF_FORMFLAG_CHOICE_COMBO = 1 << 17;
    public const int FPDF_FORMFLAG_CHOICE_EDIT = 1 << 18;
    public const int FPDF_FORMFLAG_CHOICE_MULTI_SELECT = 1 << 21;

    /// <summary>
    /// FPDF_FORMFILLINFO version 1: the version number, then callback pointers PDFium checks for null before calling.
    /// Chainpen needs none of them, so the struct is all zeros after the version. The size is generous on purpose.
    /// </summary>
    public const int FormFillInfoBytes = 64 * 8;

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFDOC_InitFormFillEnvironment(nint document, nint formInfo);

    // XFA packets (fpdfview.h): the XML some forms carry beside their fields, read here only for field descriptions.
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_GetXFAPacketCount(nint document);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial CULong FPDF_GetXFAPacketName(nint document, int index, byte* buffer, CULong bufferLength);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FPDF_GetXFAPacketContent(nint document, int index, byte* buffer, CULong bufferLength, out CULong outLength);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFDOC_ExitFormFillEnvironment(nint form);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FORM_OnAfterLoadPage(nint page, nint form);

    /// <summary>Draws form fields over a page render (after FPDF_RenderPageBitmap), with the same placement.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_FFLDraw(nint form, nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FORM_OnBeforeClosePage(nint page, nint form);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_SetFocusedAnnot(nint form, nint annotation);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_ForceToKillFocus(nint form);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_SelectAllText(nint form, nint page);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FORM_ReplaceSelection(nint form, nint page, string text);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_SetIndexSelected(nint form, nint page, int index, [MarshalAs(UnmanagedType.Bool)] bool selected);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnLButtonDown(nint form, nint page, int modifier, double pageX, double pageY);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnLButtonUp(nint form, nint page, int modifier, double pageX, double pageY);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetSubtype(nint annotation);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetFormFieldType(nint form, nint annotation);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetFormFieldFlags(nint form, nint annotation);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_SetFormFieldFlags(nint form, nint annotation, int flags);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial CULong FPDFAnnot_GetFormFieldName(nint form, nint annotation, char* buffer, CULong bufferBytes);

    /// <summary>The field's description for people (/TU), shown as its tooltip in readers.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial CULong FPDFAnnot_GetFormFieldAlternateName(nint form, nint annotation, char* buffer, CULong bufferBytes);

    /// <summary>A number from the annotation's own dictionary, e.g. "MaxLen".</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_GetNumberValue(nint annotation, string key, out float value);

    /// <summary>An annotation linked from this one under a key, e.g. the field above a widget ("Parent").</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFAnnot_GetLinkedAnnot(nint annotation, string key);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial CULong FPDFAnnot_GetFormFieldValue(nint form, nint annotation, char* buffer, CULong bufferBytes);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial CULong FPDFAnnot_GetFormFieldExportValue(nint form, nint annotation, char* buffer, CULong bufferBytes);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_IsChecked(nint form, nint annotation);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetOptionCount(nint form, nint annotation);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_IsOptionSelected(nint form, nint annotation, int index);

    /// <summary>The font size the field's text is drawn at; 0 means it's sized to fit.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_GetFontSize(nint form, nint annotation, out float size);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial CULong FPDFAnnot_GetOptionLabel(nint form, nint annotation, int index, char* buffer, CULong bufferBytes);
}
