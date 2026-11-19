using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Core.Session;
using Fillsign.Pdf.Fonts;
using Fillsign.Pdf.Pdfium;
using SkiaSharp;

namespace Fillsign.Pdf.Export;

/// <summary>What was written, and anything the person should know about it.</summary>
/// <param name="Pdf">The file's bytes.</param>
/// <param name="HaveFormsMoved">
/// PDFium refused to reorder the pages in place, so they were copied in the new order instead. Fillable fields and
/// links may no longer work (FR-8.9); the caller says so.
/// </param>
public sealed record WrittenDocument(byte[] Pdf, bool HaveFormsMoved);

/// <summary>
/// Writes a document exactly as the editor shows it (FR-8.6–8.9): the page list (order, rotation, duplicates,
/// deletions) and every item drawn into the pages as real PDF content. Text is real text in an embedded Noto subset,
/// so it can be searched and copied; marks, shapes and drawings are vector paths; signatures are images with a soft
/// mask. Item positions go through <see cref="PageTransform"/>, the same conversion the screen uses (FR-8.8).
/// </summary>
public static class DocumentWriter
{
    /// <summary>
    /// The final copy to send (FR-8.2): a fresh, compact file with no editable record (FR-9.4) and none of the
    /// original's unused pages.
    /// </summary>
    /// <param name="cleanSource">The source document as Fillsign holds it: all opened and added pages, unencrypted.</param>
    /// <param name="sourcePages">Crop box and /Rotate of each source page.</param>
    public static WrittenDocument FinalCopy(byte[] cleanSource, IReadOnlyList<SourcePage> sourcePages, DocumentState document)
    {
        ArgumentNullException.ThrowIfNull(cleanSource);
        using PdfiumDocument pdf = PdfiumDocument.Open(cleanSource);
        bool haveFormsMoved = Arrange(pdf, cleanSource, sourcePages, document);

        // Before drawing, so a whiteout's redaction also covers a filled field (FS-950).
        pdf.FillFormFields(document.Fields);
        Draw(pdf, sourcePages, document, redactUnderWhiteout: true);
        return new WrittenDocument(pdf.SaveCopy(removeSecurity: true), haveFormsMoved);
    }

    /// <summary>
    /// A final copy with its form fields locked (made read-only), for when the answers shouldn't be changed by whoever
    /// receives it. The fields still show their values; links keep working.
    /// </summary>
    public static byte[] WithFormFieldsLocked(byte[] finalCopy)
    {
        ArgumentNullException.ThrowIfNull(finalCopy);
        using PdfiumDocument pdf = PdfiumDocument.Open(finalCopy);
        return pdf.LockFormFields() > 0 ? pdf.SaveCopy(removeSecurity: true) : finalCopy;
    }

    /// <summary>
    /// The copy to print (FS-808): the final copy without its empty signature fields. On paper there's nothing to sign
    /// into, and the system's print path draws each one as a crossed box. A final copy you send keeps them.
    /// </summary>
    public static byte[] ForPrinting(byte[] finalCopy)
    {
        ArgumentNullException.ThrowIfNull(finalCopy);
        using PdfiumDocument pdf = PdfiumDocument.Open(finalCopy);
        var everywhere = new PdfBox[] { new(-1e6, -1e6, 1e6, 1e6) };
        int removed = 0;
        for (int i = 0; i < pdf.PageCount; i++)
        {
            removed += pdf.RemoveSignatureFieldsUnder(i, everywhere);
        }

        return removed > 0 ? pdf.SaveCopy(removeSecurity: true) : finalCopy;
    }

    /// <summary>
    /// The working copy (FR-8.1, FR-9.1): the clean source unchanged, then an incremental update with the finished
    /// pages (what other readers show), then Fillsign's editable record. Reopening it in Fillsign restores the clean
    /// pages and every item (FR-9.2); saving again rewrites it from the clean source, so it doesn't grow (FR-9.8).
    /// </summary>
    public static WrittenDocument WorkingCopy(byte[] cleanSource, IReadOnlyList<SourcePage> sourcePages, DocumentState document)
    {
        ArgumentNullException.ThrowIfNull(cleanSource);
        ArgumentNullException.ThrowIfNull(sourcePages);
        using PdfiumDocument pdf = PdfiumDocument.Open(cleanSource);
        bool haveFormsMoved = Arrange(pdf, cleanSource, sourcePages, document);
        pdf.FillFormFields(document.Fields);

        // The working copy keeps what's under whiteout, so the whiteout can still be moved or deleted (FR-9.2).
        Draw(pdf, sourcePages, document, redactUnderWhiteout: false);
        byte[] finished = pdf.SaveIncremental();
        if (!ByteStamp.Of(cleanSource).MatchesPrefixOf(finished))
        {
            throw new InvalidOperationException("PDFium didn't keep the clean original at the start of the working copy.");
        }

        byte[] withRecord = SessionStore.Append(finished, ByteStamp.Of(cleanSource), sourcePages.Count, document);
        return new WrittenDocument(withRecord, haveFormsMoved);
    }

    /// <summary>
    /// Puts the pages in the document's order with its rotations: unused pages are deleted, pages used more than once
    /// are copied in, and the rest are moved in place so form fields and links keep working (FR-8.9). If PDFium won't
    /// move them, every page is copied in, in order, and the originals deleted. Returns whether that happened.
    /// </summary>
    private static bool Arrange(PdfiumDocument pdf, byte[] cleanSource, IReadOnlyList<SourcePage> sourcePages, DocumentState document)
    {
        if (pdf.PageCount != sourcePages.Count)
        {
            throw new ArgumentException($"The source has {pdf.PageCount} pages but {sourcePages.Count} page descriptions.", nameof(sourcePages));
        }

        // Each entry gets a page of its own: the source page the first time it's used, a copy after that.
        var pageOf = new int[document.Pages.Length];
        var used = new HashSet<int>();
        PdfiumDocument? copies = null;
        try
        {
            for (int i = 0; i < document.Pages.Length; i++)
            {
                int source = document.Pages[i].SourceIndex;
                if (used.Add(source))
                {
                    pageOf[i] = source;
                    continue;
                }

                copies ??= PdfiumDocument.Open(cleanSource);
                pdf.ImportPages(copies, [source], pdf.PageCount);
                pageOf[i] = pdf.PageCount - 1;
            }
        }
        finally
        {
            copies?.Dispose();
        }

        // Delete pages nothing uses, from the end so earlier indices stay put.
        int[] unused = [.. Enumerable.Range(0, sourcePages.Count).Where(index => !used.Contains(index)).OrderDescending()];
        foreach (int index in unused)
        {
            pdf.DeletePage(index);
        }

        int[] order = [.. pageOf.Select(page => page - unused.Count(deleted => deleted < page))];
        bool haveFormsMoved = false;
        if (!order.SequenceEqual(Enumerable.Range(0, order.Length)))
        {
            try
            {
                pdf.MovePages(order, 0);
            }
            catch (InvalidOperationException)
            {
                ReplaceWithCopiesInOrder(pdf, cleanSource, document);
                haveFormsMoved = true;
            }
        }

        for (int i = 0; i < document.Pages.Length; i++)
        {
            PageEntry entry = document.Pages[i];
            Rotation total = sourcePages[entry.SourceIndex].Rotate + entry.Rotation;
            if (total != sourcePages[entry.SourceIndex].Rotate)
            {
                pdf.SetRotation(i, total.QuarterTurns);
            }
        }

        return haveFormsMoved;
    }

    /// <summary>The fallback for pages PDFium won't move: copy every page in, in order, then delete what was there.</summary>
    private static void ReplaceWithCopiesInOrder(PdfiumDocument pdf, byte[] cleanSource, DocumentState document)
    {
        using PdfiumDocument copies = PdfiumDocument.Open(cleanSource);
        int existing = pdf.PageCount;
        pdf.ImportPages(copies, [.. document.Pages.Select(entry => entry.SourceIndex)], existing);
        for (int index = existing - 1; index >= 0; index--)
        {
            pdf.DeletePage(index);
        }
    }

    /// <summary>
    /// Draws each page's items into it as PDF content. With <paramref name="redactUnderWhiteout"/>, what a whiteout
    /// covers is then removed from the page (FS-614); where only part of something is covered and it can't be cut,
    /// that page becomes a picture instead.
    /// </summary>
    private static void Draw(PdfiumDocument pdf, IReadOnlyList<SourcePage> sourcePages, DocumentState document, bool redactUnderWhiteout)
    {
        // Text a whiteout lands on is drawn as outlines, not with the embedded font: the font lists every character it
        // was made for, so typed-then-covered digits would otherwise stay in the file.
        HashSet<ItemId> covered = redactUnderWhiteout ? CoveredText(document) : [];
        EmbeddedFonts fonts = EmbeddedFonts.For(pdf, document.Items.OfType<TextItem>()
            .Where(text => !covered.Contains(text.Id))
            .SelectMany(text => text.Lines));

        for (int i = 0; i < document.Pages.Length; i++)
        {
            PageEntry entry = document.Pages[i];
            Item[] items = [.. document.ItemsOn(entry.Id)];
            if (items.Length == 0)
            {
                continue;
            }

            var transform = new PageTransform(sourcePages[entry.SourceIndex], entry.Rotation);
            PdfBox cropBox = sourcePages[entry.SourceIndex].CropBox;
            if (redactUnderWhiteout)
            {
                PdfBox[] whiteoutAreas = [.. items.Where(IsWhiteout).Select(item => AreaInPdf(transform, item))];
                if (whiteoutAreas.Length > 0)
                {
                    pdf.FlattenAnnotationsUnder(i, whiteoutAreas);
                }

                // FS-962: an empty signature field signed over with a signature item is taken out of the final copy,
                // or viewers draw their "unsigned" mark (a crossed box) under the signature. The working copy keeps it.
                PdfBox[] signatureAreas = [.. items.Where(item => item is SignatureItem).Select(item => AreaInPdf(transform, item))];
                if (signatureAreas.Length > 0)
                {
                    pdf.RemoveSignatureFieldsUnder(i, signatureAreas);
                }
            }

            pdf.EditPage(i, page =>
            {
                var whiteouts = new List<RedactionArea>();
                foreach (Item item in items)
                {
                    PdfiumObject? drawn = item is TextItem text && covered.Contains(text.Id)
                        ? DrawTextAsOutlines(page, transform.ItemToPdf(new PagePoint(item.X, item.Y)), text)
                        : DrawItem(pdf, page, transform, fonts, item);
                    if (redactUnderWhiteout && item is ShapeItem { Kind: ShapeKind.Whiteout } && drawn is { } cover)
                    {
                        whiteouts.Add(new RedactionArea(cover, AreaInPdf(transform, item)));
                    }
                }

                if (whiteouts.Count > 0 && !page.RemoveUnder(whiteouts))
                {
                    page.FlattenToPicture(cropBox);
                }
            });
        }
    }

    private static bool IsWhiteout(Item item) => item is ShapeItem { Kind: ShapeKind.Whiteout };

    /// <summary>Text items a whiteout drawn after them touches, on the same page.</summary>
    private static HashSet<ItemId> CoveredText(DocumentState document)
    {
        var covered = new HashSet<ItemId>();
        foreach (PageEntry entry in document.Pages)
        {
            Item[] items = [.. document.ItemsOn(entry.Id)];
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] is not TextItem text)
                {
                    continue;
                }

                PageRect bounds = ItemGeometry.Bounds(text, NotoTextMeasurer.Instance);
                if (items.Skip(i + 1).Any(later => IsWhiteout(later) && ItemGeometry.Bounds(later, NotoTextMeasurer.Instance).Intersects(bounds)))
                {
                    covered.Add(text.Id);
                }
            }
        }

        return covered;
    }

    /// <summary>
    /// Text as filled glyph outlines, laid out like <see cref="DrawText"/> (no kerning, the same line spacing), for text
    /// a whiteout covers: redaction then removes or flattens the outlines, and no font records the characters.
    /// </summary>
    private static PdfiumObject? DrawTextAsOutlines(PdfiumPageEditor page, Affine itemToPdf, TextItem text)
    {
        using var noto = new SKFont(TextFontChoice.Primary.Typeface, (float)text.FontSize);
        double ascent = -noto.Metrics.Ascent;
        var commands = new List<PathCommand>();
        IReadOnlyList<TextRun>[] lines = RunsOf(text);
        double[] offsets = LineOffsets(text, lines);
        for (int line = 0; line < lines.Length; line++)
        {
            double baseline = (line * text.FontSize * TextItem.LineHeight) + ascent;
            double x = offsets[line];
            foreach (TextRun run in lines[line])
            {
                using var font = new SKFont(run.Source.Typeface, (float)text.FontSize);
                using SKPath outline = font.GetTextPath(run.Text, new SKPoint((float)x, (float)baseline));
                commands.AddRange(ToCommands(outline));
                x += run.Width;
            }
        }

        return text.IsBold
            ? DrawPath(page, itemToPdf, commands, Rgba(text.Color), Rgba(text.Color), text.FontSize * TextItem.BoldStroke)
            : DrawPath(page, itemToPdf, commands, Rgba(text.Color), stroke: null, 0);
    }

    private static IReadOnlyList<TextRun>[] RunsOf(TextItem text) =>
        [.. text.Lines.Select(line => TextFontChoice.Runs(line, text.FontSize))];

    /// <summary>Where each line starts for its alignment (FS-952), from the widths the runs are drawn at.</summary>
    private static double[] LineOffsets(TextItem text, IReadOnlyList<TextRun>[] lines)
    {
        double[] widths = [.. lines.Select(runs => runs.Sum(run => run.Width))];
        double box = widths.Length == 0 ? 0 : widths.Max();
        double share = text.Align switch
        {
            TextAlign.Centre => 0.5,
            TextAlign.Right => 1,
            _ => 0,
        };
        return [.. widths.Select(width => (box - width) * share)];
    }

    private static IEnumerable<PathCommand> ToCommands(SKPath path)
    {
        using SKPath.RawIterator iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        static PagePoint At(SKPoint point) => new(point.X, point.Y);
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    yield return new MoveTo(At(points[0]));
                    break;
                case SKPathVerb.Line:
                    yield return new LineTo(At(points[1]));
                    break;
                case SKPathVerb.Quad:
                case SKPathVerb.Conic:
                    // TrueType outlines are quadratic; a conic's weight is close enough to a quad's for covered text.
                    yield return new QuadTo(At(points[1]), At(points[2]));
                    break;
                case SKPathVerb.Cubic:
                    yield return new CubicTo(At(points[1]), At(points[2]), At(points[3]));
                    break;
                case SKPathVerb.Close:
                    yield return ClosePath.Instance;
                    break;
            }
        }
    }

    /// <summary>The box an item covers, in PDF user space. Pages turn in quarter turns, so it stays a rectangle.</summary>
    private static PdfBox AreaInPdf(PageTransform transform, Item item)
    {
        Affine itemToPdf = transform.ItemToPdf(new PagePoint(item.X, item.Y));
        PageSize size = item is ShapeItem shape ? new PageSize(shape.Width, shape.Height) : default;
        (double X, double Y)[] corners =
        [
            itemToPdf.Apply(0, 0), itemToPdf.Apply(size.Width, 0), itemToPdf.Apply(0, size.Height), itemToPdf.Apply(size.Width, size.Height),
        ];
        return new PdfBox(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    /// <summary>Draws one item; returns the path object for a whiteout, so what it covers can be found.</summary>
    private static PdfiumObject? DrawItem(PdfiumDocument pdf, PdfiumPageEditor page, PageTransform transform, EmbeddedFonts fonts, Item item)
    {
        Affine itemToPdf = transform.ItemToPdf(new PagePoint(item.X, item.Y));
        switch (item)
        {
            case TextItem text when !fonts.IsEmpty:
                DrawText(pdf, page, itemToPdf, fonts, text);
                break;
            case MarkItem mark:
                DrawPath(page, itemToPdf, ItemPaths.MarkPath(mark), fill: null, Rgba(mark.Color), ItemPaths.MarkStrokeWidth(mark));
                break;
            case ShapeItem { Kind: ShapeKind.Highlight } highlight:
                // Multiplied onto the page, so text under it stays readable (FR-8.6). The colour is the screen's
                // see-through yellow as it looks on white paper.
                DrawPath(page, itemToPdf, ItemPaths.ShapePath(highlight), HighlightOnWhite(), stroke: null, 0, "Multiply");
                break;
            case ShapeItem { Kind: ShapeKind.Whiteout } whiteout:
                return DrawPath(page, itemToPdf, ItemPaths.ShapePath(whiteout), (255, 255, 255, 255), stroke: null, 0);
            case ShapeItem shape:
                DrawPath(page, itemToPdf, ItemPaths.ShapePath(shape), fill: null, Rgba(shape.Color), shape.StrokeWidth);
                break;
            case InkItem ink:
                DrawPath(page, itemToPdf, ItemPaths.InkPath(ink), fill: null, Rgba(ink.Color), ink.StrokeWidth);
                break;
            case SignatureItem signature:
                DrawImage(page, itemToPdf, signature.Image, signature.Width, signature.Height);
                break;
            case PictureItem picture:
                DrawImage(page, itemToPdf, picture.Image, picture.Width, picture.Height);
                break;
        }

        return null;
    }

    /// <summary>
    /// One text object per line. Lines are 1.2 × the font size apart and each baseline sits at the line's top plus the
    /// font's ascent, exactly as the editor lays them out (docs/FILE-FORMAT.md).
    /// </summary>
    private static void DrawText(PdfiumDocument pdf, PdfiumPageEditor page, Affine itemToPdf, EmbeddedFonts fonts, TextItem text)
    {
        float size = (float)text.FontSize;
        double ascent = NotoAscent(pdf, fonts, size);
        float boldStroke = text.IsBold ? (float)(text.FontSize * TextItem.BoldStroke) : 0;
        IReadOnlyList<TextRun>[] lines = RunsOf(text);
        double[] offsets = LineOffsets(text, lines);
        for (int line = 0; line < lines.Length; line++)
        {
            // Text space has y up; item space has y down from the item's top. Runs in other fonts (FS-612) follow on
            // from each other along the same baseline.
            double baseline = (line * text.FontSize * TextItem.LineHeight) + ascent;
            double x = offsets[line];
            foreach (TextRun run in lines[line])
            {
                if (fonts.FontFor(run.Source) is { } font)
                {
                    Affine matrix = Affine.Compose(new Affine(1, 0, 0, -1, x, baseline), itemToPdf);
                    page.AddText(font, size, run.Text, (matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F), (text.Color.R, text.Color.G, text.Color.B), boldStroke);
                }

                x += run.Width;
            }
        }
    }

    /// <summary>The first baseline sits at the top plus Noto's ascent, as on screen, whichever fonts the line uses.</summary>
    private static double NotoAscent(PdfiumDocument pdf, EmbeddedFonts fonts, float size)
    {
        if (fonts.FontFor(TextFontChoice.Primary) is { } noto)
        {
            return pdf.GetAscent(noto, size);
        }

        using var font = new SKFont(TextFontChoice.Primary.Typeface, size);
        return -font.Metrics.Ascent;
    }

    /// <summary>Writes an item-local path list (the same one the screen draws) as one PDF path object.</summary>
    private static PdfiumObject? DrawPath(
        PdfiumPageEditor page,
        Affine itemToPdf,
        IReadOnlyList<PathCommand> commands,
        (byte R, byte G, byte B, byte A)? fill,
        (byte R, byte G, byte B, byte A)? stroke,
        double strokeWidth,
        string? blendMode = null)
    {
        if (commands.Count == 0 || commands[0] is not MoveTo start)
        {
            return null;
        }

        (float X, float Y) ToPdf(PagePoint point)
        {
            (double x, double y) = itemToPdf.Apply(point.X, point.Y);
            return ((float)x, (float)y);
        }

        (float startX, float startY) = ToPdf(start.To);
        return page.AddPath(
            startX,
            startY,
            path =>
            {
                PagePoint current = start.To;
                PagePoint figureStart = start.To;
                foreach (PathCommand command in commands.Skip(1))
                {
                    switch (command)
                    {
                        case MoveTo move:
                            (float mx, float my) = ToPdf(move.To);
                            path.MoveTo(mx, my);
                            current = figureStart = move.To;
                            break;
                        case LineTo line:
                            (float lx, float ly) = ToPdf(line.To);
                            path.LineTo(lx, ly);
                            current = line.To;
                            break;
                        case QuadTo quad:
                            // A quadratic curve is the cubic whose controls sit two thirds of the way to its control.
                            var c1 = new PagePoint(current.X + (2.0 / 3 * (quad.Control.X - current.X)), current.Y + (2.0 / 3 * (quad.Control.Y - current.Y)));
                            var c2 = new PagePoint(quad.To.X + (2.0 / 3 * (quad.Control.X - quad.To.X)), quad.To.Y + (2.0 / 3 * (quad.Control.Y - quad.To.Y)));
                            AddCurve(path, ToPdf(c1), ToPdf(c2), ToPdf(quad.To));
                            current = quad.To;
                            break;
                        case CubicTo cubic:
                            AddCurve(path, ToPdf(cubic.Control1), ToPdf(cubic.Control2), ToPdf(cubic.To));
                            current = cubic.To;
                            break;
                        case ClosePath:
                            path.Close();
                            current = figureStart;
                            break;
                    }
                }
            },
            fill,
            stroke,
            (float)strokeWidth,
            blendMode);
    }

    private static void AddCurve(PdfiumPageEditor.PathBuilder path, (float X, float Y) c1, (float X, float Y) c2, (float X, float Y) to) =>
        path.CurveTo(c1.X, c1.Y, c2.X, c2.Y, to.X, to.Y);

    /// <summary>A signature's or picture's PNG with its transparency, stretched over its box and kept upright (FR-6.5).</summary>
    private static void DrawImage(PdfiumPageEditor page, Affine itemToPdf, SignatureImage image, double width, double height)
    {
        using SKBitmap? decoded = SKBitmap.Decode(image.Png.AsSpan());
        if (decoded is null)
        {
            // A damaged image is left out rather than failing the whole export; the editor draws nothing for it too.
            return;
        }

        using SKBitmap straight = new(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        // ReadPixels converts to straight alpha; CopyTo would keep the pixels premultiplied and darken soft edges.
        if (!decoded.PeekPixels().ReadPixels(straight.Info, straight.GetPixels(), straight.RowBytes, 0, 0))
        {
            return;
        }

        // The image's unit square has y up; its top-left goes to the item's top-left.
        Affine matrix = Affine.Compose(new Affine(width, 0, 0, -height, 0, height), itemToPdf);
        page.AddImage(straight.GetPixelSpan(), straight.Width, straight.Height, (matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F));
    }

    private static (byte R, byte G, byte B, byte A) Rgba(InkColor color) => (color.R, color.G, color.B, 255);

    private static (byte R, byte G, byte B, byte A) HighlightOnWhite()
    {
        InkColor yellow = ItemPaths.HighlightColor;
        static byte Mix(byte channel) => (byte)Math.Round((channel * ItemPaths.HighlightOpacity) + (255 * (1 - ItemPaths.HighlightOpacity)));
        return (Mix(yellow.R), Mix(yellow.G), Mix(yellow.B), 255);
    }
}
