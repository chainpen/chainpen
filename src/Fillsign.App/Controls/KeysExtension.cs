using Avalonia.Markup.Xaml;

namespace Fillsign.App.Controls;

/// <summary>
/// Shortcut wording for this computer in XAML: <c>{c:Keys 'Undo (Mod+Z)'}</c> reads "Undo (⌘Z)" on a Mac and
/// "Undo (Ctrl+Z)" elsewhere, and "Mod-click" becomes "⌘-click" or "Ctrl-click". Key badges ("Mod Shift S") read
/// "⌘ ⇧ S" or "Ctrl Shift S".
/// </summary>
public sealed class KeysExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => ForThisComputer(Text, OperatingSystem.IsMacOS());

    /// <summary>The same conversion, for code and tests; <paramref name="isMac"/> picks the wording.</summary>
    public static string ForThisComputer(string text, bool isMac)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!isMac)
        {
            return text.Replace("Mod", "Ctrl", StringComparison.Ordinal);
        }

        // Mac menus write modifiers as symbols, Shift before Command, with no plus signs: ⇧⌘Z.
        return text
            .Replace("Mod+Shift+", "⇧⌘", StringComparison.Ordinal)
            .Replace("Mod+", "⌘", StringComparison.Ordinal)
            .Replace("Mod Shift ", "⌘ ⇧ ", StringComparison.Ordinal)
            .Replace("Mod", "⌘", StringComparison.Ordinal);
    }
}
