using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;

namespace Chainpen.App.Tests;

/// <summary>Helpers for driving windows headlessly. Frames are saved to artifacts/screenshots for review.</summary>
internal static class UiTest
{
    private static readonly string s_screenshots = Path.Combine(TestFiles.RepoRoot, "artifacts", "screenshots");

    internal static void Click(Control root, string content)
    {
        Button button = root.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, content));

        // Raising Click runs Click handlers but not a bound Command, so run the command as a real click would.
        if (button.Command is { } command)
        {
            command.Execute(button.CommandParameter);
            return;
        }

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    internal static EditorViewModel Editor(MainWindow window) =>
        ((MainWindowViewModel)window.DataContext!).Editor ?? throw new InvalidOperationException("No document is open.");

    /// <summary>Runs the UI thread and render loop until <paramref name="condition"/> holds (or 10 s pass).</summary>
    internal static async Task Pump(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(30);
        }
        while (!condition() && DateTime.UtcNow < timeout);

        Assert.True(condition(), "timed out waiting for the UI");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    internal static void Save(Window window, string name)
    {
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(s_screenshots);
        using FileStream file = File.Create(Path.Combine(s_screenshots, name + ".png"));
        frame.Save(file, PngBitmapEncoderOptions.Default);
    }

    /// <summary>One pixel of a captured frame.</summary>
    internal static (byte Red, byte Green, byte Blue) Pixel(WriteableBitmap frame, int x, int y)
    {
        using ILockedFramebuffer pixels = frame.Lock();
        byte[] pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(pixels.Address + (y * pixels.RowBytes) + (x * 4), pixel, 0, 4);
        return pixels.Format == PixelFormat.Bgra8888 ? (pixel[2], pixel[1], pixel[0]) : (pixel[0], pixel[1], pixel[2]);
    }

    /// <summary>WCAG 2.2 contrast ratio.</summary>
    internal static double Contrast((byte, byte, byte) a, (byte, byte, byte) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((byte Red, byte Green, byte Blue) colour) =>
        (0.2126 * Channel(colour.Red)) + (0.7152 * Channel(colour.Green)) + (0.0722 * Channel(colour.Blue));

    private static double Channel(byte value)
    {
        double c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
