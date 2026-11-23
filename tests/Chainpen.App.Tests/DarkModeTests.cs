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
