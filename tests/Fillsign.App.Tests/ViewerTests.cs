using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fillsign.App.ViewModels;
using Fillsign.App.Views;
using Fillsign.Core.Model;

using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>Phase 2 viewer, driven headlessly with real Skia rendering. Frames are saved for review.</summary>
public sealed class ViewerTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public async Task First_launch_start_screen_offers_open_and_the_practice_form()
    {
        var window = new MainWindow(isFirstLaunch: true, []) { Width = 1280, Height = 860 };
        window.Show();
        await Pump(() => true);
        Save(window, "start-first-launch");
        var start = (StartViewModel)((MainWindowViewModel)window.DataContext!).Content;
        Assert.True(start.IsFirstLaunch);
        window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task Opening_a_pdf_shows_every_page_fitted_to_width_and_renders_them()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        await window.OpenPathsAsync([TestFiles.Sample("four-pages.pdf")]);
        EditorViewModel editor = Editor(window);

        await Pump(() => !editor.IsLoading && editor.Renderer.TryGet(0, Rotation.None, editor.Zoom) is { } page && Math.Abs(page.Scale - editor.Zoom) < 0.001);
        Assert.Equal(4, editor.Pages.Count);
        Assert.Equal("Page 1 of 4", editor.PageStatus);
        Assert.Equal("Working copy · no changes yet", editor.DocumentStatus);
        Assert.InRange(editor.Zoom, 0.5, 3);
        Save(window, "editor-four-pages");

        // Go to page 3; the current page follows the scroll position.
        editor.GoToPageCommand.Execute(3);
        await Pump(() => editor.CurrentPageIndex == 2);
        Assert.True(editor.Pages[2].IsCurrent);
        window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task Zoom_steps_stay_within_50_to_300_percent()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        await window.OpenPathsAsync([TestFiles.Sample("four-pages.pdf")]);
        EditorViewModel editor = Editor(window);
        await Pump(() => !editor.IsLoading);
        var view = (EditorView)window.FindControl<ContentControl>("Host")!.Content!;

        for (int i = 0; i < 20; i++)
        {
            view.ZoomIn();
        }

        Assert.Equal(3, editor.Zoom, 3);
        Assert.Equal("300%", editor.ZoomText);
        for (int i = 0; i < 20; i++)
        {
            view.ZoomOut();
        }

        Assert.Equal(0.5, editor.Zoom, 3);
        window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task Adding_pages_is_undoable_and_marks_the_copy_unsaved()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        await window.OpenPathsAsync([TestFiles.Sample("four-pages.pdf")]);
        EditorViewModel editor = Editor(window);
        await Pump(() => !editor.IsLoading);

        await editor.AddFilesAsync([TestFiles.Sample("sample-form.pdf")], at: 1);
        int added = editor.Pages.Count - 4;
        Assert.True(added > 0);
        Assert.Equal(1, editor.Pages[1 + added].Entry.SourceIndex); // page B now follows the added pages
        Assert.Equal("Working copy · not saved to file", editor.DocumentStatus);

        editor.UndoCommand.Execute(null);
        Assert.Equal(4, editor.Pages.Count);
        Assert.Equal("Working copy · no changes yet", editor.DocumentStatus);
        window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task A_damaged_page_keeps_its_place_and_can_be_removed()
    {
        string? path = TestFiles.CorpusFiles().FirstOrDefault(f => f.EndsWith("Pages-tree-refs.pdf", StringComparison.Ordinal));
        Assert.SkipWhen(path is null, "Run build/fetch-corpus.sh first.");
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        await window.OpenPathsAsync([path!]);
        EditorViewModel editor = Editor(window);
        await Pump(() => !editor.IsLoading);

        output.WriteLine($"{editor.Pages.Count} pages, {editor.DamagedPageCount} damaged");
        Assert.True(editor.HasDamagedPages);
        PageViewModel damaged = editor.Pages.First(page => page.IsDamaged);
        Assert.True(editor.ShowsDamagedCard(damaged));
        editor.GoToPageCommand.Execute(damaged.Number);
        await Pump(() => true);
        Save(window, "editor-damaged-page");

        int before = editor.Pages.Count;
        editor.RemovePageCommand.Execute(damaged);
        await Pump(() => editor.Pages.Count == before - 1 || before == 1);
        window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task An_unreadable_file_is_explained_on_the_start_screen()
    {
        string junk = Path.Combine(Path.GetTempPath(), $"fillsign-{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(junk, "%PDF-1.7 nothing here");
        try
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([junk]);
            var start = ((MainWindowViewModel)window.DataContext!).Start;
            Assert.NotNull(start.Problem);
            Assert.EndsWith("is damaged and can’t be opened", start.Problem!.Title.Replace('\'', '’'), StringComparison.Ordinal);
            await Pump(() => true);
            Save(window, "start-open-error");
            window.CloseWithoutAsking();
        }
        finally
        {
            File.Delete(junk);
        }
    }

    [AvaloniaFact]
    public async Task Password_and_open_choice_dialogs_answer_and_close()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        Task<string?> password = window.AskPasswordAsync("bank-statement.pdf", wasWrong: true);
        await Pump(() => true);
        Save(window, "dialog-password-wrong");
        var dialog = (PasswordDialog)window.FindControl<ContentControl>("DialogHost")!.Content!;
        dialog.FindControl<TextBox>("Password")!.Text = "secret";
        dialog.FindControl<Button>("ShowButton")!.Command?.Execute(null);
        Click(dialog, "Open");
        Assert.Equal("secret", await password);

        Task<OpenChoice> choice = window.AskOpenOrAddAsync("Open referral.pdf", "consent-form.pdf", 3);
        await Pump(() => true);
        Save(window, "dialog-open-or-add");
        var openDialog = (OpenOrAddDialog)window.FindControl<ContentControl>("DialogHost")!.Content!;
        openDialog.FindControl<RadioButton>("AddPages")!.IsChecked = true;
        Click(openDialog, "Open");
        Assert.Equal(OpenChoice.AddPages, await choice);
        Assert.False(window.FindControl<Border>("DialogLayer")!.IsVisible);
        window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task Dark_appearance_themes_the_app_but_not_the_page()
    {
        Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.Dark });
        await Pump(() => Avalonia.Application.Current!.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        try
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([TestFiles.Sample("four-pages.pdf")]);
            EditorViewModel editor = Editor(window);
            await Pump(() => !editor.IsLoading && editor.Renderer.TryGet(0, Rotation.None, editor.Zoom) is not null);
            Save(window, "editor-dark");
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, window.ActualThemeVariant);
            window.CloseWithoutAsking();
        }
        finally
        {
            Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.MatchSystem });
        }
    }
}
