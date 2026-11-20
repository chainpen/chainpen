using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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

    [AvaloniaFact]
    public async Task Toolbar_buttons_never_overlap_at_any_window_width()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1024, Height = 720 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            var problems = new List<string>();
            foreach (bool organising in (bool[])[false, true])
            {
                Editor(window).IsOrganising = organising;
                for (double width = 1024; width <= 1600; width += 16)
                {
                    window.Width = width;
                    await Pump(() => true);
                    Control toolbar = window.GetVisualDescendants().OfType<Border>().First(border => border.Name == "ToolbarBar");
                    Rect[] buttons = [.. toolbar.GetVisualDescendants().OfType<Button>()
                        .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0)
                        .Select(control => new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size))];
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        if (buttons[i].Right > width + 0.5)
                        {
                            problems.Add($"{width}px{(organising ? " organising" : string.Empty)}: a button runs off the window");
                        }

                        for (int j = i + 1; j < buttons.Length; j++)
                        {
                            Rect overlap = buttons[i].Intersect(buttons[j]);
                            if (overlap.Width > 0.5 && overlap.Height > 0.5)
                            {
                                problems.Add($"{width}px{(organising ? " organising" : string.Empty)}: buttons at x {buttons[i].X:0} and {buttons[j].X:0} overlap");
                            }
                        }
                    }
                }
            }

            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Selection_hint_trims_inside_the_window_instead_of_running_off_it()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1024, Height = 720 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);
            editor.SelectToolCommand.Execute(EditorTool.Text);
            editor.PointerPressed(0, new PagePoint(400, 600), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(400, 600));
            await Pump(() => editor.TypingItem is not null);
            window.KeyTextInput("Jordan Lee");
            editor.FinishTyping();
            editor.SelectToolCommand.Execute(EditorTool.Select);
            editor.SelectAllOnCurrentPage();
            await Pump(() => editor.ShowsSelectionActions);

            TextBlock hint = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Name == "OptionsHintText");
            Assert.StartsWith("Double-click to edit", hint.Text, StringComparison.Ordinal);
            var problems = new List<string>();
            for (double width = 1024; width <= 1600; width += 64)
            {
                window.Width = width;
                await Pump(() => true);
                double right = hint.TranslatePoint(new Point(hint.Bounds.Width, 0), window)!.Value.X;
                if (right > width + 0.5)
                {
                    problems.Add($"{width}px: the hint ends at {right:0}");
                }
            }

            Save(window, "selection-hint-1600");
            Assert.True(problems.Count == 0, string.Join("; ", problems));
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
