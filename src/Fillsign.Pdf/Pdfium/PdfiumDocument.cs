using System.Runtime.InteropServices;
using static Fillsign.Pdf.Pdfium.PdfiumNative;

namespace Fillsign.Pdf.Pdfium;

/// <summary>Process-wide PDFium initialisation and the lock that serialises all PDFium calls.</summary>
public static class PdfiumLibrary
{
    /// <summary>PDFium is not thread-safe: hold this lock for every native call.</summary>
    public static readonly Lock Sync = new();

    private static bool s_initialised;

    internal static void EnsureInitialised()
    {
        lock (Sync)
        {
            if (s_initialised)
            {
                return;
            }

            FPDF_InitLibrary();
            s_initialised = true;
        }
    }
}

public enum PdfOpenError
{
    Unknown,
    File,
    Format,
    Password,
    UnsupportedSecurity,
}

public sealed class PdfOpenException(PdfOpenError error, string message) : Exception(message)
{
    public PdfOpenError Error { get; } = error;
}

/// <summary>A single page can't be loaded (for example, a broken page tree). The rest of the document may still be usable.</summary>
public sealed class PdfPageException(int pageIndex) : Exception($"Page {pageIndex + 1} is damaged and can't be shown.")
{
    public int PageIndex { get; } = pageIndex;
}

/// <summary>Displayed page geometry in PDF points.</summary>
/// <param name="Width">Displayed width (crop box with /Rotate applied).</param>
/// <param name="Height">Displayed height.</param>
/// <param name="Rotation">The page's own /Rotate in quarter turns clockwise (0–3).</param>
/// <param name="CropBox">Crop box in unrotated PDF user space: left, bottom, right, top.</param>
public readonly record struct PdfPageInfo(float Width, float Height, int Rotation, (float Left, float Bottom, float Right, float Top) CropBox);

/// <summary>An open PDF, loaded from a private copy of the file bytes (the original file is never touched).</summary>
public sealed partial class PdfiumDocument : IDisposable
{
    private nint _document;
    private nint _buffer;

    private PdfiumDocument(nint document, nint buffer)
    {
        _document = document;
        _buffer = buffer;
    }

    public int PageCount
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                ObjectDisposedException.ThrowIf(_document == 0, this);
                return FPDF_GetPageCount(_document);
            }
        }
    }

    public static PdfiumDocument Open(ReadOnlySpan<byte> pdf, string? password = null)
    {
        PdfiumLibrary.EnsureInitialised();

        // PDFium reads lazily from the buffer, so it must outlive the document.
        nint buffer = (nint)0;
        unsafe
        {
            buffer = (nint)NativeMemory.Alloc((nuint)Math.Max(pdf.Length, 1));
            pdf.CopyTo(new Span<byte>((void*)buffer, pdf.Length));
        }

        lock (PdfiumLibrary.Sync)
        {
            nint document = FPDF_LoadMemDocument64(buffer, (nuint)pdf.Length, password);
            if (document == 0)
            {
                uint code = (uint)FPDF_GetLastError().Value;
                FreeBuffer(buffer);
                throw ToException(code);
            }

            return new PdfiumDocument(document, buffer);
        }
    }

    public PdfPageInfo GetPageInfo(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            nint page = LoadPage(pageIndex);
            try
            {
                if (!FPDFPage_GetCropBox(page, out float l, out float b, out float r, out float t)
                    && !FPDFPage_GetMediaBox(page, out l, out b, out r, out t))
                {
                    // PDFium defaults a missing MediaBox to US Letter.
                    (l, b, r, t) = (0, 0, 612, 792);
                }

                return new PdfPageInfo(FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page), FPDFPage_GetRotation(page), (l, b, r, t));
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Renders a page into a caller-owned BGRA (premultiplied, opaque) buffer on a white background.
    /// </summary>
    /// <param name="extraQuarterTurns">Rotation on top of the page's own /Rotate, clockwise (0–3).</param>
    public void RenderPage(int pageIndex, Span<byte> bgra, int pixelWidth, int pixelHeight, int stride, int extraQuarterTurns = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, pixelWidth * 4);
        if (bgra.Length < stride * pixelHeight)
        {
            throw new ArgumentException("Buffer is smaller than stride × height.", nameof(bgra));
        }

        unsafe
        {
            fixed (byte* pixels = bgra)
            {
                lock (PdfiumLibrary.Sync)
                {
                    nint page = LoadPage(pageIndex);
                    nint bitmap = FPDFBitmap_CreateEx(pixelWidth, pixelHeight, FPDFBitmap_BGRA, (nint)pixels, stride);
                    try
                    {
                        if (bitmap == 0)
                        {
                            throw new InvalidOperationException("PDFium could not create a bitmap.");
                        }

                        FPDFBitmap_FillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
                        FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixelWidth, pixelHeight, ((extraQuarterTurns % 4) + 4) % 4, FPDF_ANNOT);
                    }
                    finally
                    {
                        if (bitmap != 0)
                        {
                            FPDFBitmap_Destroy(bitmap);
                        }

                        FPDF_ClosePage(page);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Re-serialises the document as PDFium understands it: damaged cross-reference tables are rebuilt and,
    /// when <paramref name="removeSecurity"/> is set, encryption is removed. Used to normalise files that
    /// the writer can't open directly.
    /// </summary>
    public byte[] SaveCopy(bool removeSecurity)
    {
        using var output = new MemoryStream();
        var handle = GCHandle.Alloc(output);
        try
        {
            unsafe
            {
                var writer = new FileWrite { Version = 1, WriteBlock = &WriteBlock, State = GCHandle.ToIntPtr(handle) };
                lock (PdfiumLibrary.Sync)
                {
                    ObjectDisposedException.ThrowIf(_document == 0, this);
                    if (!FPDF_SaveAsCopy(_document, &writer, removeSecurity ? FPDF_REMOVE_SECURITY : FPDF_NO_INCREMENTAL))
                    {
                        throw new InvalidOperationException("PDFium could not save a copy of the document.");
                    }
                }
            }
        }
        finally
        {
            handle.Free();
        }

        return output.ToArray();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe int WriteBlock(FileWrite* self, void* data, CULong size)
    {
        try
        {
            var stream = (MemoryStream)GCHandle.FromIntPtr(self->State).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
            return 1;
        }
        catch (Exception)
        {
            // Exceptions must not cross into native code; returning 0 makes PDFium fail the save.
            return 0;
        }
    }

    public void Dispose()
    {
        lock (PdfiumLibrary.Sync)
        {
            if (_document != 0)
            {
                CloseFonts();
                FPDF_CloseDocument(_document);
                _document = 0;
            }
        }

        FreeBuffer(_buffer);
        _buffer = 0;
    }

    private nint LoadPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_document == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, FPDF_GetPageCount(_document));

        nint page = FPDF_LoadPage(_document, pageIndex);
        return page != 0 ? page : throw new PdfPageException(pageIndex);
    }

    private static unsafe void FreeBuffer(nint buffer)
    {
        if (buffer != 0)
        {
            NativeMemory.Free((void*)buffer);
        }
    }

    private static PdfOpenException ToException(uint code) => code switch
    {
        FPDF_ERR_FILE => new(PdfOpenError.File, "The file could not be read."),
        FPDF_ERR_FORMAT => new(PdfOpenError.Format, "The file is not a PDF or is too badly damaged to open."),
        FPDF_ERR_PASSWORD => new(PdfOpenError.Password, "The PDF is password-protected."),
        FPDF_ERR_SECURITY => new(PdfOpenError.UnsupportedSecurity, "The PDF uses an unsupported security handler."),
        _ => new(PdfOpenError.Unknown, $"PDFium could not open the file (error {code})."),
    };
}

/// <summary>Converts PDFium's page facts into the Core model.</summary>
public static class PdfPageInfoExtensions
{
    public static Fillsign.Core.Model.SourcePage ToSourcePage(this PdfPageInfo info) =>
        new(
            new Fillsign.Core.Model.PdfBox(info.CropBox.Left, info.CropBox.Bottom, info.CropBox.Right, info.CropBox.Top),
            Fillsign.Core.Model.Rotation.FromQuarterTurns(info.Rotation));
}
