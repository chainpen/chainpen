using System.Runtime.InteropServices;
using Chainpen.Pdf.Export;
using static Chainpen.Pdf.Pdfium.PdfiumNative;

namespace Chainpen.Pdf.Pdfium;

/// <summary>A font loaded into one document. Valid until the document is disposed.</summary>
public readonly record struct PdfiumFont(nint Handle);

/// <summary>A character and its box in PDF user space (y up).</summary>
public readonly record struct PdfCharacter(int Code, double Left, double Bottom, double Right, double Top);

/// <summary>An object added to a page. Valid while the page is being edited.</summary>
public readonly record struct PdfiumObject(nint Handle);

/// <summary>
/// Writing through PDFium's editing API (FS-024 spike, the basis for FS-607). Coordinates are PDF user space:
/// points from the bottom-left of the unrotated page; converting from Chainpen's display coordinates is
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
    /// <summary>
    /// Embeds the part of a font these characters need, named with a subset tag (<c>ABCDEF+NotoSans-Regular</c>,
    /// FS-612). Null when the font's licence doesn't allow embedding, or PDFium can't read it.
    /// </summary>
    /// <param name="faceIndex">Which font in a collection (.ttc); 0 for a single font file.</param>
    public PdfiumFont LoadSubsetFont(ReadOnlySpan<byte> fontFile, string characters, int faceIndex = 0) =>
        TryLoadSubsetFont(fontFile, characters, faceIndex) ?? throw new InvalidOperationException("The font can't be embedded.");

    /// <inheritdoc cref="LoadSubsetFont"/>
    public PdfiumFont? TryLoadSubsetFont(ReadOnlySpan<byte> fontFile, string characters, int faceIndex = 0)
    {
        var codepoints = new HashSet<int>();
        foreach (var rune in characters.EnumerateRunes())
        {
            codepoints.Add(rune.Value);
        }

        byte[] cut = FontSubsetter.Subset(fontFile, codepoints, faceIndex);
        if (!FontNaming.AllowsEmbedding(cut))
        {
            return null;
        }

        byte[] subset = FontNaming.WithSubsetTag(cut, FontNaming.SubsetTag(codepoints));
        return WithLock(() =>
        {
            unsafe
            {
                fixed (byte* data = subset)
                {
                    // A font PDFium can't read is left out, like one that may not be embedded: its text falls back.
                    nint font = FPDFText_LoadFont(_document, data, (uint)subset.Length, FPDF_FONT_TRUETYPE, cid: true);
                    if (font == 0)
                    {
                        return (PdfiumFont?)null;
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

    /// <summary>
    /// Whether the file's owner has protected it against changes (FS-214): it's encrypted, opened without the owner
    /// password, and its permissions don't allow modifying the contents.
    /// </summary>
    public bool ForbidsChanges => WithLock(() =>
        FPDF_GetSecurityHandlerRevision(_document) >= 0 && (FPDF_GetDocPermissions(_document).Value & PermissionModify) == 0);

    /// <summary>
    /// Before redacting (FS-614): annotations are drawn above the page, so a whiteout can't cover them. When any
    /// overlaps <paramref name="areas"/>, the page's annotations are drawn into its content, where redaction can remove
    /// them, and any left (those that don't print, such as hidden fields) that still overlap are deleted.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public bool FlattenAnnotationsUnder(int pageIndex, IReadOnlyList<Chainpen.Core.Model.PdfBox> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        bool changed = false;
        WithPage(pageIndex, page =>
        {
            if (AnnotationsOverlapping(page, areas).Count == 0)
            {
                return;
            }

            if (FPDFPage_Flatten(page, FLAT_PRINT) == FLATTEN_FAIL)
            {
                throw new InvalidOperationException("PDFium could not flatten the page's annotations.");
            }

            changed = true;
        });

        if (changed)
        {
            // Flattening rewrites the page's content, so what's left is looked at on the page as reloaded.
            WithPage(pageIndex, page =>
            {
                foreach (int index in AnnotationsOverlapping(page, areas).OrderDescending())
                {
                    if (!FPDFPage_RemoveAnnot(page, index))
                    {
                        throw new InvalidOperationException("PDFium could not remove an annotation under whiteout.");
                    }
                }
            });
        }

        return changed;
    }

    private static List<int> AnnotationsOverlapping(nint page, IReadOnlyList<Chainpen.Core.Model.PdfBox> areas)
    {
        var overlapping = new List<int>();
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
                if (FPDFAnnot_GetRect(annotation, out FsRectF rect)
                    && areas.Any(area => Math.Min(rect.Left, rect.Right) < area.Right && Math.Max(rect.Left, rect.Right) > area.Left
                        && Math.Min(rect.Top, rect.Bottom) < area.Top && Math.Max(rect.Top, rect.Bottom) > area.Bottom))
                {
                    overlapping.Add(index);
                }
            }
            finally
            {
                FPDFPage_CloseAnnot(annotation);
            }
        }

        return overlapping;
    }

    public int GetObjectCount(int pageIndex) => WithPage(pageIndex, FPDFPage_CountObjects);

    /// <summary>What's on a page, bottom to top: "text", "path", "image", "shading" or "form". For checks and tests.</summary>
    public IReadOnlyList<string> GetObjectKinds(int pageIndex) => WithPage(pageIndex, page =>
        (IReadOnlyList<string>)[.. Enumerable.Range(0, FPDFPage_CountObjects(page)).Select(index => FPDFPageObj_GetType(FPDFPage_GetObject(page, index)) switch
        {
            FPDF_PAGEOBJ_TEXT => "text",
            FPDF_PAGEOBJ_PATH => "path",
            FPDF_PAGEOBJ_IMAGE => "image",
            FPDF_PAGEOBJ_SHADING => "shading",
            FPDF_PAGEOBJ_FORM => "form",
            _ => "unknown",
        })]);

    /// <summary>
    /// The stored pixels of the <paramref name="nth"/> picture on a page as lightness (0 black – 255 white), row by row
    /// from the top, or null when it can't be read. Shows what's in the file, not what's drawn over it.
    /// </summary>
    public (int Width, int Height, byte[] Lightness)? ReadImage(int pageIndex, int nth) => WithPage(pageIndex, page =>
    {
        nint[] images = [.. Enumerable.Range(0, FPDFPage_CountObjects(page)).Select(index => FPDFPage_GetObject(page, index)).Where(obj => FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_IMAGE)];
        if (nth >= images.Length)
        {
            return ((int, int, byte[])?)null;
        }

        nint bitmap = FPDFImageObj_GetBitmap(images[nth]);
        if (bitmap == 0)
        {
            return null;
        }

        try
        {
            int format = FPDFBitmap_GetFormat(bitmap);
            int bytesPerPixel = format switch { FPDFBitmap_Gray => 1, FPDFBitmap_BGR => 3, _ => 4 };
            int width = FPDFBitmap_GetWidth(bitmap), height = FPDFBitmap_GetHeight(bitmap), stride = FPDFBitmap_GetStride(bitmap);
            var raw = new byte[stride * height];
            Marshal.Copy(FPDFBitmap_GetBuffer(bitmap), raw, 0, raw.Length);
            var lightness = new byte[width * height];
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int at = (row * stride) + (column * bytesPerPixel);
                    lightness[(row * width) + column] = bytesPerPixel == 1 ? raw[at] : (byte)((raw[at] + raw[at + 1] + raw[at + 2]) / 3);
                }
            }

            return (width, height, lightness);
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
        }
    });

    /// <summary>
    /// Every character on a page with its box in PDF user space, in the page's text order. Spaces PDFium infers between
    /// words come with an empty box; the line breaks it infers are left out.
    /// </summary>
    public IReadOnlyList<PdfCharacter> GetCharacters(int pageIndex) => WithPage(pageIndex, page =>
    {
        nint textPage = FPDFText_LoadPage(page);
        if (textPage == 0)
        {
            return (IReadOnlyList<PdfCharacter>)[];
        }

        try
        {
            int count = FPDFText_CountChars(textPage);
            var characters = new List<PdfCharacter>(Math.Max(0, count));
            for (int index = 0; index < count; index++)
            {
                int code = (int)FPDFText_GetUnicode(textPage, index);
                if (FPDFText_IsGenerated(textPage, index) != 0)
                {
                    // A space PDFium inferred between words still separates them; its line breaks don't matter.
                    if (code == ' ')
                    {
                        characters.Add(new PdfCharacter(code, 0, 0, 0, 0));
                    }

                    continue;
                }

                if (FPDFText_GetLooseCharBox(textPage, index, out FsRectF box))
                {
                    characters.Add(new PdfCharacter(code, box.Left, box.Bottom, box.Right, box.Top));
                }
            }

            return characters;
        }
        finally
        {
            FPDFText_ClosePage(textPage);
        }
    });

    /// <summary>
    /// Small outlined squares drawn on a page, in PDF user space: the tick boxes of forms that draw them rather than
    /// using a ☐ character. Only the page's own paths count, not those inside embedded forms.
    /// </summary>
    public IReadOnlyList<PdfCharacter> GetDrawnBoxes(int pageIndex) => WithPage(pageIndex, page =>
    {
        var boxes = new List<PdfCharacter>();
        int count = FPDFPage_CountObjects(page);
        for (int index = 0; index < count; index++)
        {
            nint obj = FPDFPage_GetObject(page, index);
            if (FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_PATH
                || !FPDFPageObj_GetBounds(obj, out float left, out float bottom, out float right, out float top)
                || !FPDFPath_GetDrawMode(obj, out _, out bool stroke) || !stroke)
            {
                continue;
            }

            float width = right - left, height = top - bottom;
            int segments = FPDFPath_CountSegments(obj);
            bool isSquare = width is >= 5 and <= 24 && height is >= 5 and <= 24 && Math.Abs(width - height) <= Math.Max(1.5f, width * 0.15f);
            if (isSquare && segments is >= 4 and <= 6)
            {
                boxes.Add(new PdfCharacter(0x2610, left, bottom, right, top));
            }
        }

        return boxes;
    });

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
                // The size comes from the file: too big for an array is refused rather than cut short, and PDFium is
                // only ever told the buffer's real length.
                if (!FPDFAttachment_GetFile(attachment, null, new CULong(0), out CULong size) || size.Value > (ulong)Array.MaxLength)
                {
                    return null;
                }

                var bytes = new byte[(int)size.Value];
                fixed (byte* buffer = bytes)
                {
                    return FPDFAttachment_GetFile(attachment, buffer, new CULong((nuint)bytes.Length), out _) ? bytes : null;
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

        var buffer = new char[checked((int)(bytes / 2))];
        fixed (char* chars = buffer)
        {
            // The buffer's own length in bytes: an odd count from PDFium would otherwise claim one byte too many.
            FPDFAttachment_GetName(attachment, chars, new CULong((nuint)(buffer.Length * sizeof(char))));
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
public sealed partial class PdfiumPageEditor
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
    /// <param name="boldStroke">For bold (FS-952): the glyphs are also outlined this wide, in text space. 0 for regular.</param>
    public void AddText(
        PdfiumFont font,
        float fontSize,
        string text,
        (double A, double B, double C, double D, double E, double F) matrix,
        (byte R, byte G, byte B) color,
        float boldStroke = 0)
    {
        AddObject(FPDFPageObj_CreateTextObj(_document, font.Handle, fontSize), "create text", obj =>
        {
            Check(FPDFText_SetText(obj, text), "set the text");
            FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, 255);
            if (boldStroke > 0)
            {
                FPDFPageObj_SetStrokeColor(obj, color.R, color.G, color.B, 255);
                FPDFPageObj_SetStrokeWidth(obj, boldStroke);
                Check(FPDFTextObj_SetTextRenderMode(obj, TextRenderFillStroke), "set bold text");
            }

            FPDFPageObj_Transform(obj, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
        });
    }

    /// <summary>Adds a path built by <paramref name="build"/>. Pass a fill colour, a stroke, or both.</summary>
    public PdfiumObject AddPath(
        float startX,
        float startY,
        Action<PathBuilder> build,
        (byte R, byte G, byte B, byte A)? fill = null,
        (byte R, byte G, byte B, byte A)? stroke = null,
        float strokeWidth = 1,
        string? blendMode = null)
    {
        return AddObject(FPDFPageObj_CreateNewPath(startX, startY), "create path", obj =>
        {
            build(new PathBuilder(obj));
            Style(obj, fill, stroke, strokeWidth, blendMode);
            Check(FPDFPath_SetDrawMode(obj, fill is null ? FPDF_FILLMODE_NONE : FPDF_FILLMODE_WINDING, stroke is not null), "set draw mode");
        });
    }

    /// <summary>Places a BGRA image (straight alpha; alpha becomes a soft mask) into a rectangle.</summary>
    public void AddImage(ReadOnlySpan<byte> bgra, int pixelWidth, int pixelHeight, double x, double y, double width, double height) =>
        AddImage(bgra, pixelWidth, pixelHeight, (width, 0, 0, height, x, y));

    /// <summary>
    /// Places a BGRA image (straight alpha; alpha becomes a soft mask) with a matrix mapping the unit square, y up, to
    /// user space.
    /// </summary>
    public void AddImage(ReadOnlySpan<byte> bgra, int pixelWidth, int pixelHeight, (double A, double B, double C, double D, double E, double F) matrix)
    {
        long needed = (long)pixelWidth * pixelHeight * 4;
        if (pixelWidth <= 0 || pixelHeight <= 0 || bgra.Length < needed)
        {
            throw new ArgumentException($"Expected {needed} bytes of BGRA pixels.", nameof(bgra));
        }

        unsafe
        {
            fixed (byte* pixels = bgra)
            {
                nint pointer = (nint)pixels;
                AddObject(FPDFPageObj_NewImageObj(_document), "create image", obj =>
                {
                    nint bitmap = FPDFBitmap_CreateEx(pixelWidth, pixelHeight, FPDFBitmap_BGRA, pointer, pixelWidth * 4);
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

                    Check(FPDFImageObj_SetMatrix(obj, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F), "place image");
                });
            }
        }
    }

    /// <summary>Places JPEG data, embedded without re-encoding, into a rectangle in PDF user space.</summary>
    public void AddJpeg(ReadOnlySpan<byte> jpeg, double x, double y, double width, double height)
    {
        unsafe
        {
            fixed (byte* data = jpeg)
            {
                byte* pinned = data;
                int length = jpeg.Length;
                AddObject(FPDFPageObj_NewImageObj(_document), "create image", obj =>
                {
                    var access = new PdfFileAccess { FileLength = new CULong((nuint)length), GetBlock = &GetJpegBlock, Param = pinned };
                    Check(FPDFImageObj_LoadJpegFileInline(null, 0, obj, &access), "read the JPEG image");
                    Check(FPDFImageObj_SetMatrix(obj, width, 0, 0, height, x, y), "place image");
                });
            }
        }
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

    /// <summary>
    /// Sets up a new object and puts it on the page. Until it's inserted the object is ours, so it's destroyed if setting
    /// it up fails; once inserting is tried, it's PDFium's, which frees it itself when inserting fails (fpdf_edit.h).
    /// </summary>
    private PdfiumObject AddObject(nint obj, string what, Action<nint> prepare)
    {
        Check(obj != 0, what);
        try
        {
            prepare(obj);
        }
        catch
        {
            FPDFPageObj_Destroy(obj);
            throw;
        }

        Check(FPDFPage_InsertObject(_page, obj), "insert the object");
        return new PdfiumObject(obj);
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
