using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fillsign.App.ViewModels;
using Fillsign.App.Views;
using Fillsign.Core.Model;

using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>Phase 5: organising pages (FR-6), driven through the view with real pointer and key input.</summary>
public sealed class PagesTests
{
    private static readonly RawInputModifiers s_command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public async Task Organise_pages_replaces_the_page_view_with_a_grid_and_back()
    {
        using var s = await Session.OpenAsync();
        Assert.True(s.Find<Control>("PagesPanel").IsVisible);
        s.Editor.ShowOrganiseCommand.Execute(null);
        await Pump(() => s.Find<Control>("OrganisePanel").IsVisible);
        Assert.False(s.Find<Control>("PagesPanel").IsVisible);
        Assert.False(s.Find<Control>("Scroller").IsVisible);
        Assert.True(s.Editor.ShowsOrganiseHint);
        Assert.StartsWith("6 pages · Drag pages to reorder", s.Editor.OrganiseHint, StringComparison.Ordinal);

        s.Key(Key.Escape, RawInputModifiers.None);
        await Pump(() => s.Find<Control>("Scroller").IsVisible);
        Assert.False(s.Editor.IsOrganising);
    }

    [AvaloniaFact]
    public async Task In_a_small_window_the_switch_and_rotate_buttons_become_labelled_icons()
    {
        using var s = await Session.OpenAsync(organise: true, width: 1024);
        s.Key(Key.A, s_command);
        await Pump(() => true);
        Assert.False(s.Find<TextBlock>("OrganiseLabel").IsVisible);
        Assert.False(s.Find<Control>("PageActions").GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "Rotate left").IsVisible);
        Assert.Equal("Organise pages", Avalonia.Automation.AutomationProperties.GetName(s.Find<Control>("OrganiseSwitch")));
        Save(s.Window, "phase5-S3-small-window");
    }

    [AvaloniaFact]
    public async Task Clicks_select_pages_and_both_lists_show_the_selection()
    {
        using var s = await Session.OpenAsync(organise: true);
        s.ClickPage(1);
        Assert.Equal([s.PageId(1)], s.Editor.SelectedPages);
        Assert.Equal("1 page selected", s.Editor.PageSelectionLabel);
        Assert.True(s.Editor.ShowsPageActions);

        // Shift-click selects the range (FR-6.3).
        s.ClickPage(3, RawInputModifiers.Shift);
        Assert.Equal(3, s.Editor.SelectedPageCount);
        Assert.Equal(3, s.Find<ListBox>("PageList").SelectedItems!.Count);
        Assert.True(s.Editor.Pages[2].IsSelected);
        await Pump(() => true);
        Save(s.Window, "phase5-4-organise-selection");

        s.Key(Key.Escape, RawInputModifiers.None);
        Assert.False(s.Editor.HasPageSelection);
        Assert.True(s.Editor.IsOrganising);

        s.Key(Key.A, s_command);
        Assert.Equal("All 6 pages selected", s.Editor.PageSelectionLabel);
    }

    [AvaloniaFact]
    public async Task Rotating_turns_the_selected_pages_and_their_drawings_and_undo_turns_them_back()
    {
        using var s = await Session.OpenAsync(organise: true);
        PageId first = s.PageId(1);
        PageSize before = s.Editor.Pages[0].DisplaySize;
        s.Editor.SetPageSelection([first, s.PageId(2)]);
        await s.ClickButton("Rotate right");

        Assert.Equal(Rotation.FromQuarterTurns(1), s.Editor.State.Pages[0].Rotation);
        Assert.Equal(Rotation.FromQuarterTurns(1), s.Editor.State.Pages[1].Rotation);
        Assert.Equal(Rotation.None, s.Editor.State.Pages[2].Rotation);
        Assert.Equal((before.Height, before.Width), (s.Editor.Pages[0].DisplaySize.Width, s.Editor.Pages[0].DisplaySize.Height));
        Assert.Equal("1 · rotated", s.Editor.Pages[0].NumberLabel);
        Assert.Equal(2, s.Editor.SelectedPageCount);

        s.Editor.UndoCommand.Execute(null);
        Assert.Equal(Rotation.None, s.Editor.State.Pages[0].Rotation);
    }

    [AvaloniaFact]
    public async Task Duplicating_copies_pages_with_their_edits_straight_after_and_selects_the_copies()
    {
        using var s = await Session.OpenAsync(organise: true);
        PageId first = s.PageId(1);
        s.Editor.SetPageSelection([first]);
        s.AddTickOnFirstPage();
        await s.ClickButton("Duplicate");

        Assert.Equal(7, s.Editor.State.Pages.Length);
        PageId copy = s.PageId(2);
        Assert.NotEqual(first, copy);
        Assert.Equal(s.Editor.State.Pages[0].SourceIndex, s.Editor.State.Pages[1].SourceIndex);
        Assert.Single(s.Editor.State.ItemsOn(copy));
        Assert.Equal([copy], s.Editor.SelectedPages);
    }

    [AvaloniaFact]
    public async Task Deleting_offers_undo_and_deleting_every_page_explains_why_not()
    {
        using var s = await Session.OpenAsync(organise: true);
        PageId third = s.PageId(3);
        s.Editor.SetPageSelection([third]);
        s.Key(Key.Delete, RawInputModifiers.None);

        Assert.Equal(5, s.Editor.State.Pages.Length);
        Assert.Equal("Page 3 deleted", s.Editor.Notice);
        Assert.True(s.Editor.ShowsPageNotice);
        await Pump(() => true);
        Save(s.Window, "phase5-deleted-notice");

        await s.ClickButton("Undo");
        Assert.Equal(third, s.PageId(3));

        // D9: everything selected, so Delete explains instead.
        s.Key(Key.A, s_command);
        await s.ClickButton("Delete");
        Assert.Equal(6, s.Editor.State.Pages.Length);
        Assert.Equal("A document needs at least one page. Deselect the page you want to keep, then delete the others.", s.Editor.PageMessage);
        await Pump(() => true);
        Save(s.Window, "phase5-D9-refused");

        // The message goes once the selection changes.
        s.ClickPage(2);
        Assert.Null(s.Editor.PageMessage);
    }

    [AvaloniaFact]
    public async Task Move_up_down_and_Move_to_reorder_without_dragging()
    {
        using var s = await Session.OpenAsync(organise: true);
        PageId[] start = [.. Enumerable.Range(1, 6).Select(s.PageId)];

        s.Editor.MovePagesDownCommand.Execute(s.Editor.Pages[0]);
        Assert.Equal([start[1], start[0], start[2]], s.Editor.State.Pages.Take(3).Select(entry => entry.Id));

        s.Editor.MovePagesUpCommand.Execute(s.Editor.Pages[1]);
        Assert.Equal(start, s.Editor.State.Pages.Select(entry => entry.Id));

        // Move to… pages 1 and 2 after page 5.
        s.Editor.SetPageSelection([start[0], start[1]]);
        await s.ClickButton("Move to…");
        await Pump(() => s.Dialog<MoveToDialog>() is not null);
        MoveToDialog dialog = s.Dialog<MoveToDialog>()!;
        await Pump(() => true);
        Save(s.Window, "phase5-move-to");
        dialog.Choose(5);
        Click(dialog, "Move");
        await Pump(() => s.Dialog<MoveToDialog>() is null);
        Assert.Equal([start[2], start[3], start[4], start[0], start[1], start[5]], s.Editor.State.Pages.Select(entry => entry.Id));
    }

    [AvaloniaFact]
    public async Task Dragging_in_the_grid_moves_the_selected_pages_where_the_marker_shows()
    {
        using var s = await Session.OpenAsync(organise: true);
        PageId[] start = [.. Enumerable.Range(1, 6).Select(s.PageId)];
        s.ClickPage(1);
        s.ClickPage(2, RawInputModifiers.Shift);
        Assert.Equal(2, s.Editor.SelectedPageCount);

        // Drag page 2 (the selection comes with it) to just before page 5.
        // Press away from where page 2 was just clicked, or the two presses count as a double-click.
        Point from = s.PageCentre(2) + new Point(0, 40);
        Point to = s.PageCentre(5) - new Point(60, 0);
        s.Window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        s.Window.MouseMove(from + new Point(10, 0), RawInputModifiers.LeftMouseButton);
        s.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        await Pump(() => true);
        Assert.True(s.Find<Control>("GridInsertMarker").IsVisible);
        Save(s.Window, "phase5-drag-grid");
        s.Window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);

        Assert.Equal([start[2], start[3], start[0], start[1], start[4], start[5]], s.Editor.State.Pages.Select(entry => entry.Id));
        Assert.Equal(2, s.Editor.SelectedPageCount);
        Assert.False(s.Find<Control>("GridInsertMarker").IsVisible);

        s.Editor.UndoCommand.Execute(null);
        Assert.Equal(start, s.Editor.State.Pages.Select(entry => entry.Id));
    }

    [AvaloniaFact]
    public async Task Dragging_in_the_Pages_panel_reorders_and_the_current_page_stays_current()
    {
        using var s = await Session.OpenAsync();
        PageId[] start = [.. Enumerable.Range(1, 6).Select(s.PageId)];
        ListBox list = s.Find<ListBox>("PageList");
        Control Container(int number) => (Control)list.ContainerFromIndex(number - 1)!;
        Point Centre(int number) => Container(number).TranslatePoint(new Point(Container(number).Bounds.Width / 2, Container(number).Bounds.Height / 2), s.Window)!.Value;

        s.Window.MouseDown(Centre(1), MouseButton.Left, RawInputModifiers.None);
        s.Window.MouseMove(Centre(1) + new Point(0, 12), RawInputModifiers.LeftMouseButton);
        s.Window.MouseMove(Centre(3) + new Point(0, 40), RawInputModifiers.LeftMouseButton);
        Assert.True(s.Find<Control>("InsertMarker").IsVisible);
        s.Window.MouseUp(Centre(3) + new Point(0, 40), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal([start[1], start[2], start[0]], s.Editor.State.Pages.Take(3).Select(entry => entry.Id));
        Assert.Equal(start[0], s.Editor.State.Pages[s.Editor.CurrentPageIndex].Id);
    }

    [AvaloniaFact]
    public async Task Each_page_has_its_own_menu_that_acts_on_that_page_or_the_selection()
    {
        using var s = await Session.OpenAsync();
        PageViewModel second = s.Editor.Pages[1];
        Assert.Equal([second.Id], s.Editor.TargetsFor(second));

        s.Editor.SetPageSelection([s.PageId(1), s.PageId(2)]);
        Assert.Equal(2, s.Editor.TargetsFor(second).Count);
        Assert.Equal([s.PageId(5)], s.Editor.TargetsFor(s.Editor.Pages[4]));

        string[] Headers(PageViewModel page) => [.. s.View.BuildPageMenu(page).Items.OfType<MenuItem>().Select(item => item.Header as string ?? string.Empty)];
        Assert.Equal(["2 selected pages", "Rotate left", "Rotate right", "Duplicate", "Move up", "Move down", "Move to…", "Delete 2 pages"], Headers(second));
        Assert.Equal(["Rotate left", "Rotate right", "Duplicate", "Move up", "Move down", "Move to…", "Delete page"], Headers(s.Editor.Pages[4]));

        // Move up is off for a selection that starts at page 1.
        Assert.False(s.View.BuildPageMenu(second).Items.OfType<MenuItem>().First(item => Equals(item.Header, "Move up")).IsEnabled);

        // Choosing from the menu acts on the whole selection.
        s.View.BuildPageMenu(second).Items.OfType<MenuItem>().First(item => Equals(item.Header, "Rotate right")).Command!.Execute(second);
        Assert.Equal(Rotation.FromQuarterTurns(1), s.Editor.State.Pages[0].Rotation);
        Assert.Equal(Rotation.FromQuarterTurns(1), s.Editor.State.Pages[1].Rotation);
        Assert.Equal(Rotation.None, s.Editor.State.Pages[2].Rotation);
    }

    [AvaloniaFact]
    public async Task Deleting_the_only_page_from_its_menu_explains_why_not()
    {
        using var s = await Session.OpenAsync(addPages: false);
        s.Editor.DeletePagesCommand.Execute(s.Editor.Pages[1]);
        Assert.Single(s.Editor.State.Pages);
        s.Editor.DeletePagesCommand.Execute(s.Editor.Pages[0]);
        await Pump(() => s.Dialog<MessageDialog>() is not null);
        Assert.Single(s.Editor.State.Pages);
    }

    [AvaloniaFact]
    public async Task Double_clicking_a_page_opens_it_for_editing()
    {
        using var s = await Session.OpenAsync(organise: true);
        Point centre = s.PageCentre(4);
        s.Window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        s.Window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        s.Window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        s.Window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        await Pump(() => !s.Editor.IsOrganising);
        await Pump(() => s.Editor.CurrentPageIndex == 3);
    }

    private sealed class Session : IDisposable
    {
        private Session(MainWindow window, EditorViewModel editor, EditorView view)
        {
            Window = window;
            Editor = editor;
            View = view;
        }

        public MainWindow Window { get; }

        public EditorViewModel Editor { get; }

        public EditorView View { get; }

        /// <summary>The practice form plus the four-page sample: six pages.</summary>
        public static async Task<Session> OpenAsync(bool organise = false, bool addPages = true, double width = 1280)
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = width, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            EditorViewModel editor = Editor(window);
            await Pump(() => !editor.IsLoading);
            if (addPages)
            {
                await editor.AddFilesAsync([TestFiles.Sample("four-pages.pdf")]);
                await Pump(() => editor.Pages.Count == 6);
            }

            var view = (EditorView)window.FindControl<ContentControl>("Host")!.Content!;
            var session = new Session(window, editor, view);
            if (organise)
            {
                editor.ShowOrganiseCommand.Execute(null);
                await Pump(() => session.Find<ListBox>("OrganiseList").ContainerFromIndex(5) is Control { Bounds.Width: > 0 });
            }

            return session;
        }

        public T Find<T>(string name)
            where T : Control => View.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

        public T? Dialog<T>()
            where T : Control => Window.FindControl<ContentControl>("DialogHost")!.Content as T;

        public PageId PageId(int number) => Editor.State.Pages[number - 1].Id;

        public async Task ClickButton(string label)
        {
            await Pump(() => true);
            Button button = View.GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label) || Equals(b.Content, label));
            button.Command?.Execute(button.CommandParameter);
        }

        public Point PageCentre(int number)
        {
            var container = (Control)Find<ListBox>("OrganiseList").ContainerFromIndex(number - 1)!;
            return container.TranslatePoint(new Point(container.Bounds.Width / 2, 100), Window)!.Value;
        }

        public void ClickPage(int number, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Point centre = PageCentre(number);
            Window.MouseDown(centre, MouseButton.Left, modifiers);
            Window.MouseUp(centre, MouseButton.Left, modifiers);
        }

        public void Key(Key key, RawInputModifiers modifiers)
        {
            Window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        }

        /// <summary>Adds a tick to page 1 through the editor, as the Tick tool would.</summary>
        public void AddTickOnFirstPage()
        {
            Editor.SelectToolCommand.Execute(EditorTool.Tick);
            Editor.PointerPressed(0, new PagePoint(100, 100), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            Editor.PointerReleased(new PagePoint(100, 100));
            Editor.SelectToolCommand.Execute(EditorTool.Select);
        }

        public void Dispose() => Window.CloseWithoutAsking();
    }
}
