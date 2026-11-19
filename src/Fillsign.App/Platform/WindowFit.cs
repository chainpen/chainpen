using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Fillsign.App.Platform;

/// <summary>
/// Keeps a window within the screen it opens on (NFR-11). Windows are designed at a set size, but a laptop screen at
/// 150% scaling has about 750 points above the taskbar, so a taller window would open with its title bar, and the
/// close button, off the top of the screen.
/// </summary>
public static class WindowFit
{
    /// <summary>Shrinks the window to the screen's working area if needed, then centres it there with its title bar showing.</summary>
    public static void KeepOnScreen(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Screen? screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null)
        {
            return;
        }

        PixelRect area = screen.WorkingArea;
        double scaling = screen.Scaling;

        // The title bar and borders, which the window's own size doesn't include.
        Size frame = window.FrameSize ?? window.ClientSize;
        double chromeWidth = Math.Max(0, frame.Width - window.ClientSize.Width);
        double chromeHeight = Math.Max(0, frame.Height - window.ClientSize.Height);
        double maxWidth = (area.Width / scaling) - chromeWidth;
        double maxHeight = (area.Height / scaling) - chromeHeight;

        if (window.Bounds.Width > maxWidth)
        {
            window.MinWidth = Math.Min(window.MinWidth, maxWidth);
            window.Width = maxWidth;
        }

        if (window.Bounds.Height > maxHeight)
        {
            window.MinHeight = Math.Min(window.MinHeight, maxHeight);
            window.Height = maxHeight;
        }

        int frameWidth = (int)Math.Ceiling((Math.Min(window.Width, maxWidth) + chromeWidth) * scaling);
        int frameHeight = (int)Math.Ceiling((Math.Min(window.Height, maxHeight) + chromeHeight) * scaling);
        int x = area.X + Math.Max(0, (area.Width - frameWidth) / 2);
        int y = area.Y + Math.Max(0, (area.Height - frameHeight) / 2);
        if (window.Position.Y < area.Y || window.Position.X < area.X || window.Position.Y + frameHeight > area.Bottom || window.Position.X + frameWidth > area.Right)
        {
            window.Position = new PixelPoint(x, y);
        }
    }
}
