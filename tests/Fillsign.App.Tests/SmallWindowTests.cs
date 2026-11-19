using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Fillsign.App.ViewModels;
using Fillsign.App.Views;
using Fillsign.Core.Model;
using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>FS-709 (S5): on a short window, tall dialogs scroll their middle and keep their buttons in reach.</summary>
public sealed class SmallWindowTests
{
    [AvaloniaFact]
    public async Task Export_and_signature_dialogs_keep_their_buttons_inside_a_short_window()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1024, Height = 600 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            editor.PointerPressed(0, new PagePoint(400, 600), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(400, 600));
            editor.SelectToolCommand.Execute(EditorTool.Select);

            editor.ExportCommand.Execute(null);
            await Pump(() => DialogOf(window) is ExportDialog);
            await Pump(() => true);
            Save(window, "fs709-export-short-window");
            AssertInside(window, ButtonIn(DialogOf(window)!, "Export final copy"));
            Click(DialogOf(window)!, "Keep editing");
            await Pump(() => DialogOf(window) is null);

            var choosing = window.ChooseSignatureAsync();
            await Pump(() => DialogOf(window) is SignatureDialog);
            await Pump(() => true);
            Save(window, "fs709-signature-short-window");
            AssertInside(window, ButtonIn(DialogOf(window)!, "Cancel"));
            Click(DialogOf(window)!, "Cancel");
            await choosing;
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    private static void AssertInside(Window window, Button button)
    {
        Point? bottomRight = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window);
        Assert.NotNull(bottomRight);
        Assert.True(bottomRight.Value.Y <= window.Bounds.Height, $"button bottom {bottomRight.Value.Y:0} is below the window ({window.Bounds.Height:0})");
    }

    private static Button ButtonIn(Control root, string content) =>
        root.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && Equals(button.Content, content));

    private static Control? DialogOf(MainWindow window) => window.FindControl<ContentControl>("DialogHost")!.Content as Control;
}
