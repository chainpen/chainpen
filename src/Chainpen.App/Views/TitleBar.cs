using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;

namespace Chainpen.App.Views;

/// <summary>
/// On macOS the window's content reaches up into the title bar, so the editor's document status strip and the start
/// screen's header are the title bar: the window buttons sit on their left, and they drag and double-click like
/// one. That saves a whole row. Windows and Linux keep the system title bar.
/// </summary>
internal static class TitleBar
{
    /// <summary>Matches the strip's height (TitleStripHeight), so the window buttons sit in its middle.</summary>
    public const double Height = 40;

    // Linux: Avalonia 12 can draw its own title bar (X11PlatformOptions.EnableDrawnDecorations), but it's marked
    // experimental, and on GNOME it drew its title over the menus and non-native window buttons, so it stays off.
    public static bool IsMerged => OperatingSystem.IsMacOS();

    public static void MergeInto(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!IsMerged)
        {
            return;
        }

        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = Height;
    }

    /// <summary>
    /// On Windows and Linux the menus sit at the left of the strip (File to Help, about 280 points), so what's centred
    /// in it keeps clear of them on both sides.
    /// </summary>
    public static void KeepClearOfMenus(Control centred)
    {
        ArgumentNullException.ThrowIfNull(centred);
        if (!IsMerged)
        {
            centred.Margin = new Avalonia.Thickness(MenusWidth, 0);
        }
    }

    private const double MenusWidth = 290;

    /// <summary>Lets a strip move the window when dragged, and zoom it on a double-click, as the system title bar does.</summary>
    public static void ActsAsTitleBar(Control strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        if (IsMerged)
        {
            WindowDecorationProperties.SetElementRole(strip, WindowDecorationsElementRole.TitleBar);
        }
    }
}
