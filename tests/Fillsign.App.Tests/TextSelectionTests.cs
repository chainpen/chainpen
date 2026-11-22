using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>Selected text in a box stays readable in both themes (WCAG 1.4.3).</summary>
public sealed class TextSelectionTests
{
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Selected_text_keeps_at_least_4_5_to_1_contrast(string theme)
    {
        ThemeVariant variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var box = new TextBox { Text = "document-final.pdf" };
        var window = new Window { RequestedThemeVariant = variant, Content = box, Width = 400, Height = 100 };
        window.Show();
        try
        {
            await Pump(() => box.IsLoaded);
            Color selection = Assert.IsAssignableFrom<ISolidColorBrush>(box.SelectionBrush).Color;
            Color text = Assert.IsAssignableFrom<ISolidColorBrush>(box.SelectionForegroundBrush).Color;
            double contrast = Contrast(text, selection);
            Assert.True(contrast >= 4.5, $"{theme}: selected text is {contrast:0.0}:1 on its highlight");
        }
        finally
        {
            window.Close();
        }
    }

    private static double Contrast(Color a, Color b)
    {
        double lighter = Math.Max(Luminance(a), Luminance(b));
        double darker = Math.Min(Luminance(a), Luminance(b));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color) =>
        (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

    private static double Channel(byte value)
    {
        double c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
