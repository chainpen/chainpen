using Avalonia.Input;
using Fillsign.App.Platform;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

/// <summary>
/// FS-951: one letter picks a tool, with no modifier (V Select, T Text, C Tick, X Cross, D Date, S Sign, R Shapes,
/// P Draw). Off until turned on in Settings: most people filling a form won't know them, and a stray letter changing
/// tools is confusing; speech input users can leave them off (WCAG 2.1.4).
/// </summary>
internal static class ToolKeys
{
    /// <summary>The letters, in toolbar order, as the shortcuts list and tooltips show them.</summary>
    public static readonly IReadOnlyList<(Key Key, EditorTool Tool)> All =
    [
        (Key.V, EditorTool.Select),
        (Key.T, EditorTool.Text),
        (Key.C, EditorTool.Tick),
        (Key.X, EditorTool.Cross),
        (Key.D, EditorTool.Date),
        (Key.S, EditorTool.Sign),
        (Key.R, EditorTool.Shape),
        (Key.P, EditorTool.Draw),
    ];

    private static bool? s_isOn;

    static ToolKeys() => AppSettings.Changed += changed => s_isOn = changed.UseSingleKeyTools;

    /// <summary>Whether the letters are on (Settings → Keyboard, off by default). Read once, then kept up to date.</summary>
    public static bool IsOn => s_isOn ??= AppSettings.Load().UseSingleKeyTools;

    /// <summary>The tool a letter picks, if it's one of them and no modifier is held.</summary>
    public static EditorTool? ToolFor(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None && modifiers != KeyModifiers.Shift)
        {
            return null;
        }

        foreach ((Key letter, EditorTool tool) in All)
        {
            if (letter == key)
            {
                return tool;
            }
        }

        return null;
    }
}
