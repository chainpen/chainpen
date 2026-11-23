using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using Chainpen.Core.Gaps;
using Chainpen.Core.Model;
using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>FS-609, scenario 3: export points out possible gaps; Show me, fill it, Next gap, Back to export.</summary>
public sealed class GapsTests
{
    [AvaloniaFact]
    public async Task Export_points_out_gaps_and_the_checking_mode_steps_through_them_and_back()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);

            // Tick the first two boxes on page 1; leave the third, the name and the signature.
            Place(editor, EditorTool.Tick, new PagePoint(67, 135));
            Place(editor, EditorTool.Tick, new PagePoint(67, 175));

            editor.ExportCommand.Execute(null);
            await Pump(() => DialogOf<ExportDialog>(window) is not null);
            ExportDialog dialog = DialogOf<ExportDialog>(window)!;
            await Pump(() => true);
            string listed = TextIn(dialog.FindControl<Control>("GapsSection")!);
            Assert.Contains("2 of 3 boxes ticked on page 1", listed, StringComparison.Ordinal);
            Assert.Contains("Not ticked: “I am over 18”", listed, StringComparison.Ordinal);
            Assert.Contains("Nothing after “Name” on page 1", listed, StringComparison.Ordinal);
            Assert.Contains("Space that may need a signature on page 1", listed, StringComparison.Ordinal);
            Save(window, "phase6-6-export-gaps");

            // Show me: the dialog closes, the bar explains, and the Tick tool is ready on the unticked box.
            Click(dialog, "Show me");
            await Pump(() => DialogOf<ExportDialog>(window) is null && editor.IsCheckingGaps);
            Assert.Equal(EditorTool.Tick, editor.Tool);
            Gap gap = editor.CheckingGap!;
            Assert.Equal(GapKind.Unticked, gap.Kind);
            Assert.StartsWith("Checking before export · gap 1 of 3", editor.CheckingText, StringComparison.Ordinal);
            await Pump(() => true);
            Save(window, "phase6-e10-checking");

            // Tick it, then Next gap: the name is next, with the Text tool ready.
            editor.PointerPressed(0, gap.Area.Centre, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(gap.Area.Centre);
            editor.NextGapCommand.Execute(null);
            Assert.Equal("Name", editor.CheckingGap?.Label);
            Assert.Equal(EditorTool.Text, editor.Tool);
            Assert.StartsWith("Checking before export · gap 1 of 2", editor.CheckingText, StringComparison.Ordinal);

            // The checking bar takes the place of the bar under the toolbar, so the page keeps its height.
            Assert.True(window.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "CheckingBar").IsEffectivelyVisible);

            // Next gap, then Previous gap comes back (UX review 8).
            editor.NextGapCommand.Execute(null);
            Assert.NotEqual("Name", editor.CheckingGap?.Label);
            editor.PreviousGapCommand.Execute(null);
            Assert.Equal("Name", editor.CheckingGap?.Label);

            // Back to export: the list is looked at afresh, so the boxes are all ticked now.
            editor.BackToExportCommand.Execute(null);
            await Pump(() => DialogOf<ExportDialog>(window) is not null);
            Assert.False(editor.IsCheckingGaps);
            listed = TextIn(DialogOf<ExportDialog>(window)!.FindControl<Control>("GapsSection")!);
            Assert.DoesNotContain("boxes ticked", listed, StringComparison.Ordinal);
            Assert.Contains("Nothing after “Name”", listed, StringComparison.Ordinal);
            Click(DialogOf<ExportDialog>(window)!, "Keep editing");
            await Pump(() => DialogOf<ExportDialog>(window) is null);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Show_me_on_a_form_field_keeps_the_Select_tool_that_fills_it()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("form-fields.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);
            editor.SetField("Name", "Jordan Lee");
            GapReport report = editor.FindGaps();
            int emailGap = report.Gaps.ToList().FindIndex(gap => gap.IsField && gap.Label == "Your email address");
            Assert.True(emailGap >= 0);
            editor.StartChecking(report, emailGap);
            Assert.Equal(EditorTool.Select, editor.Tool);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task A_document_with_nothing_added_shows_no_gaps_and_Esc_stops_checking()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);
            Assert.Empty(editor.FindGaps().Gaps);

            Place(editor, EditorTool.Tick, new PagePoint(400, 600));
            GapReport report = editor.FindGaps();
            editor.StartChecking(report, 0);
            Assert.True(editor.IsCheckingGaps);
            Assert.True(editor.BackOut(), "Esc first puts the Tick tool down");
            Assert.True(editor.BackOut(), "then stops checking");
            Assert.False(editor.IsCheckingGaps);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    private static void Place(EditorViewModel editor, EditorTool tool, PagePoint point)
    {
        editor.SelectToolCommand.Execute(tool);
        editor.PointerPressed(0, point, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
        editor.PointerReleased(point);
        editor.SelectToolCommand.Execute(EditorTool.Select);
    }

    private static T? DialogOf<T>(MainWindow window)
        where T : Control => window.FindControl<ContentControl>("DialogHost")!.Content as T;

    private static string TextIn(Control root) =>
        string.Join(" | ", root.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
}
