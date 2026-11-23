using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Chainpen.App.Platform.Signatures;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using Chainpen.Core.Model;

using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>FS-807: the newer screens in the dark appearance, saved for review (the page itself stays light).</summary>
public sealed class DarkModeTests
{
    [AvaloniaFact]
    public async Task Organise_save_menu_notices_and_signature_dialog_in_dark()
    {
        Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.Dark });
        await Pump(() => Application.Current!.ActualThemeVariant == ThemeVariant.Dark);
        MainWindow.SignatureStoreOverride = new SavedSignatureStore(Path.Combine(Path.GetTempPath(), "chainpen-tests", Guid.NewGuid().ToString("N")), new NoKeyVault());
        try
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            EditorViewModel editor = Editor(window);
            await Pump(() => !editor.IsLoading);
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            editor.PointerPressed(0, new PagePoint(62, 128), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(62, 128));
            editor.SelectToolCommand.Execute(EditorTool.Select);
            editor.SelectAllOnCurrentPage();
            Platform.AppUpdates.Instance.ShowReady(new Platform.UpdateReady("1.1", ["Faster opening for large scans"]));
            await Pump(() => true);
            Save(window, "phase8-dark-editor");
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);

            editor.ShowOrganiseCommand.Execute(null);
            editor.SetPageSelection([editor.State.Pages[0].Id]);
            await Pump(() => true);
            Save(window, "phase8-dark-organise");

            editor.ShowEditContentCommand.Execute(null);
            editor.SelectToolCommand.Execute(EditorTool.Sign);
            await Pump(() => window.FindControl<ContentControl>("DialogHost")!.Content is SignatureDialog);
            ((SignatureDialogViewModel)((SignatureDialog)window.FindControl<ContentControl>("DialogHost")!.Content!).DataContext!).Tab = SignatureTab.Type;
            await Pump(() => true);
            Save(window, "phase8-dark-signature");
            ((SignatureDialogViewModel)((SignatureDialog)window.FindControl<ContentControl>("DialogHost")!.Content!).DataContext!).CancelCommand.Execute(null);
            window.CloseWithoutAsking();
        }
        finally
        {
            MainWindow.SignatureStoreOverride = null;
            Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.MatchSystem });
        }
    }

    /// <summary>
    /// A primary button, at rest and under the pointer, keeps its white label readable (4.5:1) and its edge visible
    /// against the title strip (3:1), in both appearances. The hover fill once used AccentStrong, the dark theme's
    /// palest violet.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Platform.AppearanceChoice.Light)]
    [InlineData(Platform.AppearanceChoice.Dark)]
    public async Task A_primary_button_keeps_its_label_and_edge_readable_at_rest_and_hovered(Platform.AppearanceChoice appearance)
    {
        Platform.AppSettings.Update(settings => settings with { Appearance = appearance });
        try
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            EditorViewModel editor = Editor(window);
            await Pump(() => !editor.IsLoading);

            Button export = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ExportButton");
            Point topLeft = export.TranslatePoint(default, window)!.Value;
            Rect box = new(topLeft, export.Bounds.Size);
            await Pump(() => true);
            AssertReadable(window, box, "at rest");

            window.MouseMove(box.Center, RawInputModifiers.None);
            await Pump(() => export.IsPointerOver);
            Save(window, $"primary-hover-{appearance.ToString().ToLowerInvariant()}");
            AssertReadable(window, box, "hovered");
            window.CloseWithoutAsking();
        }
        finally
        {
            Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.MatchSystem });
        }
    }

    /// <summary>
    /// Filled buttons (the violet primary and the red one that confirms removing something) keep a readable label
    /// (4.5:1) and a visible edge (3:1) on every surface they can sit on, at rest and under the pointer, in both
    /// appearances.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Filled_buttons_stay_readable_on_every_surface(string variant)
    {
        string[] surfaces = ["SurfaceApp", "Surface", "SurfaceSubtle", "SurfaceSunken"];
        string[] kinds = ["primary", "danger-fill"];
        var rows = new StackPanel();
        var buttons = new List<(Button Button, string Name)>();
        foreach (string surface in surfaces)
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 24 };
            var band = new Border { Padding = new Thickness(24), Child = row };
            band[!Border.BackgroundProperty] = band.GetResourceObservable(surface).ToBinding();
            foreach (string kind in kinds)
            {
                var button = new Button { Content = "Export final copy", Classes = { kind } };
                row.Children.Add(button);
                buttons.Add((button, $"{kind} on {surface}"));
            }

            rows.Children.Add(band);
        }

        var window = new Window
        {
            Width = 600,
            Height = 600,
            Content = rows,
            RequestedThemeVariant = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        window.Show();
        await Pump(() => buttons.All(entry => entry.Button.Bounds.Width > 0));

        foreach ((Button button, string name) in buttons)
        {
            Point topLeft = button.TranslatePoint(default, window)!.Value;
            Rect box = new(topLeft, button.Bounds.Size);
            window.MouseMove(new Point(1, 1), RawInputModifiers.None);
            await Pump(() => !button.IsPointerOver);
            AssertReadable(window, box, $"{variant}: {name}, at rest");
            window.MouseMove(box.Center, RawInputModifiers.None);
            await Pump(() => button.IsPointerOver);
            AssertReadable(window, box, $"{variant}: {name}, hovered");
        }

        window.Close();
    }

    private static void AssertReadable(Window window, Rect box, string state)
    {
        using WriteableBitmap frame = window.CaptureRenderedFrame()!;
        // Inside the button, clear of its label and rounded corners; its outermost pixels; and the strip above it.
        (byte, byte, byte) fill = Pixel(frame, (int)(box.X + 6), (int)box.Center.Y);
        (byte, byte, byte) strip = Pixel(frame, (int)box.Center.X, (int)(box.Y - 4));
        double edge = Enumerable.Range(0, 2).Max(inset => Contrast(Pixel(frame, (int)box.X + inset, (int)box.Center.Y), strip));
        Assert.True(Contrast((255, 255, 255), fill) >= 4.5, $"{state}: white on the fill {fill} is {Contrast((255, 255, 255), fill):0.00}:1");
        Assert.True(edge >= 3, $"{state}: the button's edge on the strip {strip} is {edge:0.00}:1");
    }
}
