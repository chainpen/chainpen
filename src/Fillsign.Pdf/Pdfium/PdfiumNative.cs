using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fillsign.Pdf.Pdfium;

/// <summary>
/// Hand-written P/Invoke binding for the PDFium functions Fillsign uses.
/// The native library comes from the bblanchon.PDFium.* packages, which place it under
/// runtimes/&lt;rid&gt;/native so the runtime resolves "pdfium" on every OS.
/// </summary>
/// <remarks>
/// PDFium is not thread-safe. Every call must be made while holding <see cref="PdfiumLibrary.Sync"/>
/// (Phase 2 replaces this with a single dedicated render thread).
/// Signatures follow fpdfview.h / fpdf_edit.h / fpdf_transformpage.h from the same package version.
/// </remarks>
internal static partial class PdfiumNative
{
    private const string Lib = "pdfium";

    public const int FPDFBitmap_BGRA = 4;

    // Render flags (fpdfview.h).
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;

    // FPDF_GetLastError codes.
    public const uint FPDF_ERR_SUCCESS = 0;
    public const uint FPDF_ERR_UNKNOWN = 1;
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;
    public const uint FPDF_ERR_PAGE = 6;

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_InitLibrary();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_DestroyLibrary();

    // unsigned long: 32-bit on Windows, 64-bit on Unix.
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDF_GetLastError();

    /// <summary>The buffer must stay valid until <see cref="FPDF_CloseDocument"/>.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDF_LoadMemDocument64(nint dataBuf, nuint size, string? password);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_ClosePage(nint page);

    /// <summary>Displayed width in points (crop box, with /Rotate applied).</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial float FPDF_GetPageHeightF(nint page);

    /// <summary>Page /Rotate as quarter turns clockwise (0–3).</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPage_GetRotation(nint page);

    /// <summary>
    /// The box the page is laid out in: its crop box cut to its media box, whether they're on the page or inherited
    /// from the page tree. The two calls below read only the page's own dictionary.
    /// </summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_GetPageBoundingBox(nint page, out FsRectF rect);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_GetMediaBox(nint page, out float left, out float bottom, out float right, out float top);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_GetCropBox(nint page, out float left, out float bottom, out float right, out float top);

    /// <summary>Wraps caller-owned memory when <paramref name="firstScan"/> is non-zero.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // FPDF_SaveAsCopy flags (fpdf_save.h).
    public const uint FPDF_INCREMENTAL = 1;
    public const uint FPDF_NO_INCREMENTAL = 2;
    public const uint FPDF_REMOVE_SECURITY = 3;

    /// <summary>
    /// FPDF_FILEWRITE with a trailing field for our own state; PDFium passes the struct pointer
    /// back to <see cref="WriteBlock"/>, so the callback can recover the target stream.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FileWrite
    {
        public int Version;
        public delegate* unmanaged[Cdecl]<FileWrite*, void*, CULong, int> WriteBlock;
        public nint State;
    }

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FPDF_SaveAsCopy(nint document, FileWrite* fileWrite, uint flags);

    // ---- Editing (fpdf_edit.h, fpdf_ppo.h, fpdf_attachment.h, fpdf_text.h, fpdf_annot.h) ----

    public const int FPDF_FONT_TRUETYPE = 2;
    public const int FPDF_FILLMODE_NONE = 0;
    public const int FPDF_FILLMODE_WINDING = 2;

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDF_CreateNewDocument();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPage_New(nint document, int pageIndex, double width, double height);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFPage_Delete(nint document, int pageIndex);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FPDF_MovePages(nint document, int* pageIndices, CULong count, int destPageIndex);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FPDF_ImportPagesByIndex(nint destDocument, nint sourceDocument, int* pageIndices, CULong count, int index);

    /// <summary>Sets /Rotate in quarter turns clockwise (0–3).</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFPage_SetRotation(nint page, int rotate);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFPage_SetCropBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFPage_SetMediaBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_GenerateContent(nint page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_InsertObject(nint page, nint pageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPage_CountObjects(nint page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFPageObj_Destroy(nint pageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFPageObj_Transform(nint pageObject, double a, double b, double c, double d, double e, double f);

    // Line caps and joins (fpdf_edit.h): 1 is round for both.
    public const int FPDF_LINECAP_ROUND = 1;
    public const int FPDF_LINEJOIN_ROUND = 1;

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetLineCap(nint pageObject, int lineCap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetLineJoin(nint pageObject, int lineJoin);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetFillColor(nint pageObject, uint r, uint g, uint b, uint a);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetStrokeColor(nint pageObject, uint r, uint g, uint b, uint a);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetStrokeWidth(nint pageObject, float width);

    /// <summary>Text render mode (PDF Tr): 0 fill, 2 fill then stroke.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFTextObj_SetTextRenderMode(nint textObject, int renderMode);

    public const int TextRenderFillStroke = 2;

    /// <summary>Blend mode name, e.g. "Multiply" (PDF ExtGState /BM).</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFPageObj_SetBlendMode(nint pageObject, string blendMode);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPageObj_CreateNewPath(float x, float y);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPageObj_CreateNewRect(float x, float y, float w, float h);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_MoveTo(nint path, float x, float y);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_LineTo(nint path, float x, float y);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_BezierTo(nint path, float x1, float y1, float x2, float y2, float x3, float y3);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_Close(nint path);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_SetDrawMode(nint path, int fillMode, [MarshalAs(UnmanagedType.Bool)] bool stroke);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPageObj_NewImageObj(nint document);

    /// <summary>FPDF_FILEACCESS: PDFium reads a file through <see cref="GetBlock"/>, with <see cref="Param"/> passed back.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PdfFileAccess
    {
        public CULong FileLength;
        public delegate* unmanaged[Cdecl]<void*, CULong, byte*, CULong, int> GetBlock;
        public void* Param;
    }

    /// <summary>Embeds JPEG data as-is (no re-encoding); the data is copied before the call returns.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FPDFImageObj_LoadJpegFileInline(nint* pages, int count, nint imageObject, PdfFileAccess* file);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFImageObj_SetBitmap(nint pages, int count, nint imageObject, nint bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFImageObj_SetMatrix(nint imageObject, double a, double b, double c, double d, double e, double f);

    /// <summary>Embeds the given font bytes (whole file: subset them first). With cid, ToUnicode is generated.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial nint FPDFText_LoadFont(nint document, byte* data, uint size, int fontType, [MarshalAs(UnmanagedType.Bool)] bool cid);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFFont_Close(nint font);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFFont_GetAscent(nint font, float fontSize, out float ascent);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPageObj_CreateTextObj(nint document, nint font, float fontSize);

    /// <summary>Text is UTF-16LE (FPDF_WIDESTRING).</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFText_SetText(nint textObject, string text);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFText_CountChars(nint textPage);

    /// <summary>Writes count + 1 UTF-16 units (including the terminator).</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial int FPDFText_GetText(nint textPage, int startIndex, int count, char* result);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPage_GetAnnotCount(nint page);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFDoc_AddAttachment(nint document, string name);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FPDFAttachment_SetFile(nint attachment, nint document, byte* contents, CULong length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFDoc_GetAttachmentCount(nint document);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFDoc_GetAttachment(nint document, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFDoc_DeleteAttachment(nint document, int index);

    /// <summary>Returns the byte length of the UTF-16 name including the terminator.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial CULong FPDFAttachment_GetName(nint attachment, char* buffer, CULong bufferLength);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FPDFAttachment_GetFile(nint attachment, byte* buffer, CULong bufferLength, out CULong outLength);
}
