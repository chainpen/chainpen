using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
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
        KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyGesture Keys(Key key, KeyModifiers extra = KeyModifiers.None) => new(key, command | extra);

        var recent = new NativeMenu();
        var recentItem = new NativeMenuItem("Open recent") { Menu = recent };
        recent.Opening += (_, _) => FillRecent(recent);

        var file = Menu("File",
            Item("Open…", () => _ = ChooseAndOpenAsync(), Keys(Key.O)),
            recentItem,
            Item("Add pages…", () => _ = AddPagesAsync(), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item("Save", () => _ = Editor?.SaveCommand.ExecuteAsync(null), Keys(Key.S), requiresDocument: true),
            Item("Save as…", () => _ = Editor?.SaveAsCommand.ExecuteAsync(null), Keys(Key.S, KeyModifiers.Shift), requiresDocument: true),
            Item("Export final copy…", () => _ = Editor?.ExportCommand.ExecuteAsync(null), Keys(Key.E), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item("Settings…", ShowSettings, Keys(Key.OemComma)),
            new NativeMenuItemSeparator(),
            Item("Close window", Close, Keys(Key.W)));

        var edit = Menu("Edit",
            Item("Undo", () => Editor?.UndoCommand.Execute(null), Keys(Key.Z), requiresDocument: true),
            Item("Redo", () => Editor?.RedoCommand.Execute(null), Keys(Key.Z, KeyModifiers.Shift), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item("Cut", () => { if (Editor is { } editor) { _ = CutAsync(editor); } }, Keys(Key.X), requiresDocument: true),
            Item("Copy", () => { if (Editor is { } editor) { _ = PutOnClipboardAsync(editor.CopyToClipboard()); } }, Keys(Key.C), requiresDocument: true),
            Item("Paste", () => { if (Editor is { } editor) { _ = PasteAsync(editor); } }, Keys(Key.V), requiresDocument: true),
            Item("Duplicate", Duplicate, Keys(Key.D), requiresDocument: true),
            Item("Delete", Delete, new KeyGesture(Key.Delete), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item("Select all", SelectAll, Keys(Key.A), requiresDocument: true));

        var view = Menu("View",
            Item("Edit content", () => Editor?.ShowEditContentCommand.Execute(null), requiresDocument: true),
            Item("Organise pages", () => Editor?.ShowOrganiseCommand.Execute(null), requiresDocument: true),
            Item("Show or hide pages", () => Editor?.TogglePagesPanelCommand.Execute(null), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item("Zoom in", () => _editorView?.ZoomIn(), Keys(Key.OemPlus), requiresDocument: true),
            Item("Zoom out", () => _editorView?.ZoomOut(), Keys(Key.OemMinus), requiresDocument: true),
            Item("Fit width", () => _editorView?.FitWidth(), Keys(Key.D0), requiresDocument: true));

        var page = Menu("Page",
            Item("Previous page", () => Editor?.PreviousPageCommand.Execute(null), new KeyGesture(Key.PageUp), requiresDocument: true),
            Item("Next page", () => Editor?.NextPageCommand.Execute(null), new KeyGesture(Key.PageDown), requiresDocument: true),
            new NativeMenuItemSeparator(),
            Item("Rotate left", () => RunOnPages(editor => editor.RotatePagesLeftCommand), requiresDocument: true),
            Item("Rotate right", () => RunOnPages(editor => editor.RotatePagesRightCommand), requiresDocument: true),
            Item("Duplicate page", () => RunOnPages(editor => editor.DuplicatePagesCommand), requiresDocument: true),
            Item("Move up", () => RunOnPages(editor => editor.MovePagesUpCommand), requiresDocument: true),
            Item("Move down", () => RunOnPages(editor => editor.MovePagesDownCommand), requiresDocument: true),
            Item("Move to…", () => RunOnPages(editor => editor.MovePagesToCommand), requiresDocument: true),
            Item("Delete page", () => RunOnPages(editor => editor.DeletePagesCommand), requiresDocument: true));

        var help = Menu("Help",
            Item("Keyboard shortcuts", () => _ = ShowShortcutsAsync()),
            Item("How Fillsign works", ShowHowItWorks));

        var menu = new NativeMenu();
        foreach (NativeMenuItem item in (NativeMenuItem[])[file, edit, view, page, help])
        {
            menu.Items.Add(item);
        }

        NativeMenu.SetMenu(this, menu);
        _menuItemsNeedingDocument.ForEach(item => item.IsEnabled = false);
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
            recent.Items.Add(new NativeMenuItem("No recent documents") { IsEnabled = false });
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

    private void ShowHowItWorks() =>
        _ = ShowMessageAsync(
            "How Fillsign works",
            "Open your form, then tick, type and sign on the page with the tools at the top. Save keeps a working copy you can change later. Export final copy makes the file to send: it looks the same for everyone and can’t be changed. Nothing leaves this computer.");
}
