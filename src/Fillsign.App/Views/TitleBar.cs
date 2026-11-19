using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;

namespace Fillsign.App.Views;

/// <summary>
/// On macOS the window's content reaches up into the title bar, so the editor's document status strip and the start
/// screen's header are the title bar: the window buttons sit on their left, and they drag and double-click like
/// one. That saves a whole row. Windows and Linux keep the system title bar.
/// </summary>
internal static class TitleBar
{
    /// <summary>Matches the strip's height (TitleStripHeight), so the window buttons sit in its middle.</summary>
    public const double Height = 40;

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
