using System.Runtime.InteropServices;
using Fillsign.Pdf.Export;
using static Fillsign.Pdf.Pdfium.PdfiumNative;

namespace Fillsign.Pdf.Pdfium;

/// <summary>A font loaded into one document. Valid until the document is disposed.</summary>
public readonly record struct PdfiumFont(nint Handle);

/// <summary>
/// Writing through PDFium's editing API (FS-024 spike, the basis for FS-607). Coordinates are PDF user space:
/// points from the bottom-left of the unrotated page; converting from Fillsign's display coordinates is
/// PageTransform's job (FS-102).
/// </summary>
public sealed partial class PdfiumDocument
{
    private readonly List<nint> _fonts = [];

    public static PdfiumDocument CreateNew()
    {
        PdfiumLibrary.EnsureInitialised();
        lock (PdfiumLibrary.Sync)
        {
            nint document = FPDF_CreateNewDocument();
            return document != 0 ? new PdfiumDocument(document, 0) : throw new InvalidOperationException("PDFium could not create a document.");
        }
    }

    public void AddBlankPage(int index, double width, double height) => WithLock(() =>
    {
        nint page = FPDFPage_New(_document, index, width, height);
        if (page == 0)
        {
            throw new InvalidOperationException("PDFium could not add a page.");
        }

        FPDF_ClosePage(page);
    });

    /// <summary>Moves the given pages, in the given order, so the first lands at <paramref name="destination"/>.</summary>
    public void MovePages(int[] pageIndices, int destination) => WithLock(() =>
    {
        unsafe
        {
            fixed (int* indices = pageIndices)
            {
                if (!FPDF_MovePages(_document, indices, new CULong((nuint)pageIndices.Length), destination))
                {
                    throw new InvalidOperationException("PDFium could not move the pages.");
                }
            }
        }
    });

    public void DeletePage(int pageIndex) => WithLock(() => FPDFPage_Delete(_document, pageIndex));

    /// <summary>Sets the page's /Rotate in quarter turns clockwise.</summary>
    public void SetRotation(int pageIndex, int quarterTurns) => WithPage(pageIndex, page => FPDFPage_SetRotation(page, ((quarterTurns % 4) + 4) % 4));

    /// <summary>Sets the visible area of a page, in PDF user space.</summary>
    public void SetCropBox(int pageIndex, float left, float bottom, float right, float top) =>
        WithPage(pageIndex, page => FPDFPage_SetCropBox(page, left, bottom, right, top));

    public void ImportPages(PdfiumDocument source, int[] pageIndices, int insertAt) => WithLock(() =>
    {
        unsafe
        {
            fixed (int* indices = pageIndices)
            {
                if (!FPDF_ImportPagesByIndex(_document, source._document, indices, new CULong((nuint)pageIndices.Length), insertAt))
                {
                    throw new InvalidOperationException("PDFium could not import the pages.");
                }
            }
        }
    });

    /// <summary>Embeds a subset of <paramref name="fontFile"/> covering <paramref name="characters"/>.</summary>
    public PdfiumFont LoadSubsetFont(ReadOnlySpan<byte> fontFile, string characters)
    {
        var codepoints = new HashSet<int>();
        foreach (var rune in characters.EnumerateRunes())
        {
            codepoints.Add(rune.Value);
        }

        byte[] subset = FontSubsetter.Subset(fontFile, codepoints);
        return WithLock(() =>
        {
            unsafe
            {
                fixed (byte* data = subset)
                {
                    nint font = FPDFText_LoadFont(_document, data, (uint)subset.Length, FPDF_FONT_TRUETYPE, cid: true);
                    if (font == 0)
                    {
                        throw new InvalidOperationException("PDFium could not load the font.");
                    }

                    _fonts.Add(font);
                    return new PdfiumFont(font);
                }
            }
        });
    }

    public float GetAscent(PdfiumFont font, float fontSize) =>
        WithLock(() => FPDFFont_GetAscent(font.Handle, fontSize, out float ascent) ? ascent : throw new InvalidOperationException("No ascent."));

    /// <summary>Adds objects to a page, then regenerates its content stream once.</summary>
    public void EditPage(int pageIndex, Action<PdfiumPageEditor> edit) => WithPage(pageIndex, page =>
    {
        edit(new PdfiumPageEditor(_document, page));
        if (!FPDFPage_GenerateContent(page))
        {
            throw new InvalidOperationException("PDFium could not regenerate the page content.");
        }
    });

    public int GetAnnotationCount(int pageIndex) => WithPage(pageIndex, FPDFPage_GetAnnotCount);

    public int GetObjectCount(int pageIndex) => WithPage(pageIndex, FPDFPage_CountObjects);

    /// <summary>The page's extractable text: what search and copy in a PDF reader will see.</summary>
    public string GetText(int pageIndex) => WithPage(pageIndex, page =>
    {
        nint textPage = FPDFText_LoadPage(page);
        try
        {
            int count = FPDFText_CountChars(textPage);
            var buffer = new char[count + 1];
            unsafe
            {
                fixed (char* chars = buffer)
                {
                    int written = FPDFText_GetText(textPage, 0, count, chars);
                    return new string(buffer, 0, Math.Max(0, written - 1));
                }
            }
        }
        finally
        {
            FPDFText_ClosePage(textPage);
        }
    });

    /// <summary>Stores <paramref name="contents"/> as an embedded file, replacing any with the same name.</summary>
    public void SetAttachment(string name, ReadOnlySpan<byte> contents)
    {
        int existing = FindAttachment(name);
        unsafe
        {
            fixed (byte* data = contents)
            {
                byte* pointer = data;
                int length = contents.Length;
                WithLock(() =>
                {
                    if (existing >= 0 && !FPDFDoc_DeleteAttachment(_document, existing))
                    {
                        throw new InvalidOperationException("PDFium could not remove the old attachment.");
                    }

                    nint attachment = FPDFDoc_AddAttachment(_document, name);
                    if (attachment == 0 || !FPDFAttachment_SetFile(attachment, _document, pointer, new CULong((nuint)length)))
                    {
                        throw new InvalidOperationException("PDFium could not store the attachment.");
                    }
                });
            }
        }
    }

    public byte[]? GetAttachment(string name)
    {
        int index = FindAttachment(name);
        if (index < 0)
        {
            return null;
        }

        return WithLock(() =>
        {
            nint attachment = FPDFDoc_GetAttachment(_document, index);
            unsafe
            {
                if (!FPDFAttachment_GetFile(attachment, null, new CULong(0), out CULong size))
                {
                    return null;
                }

                var bytes = new byte[(int)size.Value];
                fixed (byte* buffer = bytes)
                {
                    return FPDFAttachment_GetFile(attachment, buffer, size, out _) ? bytes : null;
                }
            }
        });
    }

    public IReadOnlyList<string> GetAttachmentNames() => WithLock(() =>
    {
        var names = new List<string>();
        int count = FPDFDoc_GetAttachmentCount(_document);
        for (int i = 0; i < count; i++)
        {
            names.Add(ReadAttachmentName(FPDFDoc_GetAttachment(_document, i)));
        }

        return (IReadOnlyList<string>)names;
    });

    private int FindAttachment(string name)
    {
        var names = GetAttachmentNames();
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static unsafe string ReadAttachmentName(nint attachment)
    {
        ulong bytes = FPDFAttachment_GetName(attachment, null, new CULong(0)).Value;
        if (bytes <= 2)
        {
            return "";
        }

        var buffer = new char[(int)(bytes / 2)];
        fixed (char* chars = buffer)
        {
            FPDFAttachment_GetName(attachment, chars, new CULong((nuint)bytes));
        }

        return new string(buffer, 0, buffer.Length - 1);
    }

    private void CloseFonts()
    {
        foreach (nint font in _fonts)
        {
            FPDFFont_Close(font);
        }

        _fonts.Clear();
    }

    private T WithPage<T>(int pageIndex, Func<nint, T> action) => WithLock(() =>
    {
        nint page = LoadPage(pageIndex);
        try
        {
            return action(page);
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    });

    private void WithPage(int pageIndex, Action<nint> action) => WithPage(pageIndex, page =>
    {
        action(page);
        return 0;
    });

    private T WithLock<T>(Func<T> action)
    {
        lock (PdfiumLibrary.Sync)
        {
            ObjectDisposedException.ThrowIf(_document == 0, this);
            return action();
        }
    }

    private void WithLock(Action action) => WithLock(() =>
    {
        action();
        return 0;
    });
}

/// <summary>Creates page objects on one open page. Colours are 0–255 RGBA.</summary>
public sealed class PdfiumPageEditor
{
    private readonly nint _document;
    private readonly nint _page;

    internal PdfiumPageEditor(nint document, nint page)
    {
        _document = document;
        _page = page;
    }

    /// <param name="x">Left edge of the text.</param>
    /// <param name="baseline">Baseline, in PDF user space (y up).</param>
    public void AddText(PdfiumFont font, float fontSize, string text, double x, double baseline, (byte R, byte G, byte B) color) =>
        AddText(font, fontSize, text, (1, 0, 0, 1, x, baseline), color);

    /// <summary>Adds text placed by a matrix that maps text space (origin on the baseline, y up) to user space.</summary>
    public void AddText(PdfiumFont font, float fontSize, string text, (double A, double B, double C, double D, double E, double F) matrix, (byte R, byte G, byte B) color)
    {
        nint obj = FPDFPageObj_CreateTextObj(_document, font.Handle, fontSize);
        Check(obj != 0 && FPDFText_SetText(obj, text), "create text");
        FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, 255);
        FPDFPageObj_Transform(obj, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
        Insert(obj);
    }

    /// <summary>Adds a path built by <paramref name="build"/>. Pass a fill colour, a stroke, or both.</summary>
    public void AddPath(
        float startX,
        float startY,
        Action<PathBuilder> build,
        (byte R, byte G, byte B, byte A)? fill = null,
        (byte R, byte G, byte B, byte A)? stroke = null,
        float strokeWidth = 1,
        string? blendMode = null)
    {
        nint obj = FPDFPageObj_CreateNewPath(startX, startY);
        Check(obj != 0, "create path");
        build(new PathBuilder(obj));
        Style(obj, fill, stroke, strokeWidth, blendMode);
        Check(FPDFPath_SetDrawMode(obj, fill is null ? FPDF_FILLMODE_NONE : FPDF_FILLMODE_WINDING, stroke is not null), "set draw mode");
        Insert(obj);
    }

    /// <summary>Places a BGRA (premultiplied is fine for opaque; alpha becomes a soft mask) image into a rectangle.</summary>
    public void AddImage(ReadOnlySpan<byte> bgra, int pixelWidth, int pixelHeight, double x, double y, double width, double height) =>
        AddImage(bgra, pixelWidth, pixelHeight, (width, 0, 0, height, x, y));

    /// <summary>
    /// Places a BGRA image (straight alpha; alpha becomes a soft mask) with a matrix mapping the unit square, y up, to
    /// user space.
    /// </summary>
    public void AddImage(ReadOnlySpan<byte> bgra, int pixelWidth, int pixelHeight, (double A, double B, double C, double D, double E, double F) matrix)
    {
        nint obj = FPDFPageObj_NewImageObj(_document);
        Check(obj != 0, "create image");
        unsafe
        {
            fixed (byte* pixels = bgra)
            {
                nint bitmap = FPDFBitmap_CreateEx(pixelWidth, pixelHeight, FPDFBitmap_BGRA, (nint)pixels, pixelWidth * 4);
                try
                {
                    Check(bitmap != 0 && FPDFImageObj_SetBitmap(0, 0, obj, bitmap), "set image bitmap");
                }
                finally
                {
                    if (bitmap != 0)
                    {
                        FPDFBitmap_Destroy(bitmap);
                    }
                }
            }
        }

        Check(FPDFImageObj_SetMatrix(obj, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F), "place image");
        Insert(obj);
    }

    /// <summary>Places JPEG data, embedded without re-encoding, into a rectangle in PDF user space.</summary>
    public void AddJpeg(ReadOnlySpan<byte> jpeg, double x, double y, double width, double height)
    {
        nint obj = FPDFPageObj_NewImageObj(_document);
        Check(obj != 0, "create image");
        unsafe
        {
            fixed (byte* data = jpeg)
            {
                var access = new PdfFileAccess { FileLength = new CULong((nuint)jpeg.Length), GetBlock = &GetJpegBlock, Param = data };
                if (!FPDFImageObj_LoadJpegFileInline(null, 0, obj, &access))
                {
                    FPDFPageObj_Destroy(obj);
                    throw new InvalidOperationException("PDFium could not read the JPEG image.");
                }
            }
        }

        Check(FPDFImageObj_SetMatrix(obj, width, 0, 0, height, x, y), "place image");
        Insert(obj);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe int GetJpegBlock(void* param, CULong position, byte* buffer, CULong size)
    {
        // Param is the pinned JPEG data; PDFium only asks for ranges inside FileLength.
        Buffer.MemoryCopy((byte*)param + position.Value, buffer, size.Value, size.Value);
        return 1;
    }

    private static void Style(nint obj, (byte R, byte G, byte B, byte A)? fill, (byte R, byte G, byte B, byte A)? stroke, float strokeWidth, string? blendMode)
    {
        if (fill is { } f)
        {
            FPDFPageObj_SetFillColor(obj, f.R, f.G, f.B, f.A);
        }

        if (stroke is { } s)
        {
            FPDFPageObj_SetStrokeColor(obj, s.R, s.G, s.B, s.A);
            FPDFPageObj_SetStrokeWidth(obj, strokeWidth);

            // Round ends and corners, as the screen draws them.
            FPDFPageObj_SetLineCap(obj, FPDF_LINECAP_ROUND);
            FPDFPageObj_SetLineJoin(obj, FPDF_LINEJOIN_ROUND);
        }

        if (blendMode is not null)
        {
            FPDFPageObj_SetBlendMode(obj, blendMode);
        }
    }

    private void Insert(nint obj)
    {
        if (!FPDFPage_InsertObject(_page, obj))
        {
            FPDFPageObj_Destroy(obj);
            throw new InvalidOperationException("PDFium could not insert the object.");
        }
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"PDFium could not {what}.");
        }
    }

    public sealed class PathBuilder
    {
        private readonly nint _path;

        internal PathBuilder(nint path) => _path = path;

        public PathBuilder LineTo(float x, float y) => Do(FPDFPath_LineTo(_path, x, y));

        public PathBuilder MoveTo(float x, float y) => Do(FPDFPath_MoveTo(_path, x, y));

        public PathBuilder CurveTo(float x1, float y1, float x2, float y2, float x3, float y3) => Do(FPDFPath_BezierTo(_path, x1, y1, x2, y2, x3, y3));

        public PathBuilder Close() => Do(FPDFPath_Close(_path));

        private PathBuilder Do(bool ok) => ok ? this : throw new InvalidOperationException("PDFium could not extend the path.");
    }
}
