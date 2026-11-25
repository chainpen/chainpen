using System.Runtime.InteropServices;
using Chainpen.Core.Model;
using SkiaSharp;
using static Chainpen.Pdf.Pdfium.PdfiumNative;

namespace Chainpen.Pdf.Pdfium;

/// <summary>A whiteout on the page: the white object, and the area in PDF user space it covers.</summary>
public readonly record struct RedactionArea(PdfiumObject Cover, PdfBox Area);

/// <summary>
/// Whiteout as real redaction in the final copy (FS-614): whatever a whiteout covers is taken out of the file, not
/// just painted over, so it can't be selected, copied or uncovered in another editor. Only objects drawn before a
/// whiteout count as under it; anything drawn after it sits on top and stays.
/// </summary>
public sealed partial class PdfiumPageEditor
{
    /// <summary>Pages are flattened at this resolution when only a picture can remove what's covered.</summary>
    public const double FlattenDpi = 300;

    // A page picture's longest side, so a poster-sized page doesn't need gigabytes.
    private const int MaxFlattenPixels = 7000;
    private const int JpegQuality = 90;
    private const float Tolerance = 0.01f;
    private const double EdgeInset = 0.5;

    // About a 300 dpi A3 scan; past this a picture is flattened with the page rather than copied and re-encoded.
    private const long MaxWhitenPixels = 20_000_000;

    // How large a picture is drawn to look for see-through parts, and the alpha that still counts as solid.
    private const uint MaskProbePixels = 256;
    private const byte OpaqueAlpha = 250;

    /// <summary>
    /// Removes what the whiteouts cover: objects entirely under one are deleted, and the covered pixels of pictures
    /// are made white. Returns false, having removed nothing that matters, when something is only partly covered and
    /// can't be cut (text, a line passing under, an embedded form, a see-through picture): the caller then flattens
    /// the page with <see cref="FlattenToPicture"/>.
    /// </summary>
    public bool RemoveUnder(IReadOnlyList<RedactionArea> whiteouts)
    {
        ArgumentNullException.ThrowIfNull(whiteouts);
        Dictionary<nint, PdfBox> coverAreas = whiteouts.ToDictionary(whiteout => whiteout.Cover.Handle, whiteout => whiteout.Area);

        // Decide everything first, so a page that has to be flattened isn't half-changed.
        var toRemove = new List<nint>();
        var toWhiten = new List<(nint Image, List<PdfBox> Areas)>();
        var areasAbove = new List<PdfBox>();
        for (int index = FPDFPage_CountObjects(_page) - 1; index >= 0; index--)
        {
            nint obj = FPDFPage_GetObject(_page, index);
            if (coverAreas.TryGetValue(obj, out PdfBox coverArea))
            {
                areasAbove.Add(coverArea);
                continue;
            }

            if (areasAbove.Count == 0)
            {
                continue;
            }

            if (!FPDFPageObj_GetBounds(obj, out float left, out float bottom, out float right, out float top))
            {
                return false;
            }

            var bounds = new PdfBox(left, bottom, right, top);
            List<PdfBox> touching = [.. areasAbove.Where(area => Overlaps(area, bounds))];
            if (touching.Count == 0)
            {
                continue;
            }

            if (touching.Any(area => Encloses(area, bounds)))
            {
                toRemove.Add(obj);
                continue;
            }

            switch (FPDFPageObj_GetType(obj))
            {
                case FPDF_PAGEOBJ_IMAGE when CanWhiten(obj):
                    toWhiten.Add((obj, touching));
                    break;

                case FPDF_PAGEOBJ_SHADING:
                    // A colour fill holds nothing to read; the whiteout hides its part of it.
                    break;

                case FPDF_PAGEOBJ_PATH when !PathPassesUnder(obj, touching):
                    // Only its box overlaps, e.g. a form's border round the whiteout.
                    break;

                default:
                    return false;
            }
        }

        foreach ((nint image, List<PdfBox> areas) in toWhiten)
        {
            if (!WhitenImage(image, areas))
            {
                return false;
            }
        }

        foreach (nint obj in toRemove)
        {
            // Anything that stays would still be in the file under the white box, so the page is flattened instead.
            if (!FPDFPage_RemoveObject(_page, obj))
            {
                return false;
            }

            FPDFPageObj_Destroy(obj);
        }

        return true;
    }

    /// <summary>
    /// Replaces everything on the page with one picture of it, so nothing under a whiteout survives. Text on this page
    /// can't be searched or copied afterwards. Form fields and links are annotations and stay as they were.
    /// </summary>
    /// <param name="cropBox">The visible area, in PDF user space.</param>
    public void FlattenToPicture(PdfBox cropBox)
    {
        double dpi = Math.Min(FlattenDpi, MaxFlattenPixels * 72 / Math.Max(cropBox.Width, cropBox.Height));
        int width = Math.Max(1, (int)Math.Ceiling(cropBox.Width * dpi / 72));
        int height = Math.Max(1, (int)Math.Ceiling(cropBox.Height * dpi / 72));
        var pixels = new byte[width * height * 4];

        // Rendered unrotated, so the picture sits in the page's own coordinates; the page keeps its /Rotate.
        int rotation = FPDFPage_GetRotation(_page);
        FPDFPage_SetRotation(_page, 0);
        try
        {
            unsafe
            {
                fixed (byte* buffer = pixels)
                {
                    nint bitmap = FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRA, (nint)buffer, width * 4);
                    Check(bitmap != 0, "create a bitmap");
                    try
                    {
                        FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);

                        // No annotations: they stay on the page as they are, so drawing them in would double them.
                        FPDF_RenderPageBitmap(bitmap, _page, 0, 0, width, height, 0, 0);
                    }
                    finally
                    {
                        FPDFBitmap_Destroy(bitmap);
                    }
                }
            }
        }
        finally
        {
            FPDFPage_SetRotation(_page, rotation);
        }

        for (int index = FPDFPage_CountObjects(_page) - 1; index >= 0; index--)
        {
            nint obj = FPDFPage_GetObject(_page, index);
            if (FPDFPage_RemoveObject(_page, obj))
            {
                FPDFPageObj_Destroy(obj);
            }
        }

        byte[] jpeg = EncodeJpeg(pixels, width, height, SKColorType.Bgra8888, width * 4);
        AddJpeg(jpeg, cropBox.Left, cropBox.Bottom, cropBox.Width, cropBox.Height);
    }

    /// <summary>
    /// Pictures with their own colours, no transparency and a sensible size; a stencil, a see-through picture or a huge
    /// one (whose size comes from the file) is flattened instead, which is bounded.
    /// </summary>
    private bool CanWhiten(nint image) =>
        !FPDFPageObj_HasTransparency(image)
        && FPDFImageObj_GetImageMetadata(image, _page, out ImageMetadata metadata)
        && metadata.Colorspace != FPDF_COLORSPACE_UNKNOWN
        && (long)metadata.Width * metadata.Height <= MaxWhitenPixels
        && !HasOwnMask(image, metadata);

    /// <summary>
    /// Whether the picture carries a mask of its own (/SMask or /Mask), as a logo with a see-through background does.
    /// PDFium's transparency check doesn't look at those, and storing the picture again as JPEG would drop the mask and
    /// turn its see-through parts solid. PDFium has no call that says so either, so the picture is drawn by itself,
    /// small, and looked at. It only draws one upright, so the picture's place on the page is set aside meanwhile.
    /// Not being able to tell counts as masked: the page is flattened, which keeps how it looks.
    /// </summary>
    private unsafe bool HasOwnMask(nint image, ImageMetadata metadata)
    {
        if (!FPDFPageObj_GetMatrix(image, out FsMatrix placed))
        {
            return true;
        }

        int width = (int)Math.Clamp(metadata.Width, 1u, MaskProbePixels), height = (int)Math.Clamp(metadata.Height, 1u, MaskProbePixels);
        if (!FPDFImageObj_SetMatrix(image, width, 0, 0, height, 0, 0))
        {
            return true;
        }

        try
        {
            nint drawn = FPDFImageObj_GetRenderedBitmap(_document, _page, image);
            if (drawn == 0)
            {
                return true;
            }

            try
            {
                byte* pixels = (byte*)FPDFBitmap_GetBuffer(drawn);
                int stride = FPDFBitmap_GetStride(drawn);
                if (FPDFBitmap_GetFormat(drawn) != FPDFBitmap_BGRA || pixels is null)
                {
                    return false;
                }

                // The outermost pixels are left out where there are others: an edge can be softened in the drawing.
                int rows = FPDFBitmap_GetHeight(drawn), columns = FPDFBitmap_GetWidth(drawn);
                int inset = Math.Min(rows, columns) > 2 ? 1 : 0;
                for (int row = inset; row < rows - inset; row++)
                {
                    for (int column = inset; column < columns - inset; column++)
                    {
                        if (pixels[(row * stride) + (column * 4) + 3] < OpaqueAlpha)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            finally
            {
                FPDFBitmap_Destroy(drawn);
            }
        }
        finally
        {
            FPDFImageObj_SetMatrix(image, placed.A, placed.B, placed.C, placed.D, placed.E, placed.F);
        }
    }

    /// <summary>Whether any stretch of the path runs inside one of the areas (curves judged by their control points).</summary>
    private static bool PathPassesUnder(nint path, List<PdfBox> areas)
    {
        int count = FPDFPath_CountSegments(path);
        if (count < 0 || !FPDFPageObj_GetMatrix(path, out FsMatrix matrix))
        {
            return true;
        }

        (double X, double Y) ToPage(float x, float y) =>
            ((matrix.A * x) + (matrix.C * y) + matrix.E, (matrix.B * x) + (matrix.D * y) + matrix.F);

        // A line running along a whiteout's edge, like a box drawn round it, isn't under it.
        PdfBox[] inside = [.. areas.Select(area => new PdfBox(area.Left + EdgeInset, area.Bottom + EdgeInset, area.Right - EdgeInset, area.Top - EdgeInset))];
        (double X, double Y)? previous = null;
        (double X, double Y) figureStart = default;
        for (int i = 0; i < count; i++)
        {
            nint segment = FPDFPath_GetPathSegment(path, i);
            if (segment == 0 || !FPDFPathSegment_GetPoint(segment, out float x, out float y))
            {
                return true;
            }

            (double X, double Y) point = ToPage(x, y);
            if (FPDFPathSegment_GetType(segment) == FPDF_SEGMENT_MOVETO || previous is null)
            {
                figureStart = point;
            }
            else if (inside.Any(area => SegmentEnters(area, previous.Value, point)))
            {
                return true;
            }

            if (FPDFPathSegment_GetClose(segment) && inside.Any(area => SegmentEnters(area, point, figureStart)))
            {
                return true;
            }

            previous = point;
        }

        return false;
    }

    /// <summary>Makes the picture's pixels under the areas white and stores it again as JPEG.</summary>
    private bool WhitenImage(nint image, List<PdfBox> areas)
    {
        if (!FPDFPageObj_GetMatrix(image, out FsMatrix m))
        {
            return false;
        }

        // The picture fills its unit square (y up) mapped by the matrix; undo that to find which pixels are covered.
        double determinant = (m.A * m.D) - (m.B * m.C);
        if (Math.Abs(determinant) < 1e-9)
        {
            return false;
        }

        nint bitmap = FPDFImageObj_GetBitmap(image);
        if (bitmap == 0)
        {
            return false;
        }

        try
        {
            int format = FPDFBitmap_GetFormat(bitmap);
            int bytesPerPixel = format switch
            {
                FPDFBitmap_Gray => 1,
                FPDFBitmap_BGR => 3,
                FPDFBitmap_BGRx or FPDFBitmap_BGRA => 4,
                _ => 0,
            };
            int width = FPDFBitmap_GetWidth(bitmap), height = FPDFBitmap_GetHeight(bitmap), stride = FPDFBitmap_GetStride(bitmap);
            nint buffer = FPDFBitmap_GetBuffer(bitmap);
            if (bytesPerPixel == 0 || width <= 0 || height <= 0 || buffer == 0)
            {
                return false;
            }

            (double U, double V) ToUnit(double x, double y)
            {
                double dx = x - m.E, dy = y - m.F;
                return (((m.D * dx) - (m.C * dy)) / determinant, ((m.A * dy) - (m.B * dx)) / determinant);
            }

            var pixels = new byte[checked(stride * height)];
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
            foreach (PdfBox area in areas)
            {
                (double U, double V)[] corners =
                [
                    ToUnit(area.Left, area.Bottom), ToUnit(area.Right, area.Bottom), ToUnit(area.Left, area.Top), ToUnit(area.Right, area.Top),
                ];
                int firstColumn = Math.Max(0, (int)Math.Floor(corners.Min(c => c.U) * width) - 1);
                int lastColumn = Math.Min(width - 1, (int)Math.Ceiling(corners.Max(c => c.U) * width) + 1);
                int firstRow = Math.Max(0, (int)Math.Floor((1 - corners.Max(c => c.V)) * height) - 1);
                int lastRow = Math.Min(height - 1, (int)Math.Ceiling((1 - corners.Min(c => c.V)) * height) + 1);
                for (int row = firstRow; row <= lastRow; row++)
                {
                    double v = 1 - ((row + 0.5) / height);
                    for (int column = firstColumn; column <= lastColumn; column++)
                    {
                        double u = (column + 0.5) / width;
                        double x = (m.A * u) + (m.C * v) + m.E, y = (m.B * u) + (m.D * v) + m.F;
                        if (x >= area.Left && x <= area.Right && y >= area.Bottom && y <= area.Top)
                        {
                            pixels.AsSpan((row * stride) + (column * bytesPerPixel), bytesPerPixel).Fill(0xFF);
                        }
                    }
                }
            }

            byte[] jpeg = bytesPerPixel switch
            {
                1 => EncodeJpeg(pixels, width, height, SKColorType.Gray8, stride),
                4 => EncodeJpeg(pixels, width, height, SKColorType.Bgra8888, stride),
                _ => EncodeJpeg(ExpandBgr(pixels, width, height, stride), width, height, SKColorType.Bgra8888, width * 4),
            };
            return ReplaceWithJpeg(image, jpeg);
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
        }
    }

    private unsafe bool ReplaceWithJpeg(nint image, byte[] jpeg)
    {
        fixed (byte* data = jpeg)
        {
            nint page = _page;
            var access = new PdfFileAccess { FileLength = new CULong((nuint)jpeg.Length), GetBlock = &GetJpegBlock, Param = data };
            return FPDFImageObj_LoadJpegFileInline(&page, 1, image, &access);
        }
    }

    private static byte[] ExpandBgr(byte[] bgr, int width, int height, int stride)
    {
        var bgra = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int from = (row * stride) + (column * 3), to = ((row * width) + column) * 4;
                bgra[to] = bgr[from];
                bgra[to + 1] = bgr[from + 1];
                bgra[to + 2] = bgr[from + 2];
                bgra[to + 3] = 0xFF;
            }
        }

        return bgra;
    }

    private static byte[] EncodeJpeg(byte[] pixels, int width, int height, SKColorType colorType, int stride)
    {
        // Opaque: the fourth byte of BGRx is whatever PDFium left there, so every pixel is treated as solid.
        var info = new SKImageInfo(width, height, colorType, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap();
        unsafe
        {
            fixed (byte* data = pixels)
            {
                Check(bitmap.InstallPixels(info, (nint)data, stride), "prepare the picture");
                using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, JpegQuality) ?? throw new InvalidOperationException("The picture could not be encoded.");
                return encoded.ToArray();
            }
        }
    }

    private static bool Overlaps(PdfBox a, PdfBox b) =>
        a.Left < b.Right - Tolerance && b.Left < a.Right - Tolerance && a.Bottom < b.Top - Tolerance && b.Bottom < a.Top - Tolerance;

    private static bool Encloses(PdfBox outer, PdfBox inner) =>
        inner.Left >= outer.Left - Tolerance && inner.Right <= outer.Right + Tolerance
        && inner.Bottom >= outer.Bottom - Tolerance && inner.Top <= outer.Top + Tolerance;

    /// <summary>Liang–Barsky: whether the segment from <paramref name="a"/> to <paramref name="b"/> enters the box.</summary>
    private static bool SegmentEnters(PdfBox box, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double enter = 0, leave = 1;
        foreach ((double p, double q) in new[] { (-dx, a.X - box.Left), (dx, box.Right - a.X), (-dy, a.Y - box.Bottom), (dy, box.Top - a.Y) })
        {
            if (Math.Abs(p) < 1e-12)
            {
                if (q < 0)
                {
                    return false;
                }

                continue;
            }

            double t = q / p;
            if (p < 0)
            {
                enter = Math.Max(enter, t);
            }
            else
            {
                leave = Math.Min(leave, t);
            }

            if (enter > leave)
            {
                return false;
            }
        }

        return true;
    }
}
