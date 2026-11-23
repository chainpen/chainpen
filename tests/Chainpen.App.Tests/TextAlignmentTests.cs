using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Chainpen.Pdf.Fonts;
using Chainpen.Pdf.Pdfium;

namespace Chainpen.App.Tests;

/// <summary>
/// FS-023: text drawn by Avalonia on screen lands on the same pixels as the text the PDFium writer exports
/// (rendered back with PDFium). Uses kerning pairs (AV, Wa, To, Ty) to expose shaping differences.
/// </summary>
public sealed class TextAlignmentTests(ITestOutputHelper output)
{
    private const int PageWidth = 900;
    private const int PageHeight = 160;
    private const double X = 40;
    private const double Y = 40;

    /// <summary>FS-023/FS-024: Avalonia text vs the PDFium writer (HarfBuzz-subset Noto via FPDFText_LoadFont).</summary>
    [AvaloniaTheory]
    [InlineData("AVAILABLE Wave Today 17/11/2026", 48)]
    [InlineData("Chainpen Ünïcödé Ελληνικά Кириллица", 36)]
    [InlineData("Jane Citizen", 12)]
    public void Screen_text_matches_pdfium_export(string text, double fontSize)
    {
        var screen = InkBounds(RenderOnScreen(text, fontSize));
        var export = InkBounds(RenderPdfiumExport(text, fontSize));

        output.WriteLine($"screen {screen}, pdfium export {export}");
        Assert.InRange(screen.Left - export.Left, -1, 1);
        Assert.InRange(screen.Right - export.Right, -1, 1);
        Assert.InRange(screen.Top - export.Top, -1, 1);
        Assert.InRange(screen.Bottom - export.Bottom, -1, 1);
    }

    private static (byte[] Bgra, int Stride) RenderPdfiumExport(string text, double fontSize)
    {
        using var doc = PdfiumDocument.CreateNew();
        doc.AddBlankPage(0, PageWidth, PageHeight);
        var font = doc.LoadSubsetFont(BundledFonts.Load(BundledFonts.RegularFace), text);
        float ascent = doc.GetAscent(font, (float)fontSize);

        // PDF user space has y up: baseline = page height − (top + ascent).
        doc.EditPage(0, page => page.AddText(font, (float)fontSize, text, X, PageHeight - (Y + ascent), (0, 0, 0)));

        using var pdf = PdfiumDocument.Open(doc.SaveCopy(removeSecurity: false));
        var bytes = new byte[PageWidth * 4 * PageHeight];
        pdf.RenderPage(0, bytes, PageWidth, PageHeight, PageWidth * 4);
        return (bytes, PageWidth * 4);
    }

    private static (byte[] Bgra, int Stride) RenderOnScreen(string text, double fontSize)
    {
        var canvas = new Canvas { Background = Brushes.White, Width = PageWidth, Height = PageHeight };
        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("avares://Chainpen.App/Assets/Fonts#Noto Sans"),
            FontSize = fontSize,
            Foreground = Brushes.Black,

            // The export writes unkerned text, so the screen must not kern either or long lines drift apart.
            FontFeatures = [FontFeature.Parse("-kern")],
        };
        Canvas.SetLeft(block, X);
        Canvas.SetTop(block, Y);
        canvas.Children.Add(block);

        var window = new Window { Content = canvas, Width = PageWidth, Height = PageHeight };
        window.Show();
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        window.Close();
        Assert.NotNull(frame);

        using var buffer = frame.Lock();
        int stride = buffer.RowBytes;
        var bytes = new byte[stride * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return (bytes, stride);
    }

    private static (int Left, int Top, int Right, int Bottom) InkBounds((byte[] Bgra, int Stride) image)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < PageHeight; y++)
        {
            for (int x = 0; x < PageWidth; x++)
            {
                int i = (y * image.Stride) + (x * 4);
                if (image.Bgra[i] < 128 && image.Bgra[i + 1] < 128 && image.Bgra[i + 2] < 128)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        Assert.True(right >= 0, "no ink rendered");
        return (left, top, right, bottom);
    }
}
