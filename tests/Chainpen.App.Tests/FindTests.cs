using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>FS-974 (🎨 F1–F4): find words in the document by keyboard, with the matches shown and stepped through.</summary>
public sealed class FindTests
{
    private static readonly RawInputModifiers s_command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public async Task Organise_pages_keeps_showing_how_many_matches_each_page_has()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1440, Height = 900 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);
            editor.OpenFindCommand.Execute(null);
            editor.FindQuery = "over 18";
            await Pump(() => editor.FindHits.Count > 0 && !editor.IsSearching);

            editor.ShowOrganiseCommand.Execute(null);
            await Pump(() => true);
            ListBox organise = Named<ListBox>(window, "OrganiseList");
            Border[] badges = [.. organise.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("findBadge") && border.IsEffectivelyVisible)];
            Assert.Equal(editor.Pages.Count(page => page.HasFindMatches), badges.Length);
            Assert.Contains(badges, badge => badge.Child is TextBlock { Text: "1 match" });
            Save(window, "fs974-organise-match-badges");
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Mod_F_finds_words_on_the_page_and_in_added_text_and_Esc_goes_back_to_the_page()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1440, Height = 900 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);

            // Text added in Chainpen is found too.
            Assert.True(editor.PasteFromClipboard(null, "I am over 18 and agree"));
            editor.SelectToolCommand.Execute(EditorTool.Select);

            window.KeyPress(Key.F, s_command, PhysicalKey.None, null);
            await Pump(() => editor.IsFinding);
            TextBox box = Named<TextBox>(window, "FindBox");
            Assert.True(box.IsFocused, "Mod+F puts the cursor in the find box");
            Assert.True(Named<Control>(window, "FindBar").IsEffectivelyVisible);

            window.KeyTextInput("OVER 18");
            await Pump(() => editor.FindHits.Count > 0 && !editor.IsSearching);

            // Two on page 1 (the form's own words and the added text) and one on page 2, which is set sideways.
            Assert.Equal("1 of 3", editor.FindStatus);
            Assert.Equal(2, editor.Pages[0].FindMatchCount);
            Assert.Equal(1, editor.Pages[1].FindMatchCount);
            Assert.Contains("2 matches", editor.Pages[0].AccessibleName, StringComparison.Ordinal);

            // Return and Shift+Return in the box, and Mod+G from anywhere, step round the matches.
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal("2 of 3", editor.FindStatus);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal("3 of 3", editor.FindStatus);
            Assert.Equal(1, editor.CurrentFindHit!.PageIndex);
            await Pump(() => true);
            Save(window, "fs974-find-sideways-page");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal("1 of 3", editor.FindStatus);
            window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.None, null);
            Assert.Equal("3 of 3", editor.FindStatus);
            window.KeyPress(Key.G, s_command, PhysicalKey.None, null);
            Assert.Equal("1 of 3", editor.FindStatus);
            Assert.True(box.IsFocused, "stepping through matches leaves the cursor in the box");
            await Pump(() => true);
            Save(window, "fs974-f1-find-matches");

            // Esc closes it, clears the highlights and puts focus back on the page.
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            await Pump(() => !editor.IsFinding);
            Assert.Empty(editor.FindHits);
            Assert.Equal(0, editor.Pages[0].FindMatchCount);
            var view = window.GetVisualDescendants().OfType<EditorView>().Single();
            Assert.True(view.IsPageFocused(window.FocusManager?.GetFocusedElement()), "focus goes back to the page");

            // The words are kept for next time, selected so typing replaces them.
            window.KeyPress(Key.F, s_command, PhysicalKey.None, null);
            await Pump(() => editor.FindHits.Count == 3 && !editor.IsSearching);
            Assert.Equal("OVER 18", box.SelectedText);
            window.KeyTextInput("no such words");
            await Pump(() => editor.FindStatus == "No matches");
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task A_picture_has_no_text_to_search_and_the_find_bar_says_so()
    {
        string picture = Path.Combine(Path.GetTempPath(), $"chainpen-find-{Guid.NewGuid():N}.png");
        using (var bitmap = new WriteableBitmap(new PixelSize(400, 560), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque))
        {
            using (var buffer = bitmap.Lock())
            {
                byte[] white = new byte[buffer.RowBytes * buffer.Size.Height];
                Array.Fill(white, (byte)0xFF);
                System.Runtime.InteropServices.Marshal.Copy(white, 0, buffer.Address, white.Length);
            }

            using FileStream file = File.Create(picture);
            bitmap.Save(file, PngBitmapEncoderOptions.Default);
        }

        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([picture]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);

            editor.OpenFindCommand.Execute(null);
            editor.FindQuery = "anything";
            await Pump(() => editor.FindStatus == "No matches");
            Assert.True(editor.HasFindNote);
            Assert.Equal("Page 1 is a scan or picture, so it has no text to search.", editor.FindNote);
        }
        finally
        {
            window.CloseWithoutAsking();
            File.Delete(picture);
        }
    }

    [AvaloniaFact]
    public async Task In_a_small_window_the_find_bar_keeps_every_control_in_view_with_its_name()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1024, Height = 720 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);
            editor.OpenFindCommand.Execute(null);
            editor.FindQuery = "over 18";
            await Pump(() => editor.FindHits.Count > 0);
            await Pump(() => true);
            Save(window, "fs974-f4-find-small-window");

            Control bar = Named<Control>(window, "FindBar");
            foreach (Button button in bar.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
            {
                Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(button)), "icon-only buttons keep a name");
                Rect bounds = new(button.TranslatePoint(default, window) ?? default, button.Bounds.Size);
                Assert.True(bounds.Right <= window.Width && bounds.X >= 0, $"{Avalonia.Automation.AutomationProperties.GetName(button)} is inside the window");
            }

            // The toolbar's Find button moves into More, so More doesn't run into Undo.
            Assert.False(Named<Control>(window, "FindButton").IsVisible);
            EditorView view = window.GetVisualDescendants().OfType<EditorView>().Single();
            Assert.True(view.FindControl<MenuItem>("MoreFindItem")!.IsVisible);

            // The buttons lose their words, not their icons or names.
            Assert.DoesNotContain(bar.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Previous" && text.IsEffectivelyVisible);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
}
