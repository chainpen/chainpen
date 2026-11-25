using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Chainpen.Pdf.Pdfium;

/// <summary>Reading and removing a page's existing objects (fpdf_edit.h), used to redact under whiteout.</summary>
internal static partial class PdfiumNative
{
    // FPDFPageObj_GetType.
    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_PAGEOBJ_PATH = 2;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_SHADING = 4;
    public const int FPDF_PAGEOBJ_FORM = 5;

    // FPDFPathSegment_GetType.
    public const int FPDF_SEGMENT_MOVETO = 2;

    // FPDFBitmap_GetFormat.
    public const int FPDFBitmap_Gray = 1;
    public const int FPDFBitmap_BGR = 2;
    public const int FPDFBitmap_BGRx = 3;

    /// <summary>FS_MATRIX: maps an object's own space to page space.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FsMatrix
    {
        public float A;
        public float B;
        public float C;
        public float D;
        public float E;
        public float F;
    }

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPage_GetObject(nint page, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_RemoveObject(nint page, nint pageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPageObj_GetType(nint pageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetBounds(nint pageObject, out float left, out float bottom, out float right, out float top);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetMatrix(nint pageObject, out FsMatrix matrix);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPath_CountSegments(nint path);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPath_GetPathSegment(nint path, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPathSegment_GetPoint(nint segment, out float x, out float y);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPathSegment_GetType(nint segment);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPathSegment_GetClose(nint segment);

    /// <summary>The image's own pixels, without its soft mask. The caller destroys the bitmap.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFImageObj_GetBitmap(nint imageObject);

    /// <summary>
    /// The image drawn by itself with its own mask, at the size its matrix gives it; only an upright matrix works.
    /// The caller destroys the bitmap.
    /// </summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFImageObj_GetRenderedBitmap(nint document, nint page, nint imageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFBitmap_GetFormat(nint bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFBitmap_GetWidth(nint bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFBitmap_GetHeight(nint bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    /// <summary>FPDF_IMAGEOBJ_METADATA.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ImageMetadata
    {
        public uint Width;
        public uint Height;
        public float HorizontalDpi;
        public float VerticalDpi;
        public uint BitsPerPixel;
        public int Colorspace;
        public int MarkedContentId;
    }

    // FPDF_COLORSPACE_UNKNOWN: no colour space, as with an image mask (a stencil painted in the fill colour).
    public const int FPDF_COLORSPACE_UNKNOWN = 0;

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFImageObj_GetImageMetadata(nint imageObject, nint page, out ImageMetadata metadata);

    /// <summary>Whether the object is drawn with transparency: a soft mask, blending or partial opacity.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_HasTransparency(nint pageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_GetDrawMode(nint path, out int fillMode, [MarshalAs(UnmanagedType.Bool)] out bool stroke);

    // FPDFPage_Flatten: draw annotations as they print.
    public const int FLAT_PRINT = 1;
    public const int FLATTEN_FAIL = 0;

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPage_Flatten(nint page, int flag);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFPage_CloseAnnot(nint annotation);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_GetRect(nint annotation, out FsRectF rect);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_RemoveAnnot(nint page, int index);

    /// <summary>The permission flags (/P) the document was opened with; all set when it isn't encrypted or the owner password was given.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDF_GetDocPermissions(nint document);

    /// <summary>-1 when the document isn't encrypted.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_GetSecurityHandlerRevision(nint document);

    // /P bit 4: modify the contents.
    public const uint PermissionModify = 0x8;
}
