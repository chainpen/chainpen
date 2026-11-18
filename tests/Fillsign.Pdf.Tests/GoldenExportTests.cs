using System.Collections.Immutable;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Pdf.Export;
using Fillsign.Pdf.Pdfium;
using Fillsign.Pdf.Signatures;
using SkiaSharp;

namespace Fillsign.Pdf.Tests;

/// <summary>
/// FS-604: exports the acceptance scenarios (REQUIREMENTS §7) and compares every page with a reviewed reference
/// image in tests/Fillsign.Pdf.Tests/Golden. To accept a deliberate change, run with FILLSIGN_UPDATE_GOLDEN=1, look
/// at the new images, and commit them.
/// </summary>
public sealed class GoldenExportTests
{
    // A pixel differs when a channel is off by more than this; a page passes with up to 0.2 % of pixels differing,
    // which allows for anti-aliasing differences between platforms but not a moved or missing item.
    private const int ChannelTolerance = 48;
    private const double MaxDifferingFraction = 0.002;
    private const double RenderScale = 1;

    private static readonly string s_golden = Path.Combine(TestFiles.RepoRoot, "tests", "Fillsign.Pdf.Tests", "Golden");
    private static readonly string s_actual = Path.Combine(TestFiles.RepoRoot, "artifacts", "golden-actual");

    /// <summary>Scenario 2: a two-page form whose second page is rotated; a tick, a name and a drawn signature.</summary>
    [Fact]
    public void Scenario_2_form_with_a_rotated_page()
    {
        (byte[] source, SourcePage[] pages) = DocumentWriterTests.Source("sample-form.pdf");
        var state = DocumentState.ForSourcePages(pages.Length);
        PageId first = state.Pages[0].Id, second = state.Pages[1].Id;
        SignatureImage signature = SignatureImageMaker.FromInk(
            [[new InkSample(10, 40, 0.5), new InkSample(40, 10, 0.5), new InkSample(70, 45, 0.5), new InkSample(110, 15, 0.5), new InkSample(150, 40, 0.5)]],
            InkColor.Blue)!;
        DocumentState document = state.WithItems(
        [
            new MarkItem(ItemId.New(), first, 60, 126, MarkKind.Tick, 16, InkColor.Blue),
            new TextItem(ItemId.New(), first, 116, 282, "Jordan Lee", 14, InkColor.Blue),
            new SignatureItem(ItemId.New(), first, 150, 322, SignatureKind.Signature, signature, 150, 150.0 * signature.PixelHeight / signature.PixelWidth),
            new MarkItem(ItemId.New(), second, 120, 90, MarkKind.Cross, 18, InkColor.Black),
            new TextItem(ItemId.New(), second, 140, 140, "Second page", 12, InkColor.Black),
        ]);

        Compare("scenario-2", DocumentWriter.FinalCopy(source, pages, document).Pdf);
    }

    /// <summary>Scenario 4: every shape type and a two-stroke freehand drawing.</summary>
    [Fact]
    public void Scenario_4_every_shape_and_a_drawing()
    {
        (byte[] source, SourcePage[] pages) = DocumentWriterTests.Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(pages.Length);
        PageId page = state.Pages[1].Id;
        ShapeItem Shape(ShapeKind kind, double x, double y, double w, double h, bool flipY = false) =>
            new(ItemId.New(), page, x, y, kind, w, h, InkColor.Red, 2, false, flipY);
        ImmutableArray<ImmutableArray<PagePoint>> strokes =
        [
            [new(0, 0.5), new(0.2, 0.1), new(0.4, 0.9), new(0.6, 0.2)],
            [new(0.5, 1), new(0.8, 0.4), new(1, 0.8)],
        ];
        DocumentState document = state.WithPages([state.Pages[1]]).WithItems(
        [
            new TextItem(ItemId.New(), page, 72, 300, "Highlighted words", 18, InkColor.Black),
            Shape(ShapeKind.Rectangle, 72, 120, 120, 60),
            Shape(ShapeKind.Ellipse, 230, 120, 120, 60),
            Shape(ShapeKind.Line, 380, 120, 100, 60),
            Shape(ShapeKind.Arrow, 72, 210, 120, 60, flipY: true),
            Shape(ShapeKind.Highlight, 66, 296, 170, 28),
            Shape(ShapeKind.Whiteout, 230, 210, 120, 60),
            new InkItem(ItemId.New(), page, 380, 210, 120, 80, strokes, InkColor.Blue, 3),
        ]);

        Compare("scenario-4", DocumentWriter.FinalCopy(source, pages, document).Pdf);
    }

    /// <summary>Scenario 5: tick A, rotate it, move D to the top, delete C: D, A (rotated, tick beside its text), B.</summary>
    [Fact]
    public void Scenario_5_reorder_rotate_and_delete()
    {
        (byte[] source, SourcePage[] pages) = DocumentWriterTests.Source("four-pages.pdf");
        var state = DocumentState.ForSourcePages(pages.Length);
        var tick = new MarkItem(ItemId.New(), state.Pages[0].Id, 60, 60, MarkKind.Tick, 20, InkColor.Blue);
        DocumentState ticked = state.WithItems([tick]);
        PageSize size = new PageTransform(pages[0], Rotation.None).DisplaySize;
        DocumentState rotated = Core.Editing.PageEdits.Rotate(ticked, new HashSet<PageId> { state.Pages[0].Id }, clockwise: true, _ => size, ApproximateTextMeasurer.Instance);
        DocumentState document = rotated.WithPages([rotated.Pages[3], rotated.Pages[0], rotated.Pages[1]]);

        Compare("scenario-5", DocumentWriter.FinalCopy(source, pages, document).Pdf);
    }

    private static void Compare(string name, byte[] pdf)
    {
        using PdfiumDocument document = PdfiumDocument.Open(pdf);
        bool isUpdating = Environment.GetEnvironmentVariable("FILLSIGN_UPDATE_GOLDEN") == "1";
        var failures = new List<string>();
        for (int page = 0; page < document.PageCount; page++)
        {
            using SKBitmap actual = Render(document, page);
            string file = $"{name}-page{page + 1}.png";
            string reference = Path.Combine(s_golden, file);
            if (isUpdating)
            {
                Directory.CreateDirectory(s_golden);
                Save(actual, reference);
                continue;
            }

            Directory.CreateDirectory(s_actual);
            Save(actual, Path.Combine(s_actual, file));
            if (!File.Exists(reference))
            {
                failures.Add($"{file}: no reference image (run with FILLSIGN_UPDATE_GOLDEN=1, review, commit)");
                continue;
            }

            // Decoding may give RGBA; compare in the renderer's BGRA order.
            using SKBitmap decoded = SKBitmap.Decode(reference);
            using SKBitmap expected = decoded.Copy(SKColorType.Bgra8888);
            if (expected.Width != actual.Width || expected.Height != actual.Height)
            {
                failures.Add($"{file}: size {actual.Width}×{actual.Height}, expected {expected.Width}×{expected.Height}");
                continue;
            }

            double differing = DifferingFraction(expected, actual);
            if (differing > MaxDifferingFraction)
            {
                failures.Add($"{file}: {differing:P2} of pixels differ (see artifacts/golden-actual)");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static SKBitmap Render(PdfiumDocument document, int page)
    {
        PdfPageInfo info = document.GetPageInfo(page);
        int width = (int)Math.Round(info.Width * RenderScale), height = (int)Math.Round(info.Height * RenderScale);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        document.RenderPage(page, bitmap.GetPixelSpan(), width, height, bitmap.RowBytes);
        return bitmap;
    }

    private static double DifferingFraction(SKBitmap expected, SKBitmap actual)
    {
        ReadOnlySpan<byte> a = expected.GetPixelSpan(), b = actual.GetPixelSpan();
        int differing = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            bool differs = Math.Abs(a[i] - b[i]) > ChannelTolerance
                || Math.Abs(a[i + 1] - b[i + 1]) > ChannelTolerance
                || Math.Abs(a[i + 2] - b[i + 2]) > ChannelTolerance;
            differing += differs ? 1 : 0;
        }

        return differing / (a.Length / 4.0);
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, png.ToArray());
    }
}
