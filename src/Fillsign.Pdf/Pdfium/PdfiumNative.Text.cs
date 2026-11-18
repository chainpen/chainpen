using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fillsign.Pdf.Pdfium;

/// <summary>Where each character sits on a page (fpdf_text.h), used to find boxes and blanks to fill.</summary>
internal static partial class PdfiumNative
{
    /// <summary>FS_RECTF.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FsRectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint FPDFText_GetUnicode(nint textPage, int index);

    /// <summary>1 for characters PDFium made up (spaces and line breaks between words), 0 for real ones, -1 on error.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFText_IsGenerated(nint textPage, int index);

    /// <summary>The character's box from its font's ascent and descent, so every character on a line is as tall.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFText_GetLooseCharBox(nint textPage, int index, out FsRectF rect);
}
