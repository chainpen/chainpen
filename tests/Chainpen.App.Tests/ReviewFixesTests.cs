using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Chainpen.App.Controls;
using Chainpen.App.Platform;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using Chainpen.Core.Model;
using SkiaSharp;
using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>Fixes from the code review: shortcuts, clipboard, typing limits, recovery copies, repeated commands.</summary>
public sealed class ReviewFixesTests
{
    [Theory]
    [InlineData("Undo (Mod+Z)", true, "Undo (⌘Z)")]
    [InlineData("Redo (Mod+Shift+Z)", true, "Redo (⇧⌘Z)")]
    [InlineData("Mod Shift S", true, "⌘ ⇧ S")]
    [InlineData("Mod-click adds a page.", true, "⌘-click adds a page.")]
    [InlineData("Undo (Mod+Z)", false, "Undo (Ctrl+Z)")]
    [InlineData("Mod Shift S", false, "Ctrl Shift S")]
    public void Shortcut_wording_suits_the_computer(string text, bool isMac, string expected) =>
        Assert.Equal(expected, KeysExtension.ForThisComputer(text, isMac));

    [Theory]
    [InlineData(0, "It’s the only page")]
    [InlineData(1, "The other page is fine")]
    [InlineData(3, "The other 3 pages are fine")]
    public void The_damaged_page_card_reads_right_for_any_number_of_pages(int otherPages, string expected) =>
        Assert.Contains(expected, DamagedPageCard.ExplanationFor("form.pdf", otherPages), StringComparison.Ordinal);

    [AvaloniaFact]
    public async Task A_copied_signature_stays_off_the_system_clipboard_but_pastes_in_this_window()
    {
        (MainWindow window, EditorViewModel editor) = await OpenAsync();
        try
        {
            editor.BeginPlacingSignature(OnePixelSignature(), SignatureKind.Signature);
            editor.PointerPressed(0, new PagePoint(200, 300), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(200, 300));
            Assert.IsType<SignatureItem>(Assert.Single(editor.SelectedItems()));

            SystemClip clip = editor.CopyToClipboard()!;
            Assert.DoesNotContain("png", System.Text.Encoding.UTF8.GetString(clip.Items!), StringComparison.OrdinalIgnoreCase);

            Assert.True(editor.PasteFromClipboard(clip.Items, clip.Text));
            Assert.Equal(2, editor.State.Items.OfType<SignatureItem>().Count());
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Typing_stops_at_the_length_the_working_copy_can_hold()
    {
        (MainWindow window, EditorViewModel editor) = await OpenAsync();
        try
        {
            editor.SelectToolCommand.Execute(EditorTool.Text);
            editor.PointerPressed(0, new PagePoint(100, 100), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(100, 100));
            editor.UpdateTyping(new string('x', TextItem.MaxLength + 50));
            editor.FinishTyping();
            Assert.Equal(TextItem.MaxLength, Assert.Single(editor.State.Items.OfType<TextItem>()).Text.Length);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task A_copy_being_made_when_the_work_is_discarded_is_not_written()
    {
        (MainWindow window, EditorViewModel editor) = await OpenAsync();
        try
        {
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            editor.PointerPressed(0, new PagePoint(100, 100), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(100, 100));

            // "Don't save" lands while the copy is still being made off the UI thread.
            Task<bool> autosaving = editor.AutosaveAsync();
            editor.DiscardRecoveryCopy();
            Assert.False(await autosaving);
            Assert.DoesNotContain(RecoveryStore.List(), copy => copy.Id == editor.RecoveryId);
            Assert.Null(editor.RecoveryKeptAt);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Pressing_export_twice_opens_one_export()
    {
        (MainWindow window, EditorViewModel editor) = await OpenAsync();
        try
        {
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            editor.PointerPressed(0, new PagePoint(400, 600), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(400, 600));
            editor.SelectToolCommand.Execute(EditorTool.Select);

            RawInputModifiers command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.E, command, PhysicalKey.None, null);
            window.KeyRelease(Key.E, command, PhysicalKey.None, null);
            window.KeyPress(Key.E, command, PhysicalKey.None, null);
            window.KeyRelease(Key.E, command, PhysicalKey.None, null);
            await Pump(() => DialogOf(window) is ExportDialog);

            Click(DialogOf(window)!, "Keep editing");
            await Pump(() => DialogOf(window) is null);
            await Task.Delay(300);
            await Pump(() => true);
            Assert.Null(DialogOf(window));
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task A_dialog_keeps_focus_and_the_window_under_it_is_out_of_reach()
    {
        (MainWindow window, EditorViewModel editor) = await OpenAsync();
        try
        {
            Task asking = window.ShowMessageAsync("Test", "A message.");
            await Pump(() => DialogOf(window) is MessageDialog);
            await Pump(() => DialogOf(window)!.IsKeyboardFocusWithin);
            Assert.False(window.FindControl<ContentControl>("Host")!.IsEnabled);

            Click(DialogOf(window)!, "Got it");
            await asking;
            await Pump(() => window.FindControl<ContentControl>("Host")!.IsEnabled);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Tab_steps_through_items_in_reading_order_and_then_lets_focus_move_on()
    {
        (MainWindow window, EditorViewModel editor) = await OpenAsync();
        try
        {
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            foreach (PagePoint point in new PagePoint[] { new(300, 400), new(100, 100), new(300, 102) })
            {
                editor.PointerPressed(0, point, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
                editor.PointerReleased(point);
            }

            editor.SelectToolCommand.Execute(EditorTool.Select);
            var visited = new List<double>();
            while (editor.SelectNextItem(backwards: false))
            {
                visited.Add(Math.Round(editor.SelectedItems().Single().X + 7));
            }

            // Top row left to right (the two ticks a couple of points apart share it), then the one below.
            Assert.Equal([100, 300, 300], visited);
            Assert.True(editor.SelectNextItem(backwards: true));
            Assert.Equal(300, Math.Round(editor.SelectedItems().Single().X + 7));
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task A_pasted_picture_goes_on_the_page_in_view_keeps_its_shape_and_exports()
    {
        (MainWindow window, EditorViewModel editor) = await OpenAsync();
        try
        {
            using var bitmap = new SKBitmap(new SKImageInfo(800, 400, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(SKColors.Teal);
            using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            Assert.True(editor.PastePicture(png.ToArray()));

            PictureItem picture = Assert.IsType<PictureItem>(Assert.Single(editor.SelectedItems()));
            Assert.Equal(PictureItem.MaxStartWidth, picture.Width, 3);
            Assert.Equal(picture.Width / 2, picture.Height, 3);
            Assert.Equal("Picture selected", editor.SelectionLabel);

            SourcePage[] pages = [.. Enumerable.Range(0, editor.Source.PageCount).Select(editor.Source.GetPage)];
            byte[] final = Chainpen.Pdf.Export.DocumentWriter.FinalCopy(editor.Source.Pdf.SaveCopy(removeSecurity: true), pages, editor.State).Pdf;
            using var pdf = Chainpen.Pdf.Pdfium.PdfiumDocument.Open(final);
            Assert.Contains("image", pdf.GetObjectKinds(0));

            Assert.False(editor.PastePicture([1, 2, 3]), "not a picture");
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    private static async Task<(MainWindow Window, EditorViewModel Editor)> OpenAsync()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
        await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
        return (window, Editor(window));
    }

    private static Control? DialogOf(MainWindow window) => window.FindControl<ContentControl>("DialogHost")!.Content as Control;

    private static SignatureImage OnePixelSignature()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Black);
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new SignatureImage(Guid.NewGuid(), ImmutableArray.Create(png.AsSpan()), 4, 2);
    }
}
