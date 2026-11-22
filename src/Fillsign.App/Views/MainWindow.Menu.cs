using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Fillsign.App.Localization;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

/// <summary>
/// FS-700: the menus. On macOS they're the system menu bar; on Windows and Linux a File / Edit / View / Page / Help bar
/// at the top of the window (§3.4). Every command is also on the toolbar or a shortcut. Shortcuts are handled by the
/// window's key handler, which marks them handled, so a menu item and its shortcut never both run.
/// </summary>
public partial class MainWindow
{
    private void BuildMenu()
    {
        _menuItemsNeedingDocument.Clear();
        NativeMenu.SetMenu(this, CreateMenu());
        _menuItemsNeedingDocument.ForEach(item => item.IsEnabled = false);
    }

    /// <summary>
    /// The interface language changed: the menu is built again in it. The window keeps its menu object, because
    /// Avalonia's macOS menu can't take a new one once shown, and the top-level items are swapped for new ones, because
    /// macOS shows each menu's title from its submenu, which Avalonia names only when it creates it.
    /// </summary>
    private void RenameMenu()
    {
        if (NativeMenu.GetMenu(this) is not { } menu)
        {
            BuildMenu();
            return;
        }

        _menuItemsNeedingDocument.Clear();
        NativeMenu fresh = CreateMenu();
        NativeMenuItemBase[] items = [.. fresh.Items];
        fresh.Items.Clear();
        menu.Items.Clear();
        foreach (NativeMenuItemBase item in items)
        {
            menu.Items.Add(item);
        }

        UpdateMenuForDocument();
    }

    private NativeMenu CreateMenu()
    {
        KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyGesture Keys(Key key, KeyModifiers extra = KeyModifiers.None) => new(key, command | extra);

        var recent = new NativeMenu();
        var recentItem = new NativeMenuItem(Strings.Menu_OpenRecent) { Menu = recent };
        recent.Opening += (_, _) => FillRecent(recent);

        var file = Menu(Strings.Menu_File,
            Item(Strings.Start_Open, () => _ = ChooseAndOpenAsync(), Keys(Key.O)),
            recentItem,
            Item(Strings.Editor_AddPages, () => _ = AddPagesAsync(), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item(Strings.Editor_Save, () => RunOnce(Editor?.SaveCommand), Keys(Key.S), requiresDocument: true),
            Item(Strings.Editor_SaveAs, () => RunOnce(Editor?.SaveAsCommand), Keys(Key.S, KeyModifiers.Shift), requiresDocument: true),
            Item(Strings.Editor_ExportFinalCopy, () => RunOnce(Editor?.ExportCommand), Keys(Key.E), requiresDocument: true),
            Item(Strings.Menu_Print, () => RunOnce(Editor?.PrintCommand), Keys(Key.P), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item(Strings.Menu_CloseWindow, Close, Keys(Key.W)));

        // On macOS, About and Settings live in the Fillsign app menu (App.MacAppMenu); elsewhere, in File and Help.
        if (!OperatingSystem.IsMacOS())
        {
            NativeMenu fileItems = file.Menu!;
            fileItems.Items.Insert(fileItems.Items.Count - 1, Item(Strings.Menu_Settings, ShowSettings, Keys(Key.OemComma)));
            fileItems.Items.Insert(fileItems.Items.Count - 1, new NativeMenuItemSeparator());
        }

        var edit = Menu(Strings.Menu_Edit,
            Item(Strings.Editor_Undo, () => InTextBoxOr(box => box.Undo(), () => Editor?.UndoCommand.Execute(null)), Keys(Key.Z), requiresDocument: true),
            Item(Strings.Editor_Redo, () => InTextBoxOr(box => box.Redo(), () => Editor?.RedoCommand.Execute(null)), Keys(Key.Z, KeyModifiers.Shift), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item(Strings.Menu_Cut, () => InTextBoxOr(box => box.Cut(), () => { if (Editor is { } editor) { _ = CutAsync(editor); } }), Keys(Key.X), requiresDocument: true),
            Item(Strings.Menu_Copy, () => InTextBoxOr(box => box.Copy(), () => { if (Editor is { } editor) { _ = PutOnClipboardAsync(editor.CopyToClipboard()); } }), Keys(Key.C), requiresDocument: true),
            Item(Strings.Menu_Paste, () => InTextBoxOr(box => box.Paste(), () => { if (Editor is { } editor) { _ = PasteAsync(editor); } }), Keys(Key.V), requiresDocument: true),
            Item(Strings.Editor_Duplicate, Duplicate, Keys(Key.D), requiresDocument: true),
            Item(Strings.Common_Delete, () => InTextBoxOr(DeleteForward, Delete), new KeyGesture(Key.Delete), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item(Strings.Menu_SelectAll, () => InTextBoxOr(box => box.SelectAll(), SelectAll), Keys(Key.A), requiresDocument: true));

        var view = Menu(Strings.Editor_View,
            Item(Strings.Editor_EditContent, () => Editor?.ShowEditContentCommand.Execute(null), requiresDocument: true),
            Item(Strings.Editor_OrganisePages, () => Editor?.ShowOrganiseCommand.Execute(null), requiresDocument: true),
            Item(Strings.Editor_ShowOrHidePages, () => Editor?.TogglePagesPanelCommand.Execute(null), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item(Strings.Editor_ZoomIn, () => _editorView?.ZoomIn(), Keys(Key.OemPlus), requiresDocument: true),
            Item(Strings.Editor_ZoomOut, () => _editorView?.ZoomOut(), Keys(Key.OemMinus), requiresDocument: true),
            Item(Strings.Editor_FitWidth, () => _editorView?.FitWidth(), Keys(Key.D0), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item(Strings.Menu_ResetLayout, () =>
            {
                ResetWindowSize();
                _editorView?.ResetLayout();
            }, requiresDocument: true));

        var page = Menu(Strings.Editor_Page,
            Item(Strings.Menu_PreviousPage, () => Editor?.PreviousPageCommand.Execute(null), new KeyGesture(Key.PageUp), requiresDocument: true),
            Item(Strings.Menu_NextPage, () => Editor?.NextPageCommand.Execute(null), new KeyGesture(Key.PageDown), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item(Strings.Editor_RotateLeft, () => RunOnPages(editor => editor.RotatePagesLeftCommand), requiresDocument: true),
            Item(Strings.Editor_RotateRight, () => RunOnPages(editor => editor.RotatePagesRightCommand), requiresDocument: true),
            Item(Strings.Menu_DuplicatePage, () => RunOnPages(editor => editor.DuplicatePagesCommand), requiresDocument: true),
            Item(Strings.Menu_MoveUp, () => RunOnPages(editor => editor.MovePagesUpCommand), requiresDocument: true),
            Item(Strings.Menu_MoveDown, () => RunOnPages(editor => editor.MovePagesDownCommand), requiresDocument: true),
            Item(Strings.Editor_MoveTo, () => RunOnPages(editor => editor.MovePagesToCommand), requiresDocument: true),
            Item(Strings.Menu_DeletePage, () => RunOnPages(editor => editor.DeletePagesCommand), requiresDocument: true));

        var help = Menu(Strings.Menu_Help,
            Item(Strings.Common_KeyboardShortcuts, () => _ = ShowShortcutsAsync()),
            Item(Strings.Menu_TryThePracticeForm, OpenPracticeGuide));
        if (!OperatingSystem.IsMacOS())
        {
            help.Menu!.Items.Add(new NativeMenuItemSeparator());
            help.Menu.Items.Add(Item(Strings.Menu_CheckForUpdates, () => _ = CheckForUpdatesAsync()));
            help.Menu.Items.Add(Item(Strings.Menu_AboutFillsign, ShowAbout));
        }

        var menu = new NativeMenu();
        foreach (NativeMenuItem item in (NativeMenuItem[])[file, edit, view, page, help])
        {
            menu.Items.Add(item);
        }

        return menu;
    }

    private readonly List<NativeMenuItem> _menuItemsNeedingDocument = [];

    private EditorViewModel? Editor => _viewModel.Editor;

    /// <summary>Menu items that need an open document are greyed out on the start screen.</summary>
    private void UpdateMenuForDocument() => _menuItemsNeedingDocument.ForEach(item => item.IsEnabled = Editor is not null);

    private NativeMenuItem Item(string header, Action action, KeyGesture? gesture = null, bool requiresDocument = false)
    {
        var item = new NativeMenuItem(header) { Command = new RelayCommand(action), Gesture = gesture };
        if (requiresDocument)
        {
            _menuItemsNeedingDocument.Add(item);
        }

        return item;
    }

    private static NativeMenuItem Menu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (NativeMenuItemBase item in items)
        {
            menu.Items.Add(item);
        }

        return new NativeMenuItem(header) { Menu = menu };
    }

    private void FillRecent(NativeMenu recent)
    {
        recent.Items.Clear();
        IReadOnlyList<Platform.RecentFile> files = Platform.AppSettings.Load().Recent;
        if (files.Count == 0)
        {
            recent.Items.Add(new NativeMenuItem(Strings.Menu_NoRecentDocuments) { IsEnabled = false });
            return;
        }

        foreach (Platform.RecentFile file in files)
        {
            string path = file.Path;
            recent.Items.Add(new NativeMenuItem(Path.GetFileName(path)) { Command = new RelayCommand(() => _ = OpenPathsAsync([path])) });
        }
    }

    private async Task AddPagesAsync()
    {
        IReadOnlyList<string> paths = await ChooseFilesAsync().ConfigureAwait(true);
        if (paths.Count > 0 && Editor is { } editor)
        {
            await editor.AddFilesAsync(paths).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// On macOS the menu bar sees ⌘A, ⌘C and the rest before the focused control does. While a text box has focus
    /// (typing on the page, a form field, Go to page), the command is for its text, not for the page's items.
    /// </summary>
    private void InTextBoxOr(Action<TextBox> forText, Action otherwise)
    {
        if (FocusManager?.GetFocusedElement() is TextBox box)
        {
            forText(box);
        }
        else
        {
            otherwise();
        }
    }

    /// <summary>The Delete key in a text box: removes the selection, or the character after the caret.</summary>
    private static void DeleteForward(TextBox box)
    {
        string text = box.Text ?? string.Empty;
        int start = Math.Min(box.SelectionStart, box.SelectionEnd), end = Math.Max(box.SelectionStart, box.SelectionEnd);
        if (end == start && end < text.Length)
        {
            end++;
        }

        if (end > start)
        {
            box.Text = text.Remove(start, end - start);
            box.CaretIndex = start;
        }
    }

    private void Duplicate()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (editor.IsOrganising)
        {
            editor.DuplicatePagesCommand.Execute(null);
        }
        else if (editor.DuplicateSelectionCommand.CanExecute(null))
        {
            editor.DuplicateSelectionCommand.Execute(null);
        }
    }

    private void Delete()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (editor.IsOrganising)
        {
            editor.DeletePagesCommand.Execute(null);
        }
        else if (editor.DeleteSelectionCommand.CanExecute(null))
        {
            editor.DeleteSelectionCommand.Execute(null);
        }
    }

    private void SelectAll()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (editor.IsOrganising)
        {
            editor.SelectAllPagesCommand.Execute(null);
        }
        else
        {
            editor.SelectAllOnCurrentPage();
        }
    }

    /// <summary>Page menu actions apply to the selected pages, or to the page in view when none are selected.</summary>
    private void RunOnPages(Func<EditorViewModel, System.Windows.Input.ICommand> command)
    {
        if (Editor is not { } editor || editor.Pages.Count == 0)
        {
            return;
        }

        PageViewModel? target = editor.HasPageSelection ? null : editor.Pages[editor.CurrentPageIndex];
        command(editor).Execute(target);
    }

    private Task<bool> ShowShortcutsAsync() =>
        ShowDialogAsync(new ShortcutsDialog(), dialog => dialog.Closed.ContinueWith(_ => true, TaskScheduler.Default), top: 60);

    /// <summary>Check for Updates: checks now and says what was found, unlike the quiet check at start-up (FR-10.2).</summary>
    public async Task CheckForUpdatesAsync()
    {
        Platform.AppUpdates updates = Platform.AppUpdates.Instance;
        Platform.UpdateCheck result = await updates.CheckAsync().ConfigureAwait(true);
        (string title, string message) = Platform.AppUpdates.Describe(result, App.DisplayVersion, updates.Ready);

        // Only a failed check is a problem; the rest is news.
        await (result == Platform.UpdateCheck.Failed ? ShowMessageAsync(title, message) : ShowInformationAsync(title, message)).ConfigureAwait(true);
    }

    /// <summary>About Fillsign: the version, that it works offline, and the licence in plain words (FS-027).</summary>
    public void ShowAbout() =>
        _ = ShowInformationAsync(
            Strings.Menu_AboutFillsign,
            Text.Format(Strings.Menu_AboutText, App.DisplayVersion));

    /// <summary>Opens the practice form with its guide: here if nothing's open, otherwise in a new window.</summary>
    private void OpenPracticeGuide()
    {
        if (Editor is null)
        {
            _ = OpenPracticeFormAsync();
            return;
        }

        var window = new MainWindow(isFirstLaunch: false, []);
        window.Show();
        _ = window.OpenPracticeFormAsync();
    }
}
