using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fillsign.App.Controls;
using Fillsign.App.ViewModels;
using Fillsign.App.Views;
using Fillsign.Core.Model;
using SkiaSharp;
using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>FR-1.8: the practice form's step-by-step guide, and the nudge towards the tools on pages without fields.</summary>
public sealed class PracticeGuideTests
{
    [AvaloniaFact]
    public async Task Guide_lights_each_tool_then_the_place_to_click_and_moves_on_as_each_step_is_done()
    {
        var window = new MainWindow(isFirstLaunch: true, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            await Pump(() => true);
            Click(window, "Try it with a practice form");
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false, IsGuiding: true } });
            EditorViewModel editor = Editor(window);
            var view = (EditorView)window.FindControl<ContentControl>("Host")!.Content!;
            var canvas = view.FindControl<PagesCanvas>("Canvas")!;
            await Pump(() => canvas.Bounds.Width > 0);
            Assert.True(view.FindControl<Border>("GuideCard")!.IsEffectivelyVisible);

            // Step 1: Tick is lit until chosen, then the box is outlined.
            Assert.Equal(GuideStep.Tick, editor.Guide);
            Assert.Contains("scan", editor.GuideText, StringComparison.Ordinal);
            Assert.Contains("guided", ToolButton(view, "Tick").Classes);
            Assert.Null(editor.GuideTarget);
            await Pump(() => view.FindControl<Avalonia.Controls.Shapes.Path>("GuideArrow")!.IsVisible);
            AssertArrowUnder(view, ToolButton(view, "Tick"));
            Save(window, "practice-guide-1-choose-tick");
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            Assert.DoesNotContain("guided", ToolButton(view, "Tick").Classes);
            PageRect box = Assert.NotNull(editor.GuideTarget).Area;
            await Pump(() => true);
            Save(window, "practice-guide-1-click-the-box");
            ClickPage(window, canvas, box);
            await Pump(() => editor.Guide == GuideStep.Name);

            // Undo takes the guide back a step; Redo forward again.
            editor.UndoCommand.Execute(null);
            Assert.Equal(GuideStep.Tick, editor.Guide);
            editor.RedoCommand.Execute(null);
            Assert.Equal(GuideStep.Name, editor.Guide);

            // Step 2: Text, then the name line; the step ends once the name is typed and finished.
            Assert.Contains("guided", ToolButton(view, "Text").Classes);
            editor.SelectToolCommand.Execute(EditorTool.Text);
            PageRect name = Assert.NotNull(editor.GuideTarget).Area;
            await Pump(() => true);
            ClickPage(window, canvas, name);
            await Pump(() => editor.TypingItem is not null);
            Assert.Null(editor.GuideTarget);
            window.KeyTextInput("Jordan Lee");
            Assert.Equal(GuideStep.Name, editor.Guide);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Pump(() => editor.Guide == GuideStep.Sign);

            // Step 3: Sign, then the signature line.
            Assert.Contains("guided", ToolButton(view, "Sign").Classes);
            editor.BeginPlacingSignature(Signature(), SignatureKind.Signature);
            var (_, line, label) = Assert.NotNull(editor.GuideTarget);
            Assert.Equal("Sign here", label);
            await Pump(() => true);
            Save(window, "practice-guide-3-sign-here");
            ClickPage(window, canvas, line);
            await Pump(() => editor.Guide == GuideStep.Export);

            // Step 4: Export is lit; opening it finishes the practice.
            Assert.Contains("guided", view.FindControl<Button>("ExportButton")!.Classes);
            await Pump(() => true);
            AssertArrowUnder(view, view.FindControl<Button>("ExportButton")!);
            await Pump(() => true);
            Save(window, "practice-guide-4-export");
            editor.ExportCommand.Execute(null);
            await Pump(() => editor.Guide == GuideStep.Done);
            await Pump(() => window.FindControl<ContentControl>("DialogHost")!.Content is ExportDialog);
            Click((Control)window.FindControl<ContentControl>("DialogHost")!.Content!, "Keep editing");
            await Pump(() => window.FindControl<ContentControl>("DialogHost")!.Content is null);
            Assert.Equal("Close guide", editor.GuideCloseLabel);
            Save(window, "practice-guide-done");
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Guide_is_read_out_and_closes_from_the_keyboard()
    {
        var (window, editor, view, _) = await OpenPracticeAsync();
        try
        {
            TextBlock text = view.FindControl<TextBlock>("GuideAnnouncement")!;
            Assert.Equal("Step 1 of 4. Tick a box. " + editor.GuideText, AutomationProperties.GetName(text));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(text));

            Button close = view.FindControl<Button>("CloseGuideButton")!;
            Assert.Equal("Skip guide", close.Content);
            Assert.True(close.Focus(NavigationMethod.Tab));
            await Pump(() => close.IsFocused);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Pump(() => !editor.IsGuiding);
            Assert.False(view.FindControl<Border>("GuideCard")!.IsVisible);
            await Pump(() => true);
            Assert.False(view.FindControl<Avalonia.Controls.Shapes.Path>("GuideArrow")!.IsVisible);
            Assert.DoesNotContain("guided", ToolButton(view, "Tick").Classes);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Arrow_bounces_on_arriving_and_survives_moving_and_hiding_after_a_bounce_ends()
    {
        // As if the computer allowed motion, whatever the one running the tests is set to.
        Platform.ReducedMotion.Override = false;
        var (window, editor, view, _) = await OpenPracticeAsync();
        try
        {
            var arrow = view.FindControl<Avalonia.Controls.Shapes.Path>("GuideArrow")!;
            await Pump(() => arrow.IsVisible && arrow.Classes.Contains("bouncing"));
            var lift = ((Avalonia.Media.TransformGroup)arrow.RenderTransform!).Children.OfType<Avalonia.Media.TranslateTransform>().Single();
            await Pump(() => lift.Y < -1);

            // Organise pages hides the tools, so the arrow under Tick goes too, and comes back with them.
            editor.IsOrganising = true;
            await Pump(() => !arrow.IsVisible);
            editor.IsOrganising = false;
            await Pump(() => arrow.IsVisible);

            // The crash: once a bounce had finished, hiding or moving the arrow cancelled a disposed token.
            await Task.Delay(3500);
            await Pump(() => true);
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            await Pump(() => !arrow.IsVisible);
            Assert.DoesNotContain("bouncing", arrow.Classes);
            editor.SelectToolCommand.Execute(EditorTool.Select);
            await Pump(() => arrow.IsVisible && arrow.Classes.Contains("bouncing"));
            for (int i = 0; i < 5; i++)
            {
                editor.SelectToolCommand.Execute(i % 2 == 0 ? EditorTool.Tick : EditorTool.Select);
                await Pump(() => true);
            }

            editor.CloseGuideCommand.Execute(null);
            await Pump(() => !arrow.IsVisible);
        }
        finally
        {
            Platform.ReducedMotion.Override = null;
            window.CloseWithoutAsking();
        }
    }

    [Fact]
    public void The_computers_reduce_motion_setting_can_be_read()
    {
        // Asks macOS, Windows or GNOME for real: a broken native call throws here rather than in front of someone.
        _ = Platform.ReducedMotion.IsOn;
    }

    [AvaloniaFact]
    public async Task With_reduced_motion_the_arrow_points_without_bouncing()
    {
        Platform.ReducedMotion.Override = true;
        var (window, editor, view, _) = await OpenPracticeAsync();
        try
        {
            var arrow = view.FindControl<Avalonia.Controls.Shapes.Path>("GuideArrow")!;
            await Pump(() => arrow.IsVisible);
            AssertArrowUnder(view, ToolButton(view, "Tick"));
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            editor.SelectToolCommand.Execute(EditorTool.Select);
            await Pump(() => arrow.IsVisible);
            await Task.Delay(400);
            await Pump(() => true);
            Assert.DoesNotContain("bouncing", arrow.Classes);
            var lift = ((Avalonia.Media.TransformGroup)arrow.RenderTransform!).Children.OfType<Avalonia.Media.TranslateTransform>().Single();
            Assert.Equal(0, lift.Y);
        }
        finally
        {
            Platform.ReducedMotion.Override = null;
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Select_click_on_empty_paper_without_fields_lights_the_tools()
    {
        var (window, editor, view, canvas) = await OpenPracticeAsync();
        try
        {
            editor.CloseGuideCommand.Execute(null);
            ClickPage(window, canvas, new PageRect(450, 240, 0, 0));
            await Pump(() => editor.IsNudgingTools);
            Assert.StartsWith("To fill in this page, choose a tool first", editor.Notice, StringComparison.Ordinal);
            Assert.False(editor.NoticeOffersUndo);
            foreach (string tool in (string[])["Tick", "Text", "Sign"])
            {
                Assert.Contains("guided", ToolButton(view, tool).Classes);
            }

            Save(window, "practice-nudge-towards-tools");
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            Assert.False(editor.IsNudgingTools);
            Assert.DoesNotContain("guided", ToolButton(view, "Tick").Classes);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Select_click_on_your_own_document_without_fields_does_not_nudge()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("four-pages.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);
            Assert.False(editor.HasFillableFields);
            editor.PointerPressed(0, new PagePoint(5, 5), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(5, 5));
            Assert.False(editor.IsNudgingTools);
            Assert.False(editor.GuideLightsTick);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Select_click_on_a_form_with_fields_does_not_nudge()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("form-fields.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);
            Assert.True(editor.HasFillableFields);
            editor.PointerPressed(0, new PagePoint(5, 5), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(5, 5));
            Assert.False(editor.IsNudgingTools);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    public static TheoryData<string> Languages => [.. Localization.Languages.Supported.Select(language => language.Code)];

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Every_language_has_its_practice_form_and_the_guide_starts_on_it(string language)
    {
        MainWindow? window = null;
        try
        {
            Localization.Languages.Apply(language);
            await Pump(() => true);
            window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await window.OpenPracticeFormAsync();
            MainWindow opened = window;
            await Pump(() => opened.DataContext is MainWindowViewModel { Editor: { IsLoading: false, IsGuiding: true } });
            EditorViewModel editor = Editor(opened);
            Assert.Equal(Localization.Strings.Main_PracticeFormPdf, editor.Title);
            Assert.Equal(GuideStep.Tick, editor.Guide);
            Assert.Single(editor.Pages);
        }
        finally
        {
            window?.CloseWithoutAsking();
            Localization.Languages.Apply("en");
        }
    }

    private static async Task<(MainWindow Window, EditorViewModel Editor, EditorView View, PagesCanvas Canvas)> OpenPracticeAsync()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        await window.OpenPracticeFormAsync();
        await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false, IsGuiding: true } });
        var view = (EditorView)window.FindControl<ContentControl>("Host")!.Content!;
        var canvas = view.FindControl<PagesCanvas>("Canvas")!;
        await Pump(() => canvas.Bounds.Width > 0);
        return (window, Editor(window), view, canvas);
    }

    /// <summary>
    /// The arrow's tip is just below the button's middle. Read from where the arrow is placed, not where it's drawn,
    /// which its bounce moves for a few seconds.
    /// </summary>
    private static void AssertArrowUnder(EditorView view, Button button)
    {
        var arrow = view.FindControl<Avalonia.Controls.Shapes.Path>("GuideArrow")!;
        var layer = view.FindControl<Canvas>("GuideArrowLayer")!;
        Assert.True(arrow.IsVisible);
        var tip = new Point(Canvas.GetLeft(arrow) + 11, Canvas.GetTop(arrow));
        Point bottom = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height), layer)!.Value;
        Assert.InRange(tip.X - bottom.X, -1, 1);
        Assert.InRange(tip.Y - bottom.Y, 0, 8);
    }

    private static Button ToolButton(EditorView view, string name) =>
        view.GetVisualDescendants().OfType<Button>().First(button => AutomationProperties.GetName(button) == name);

    /// <summary>A real mouse click in the middle of an area of page 1.</summary>
    private static void ClickPage(Window window, PagesCanvas canvas, PageRect area)
    {
        var centre = new PagePoint(area.X + (area.Width / 2), area.Y + (area.Height / 2));
        Point point = canvas.TranslatePoint(canvas.PageToCanvas(0, centre), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static SignatureImage Signature()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 12, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Black);
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new SignatureImage(Guid.NewGuid(), ImmutableArray.Create(png.AsSpan()), 40, 12);
    }
}
