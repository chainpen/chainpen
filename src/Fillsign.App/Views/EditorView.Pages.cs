using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Fillsign.App.ViewModels;
using Fillsign.Core.Model;

namespace Fillsign.App.Views;

/// <summary>
/// Phase 5: the two page lists (Pages panel and Organise pages) share the editor's page selection, open each page's
/// own menu (FR-6.9), and reorder pages by dragging (FR-6.4). Move up, Move down and Move to… are the non-drag way.
/// </summary>
public partial class EditorView
{
    // A drag starts once the pointer has moved this far, so a click stays a click.
    private const double DragThreshold = 6;

    // Half the Organise grid's gap between pages (the WrapPanel's item spacing), where the insert marker sits, and the
    // item padding above each thumbnail.
    private const double GridMarkerOffset = 8;
    private const double GridItemPadding = 10;

    private bool _isSyncingSelection;
    private PagePress? _press;
    private ImmutableHashSet<PageId>? _dragging;
    private int? _dragGap;

    private void AttachPageList(ListBox list)
    {
        list.SelectionChanged += (_, e) => OnListSelectionChanged(list, e);
        list.AddHandler(PointerPressedEvent, (_, e) => OnListPointerPressed(list, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerMovedEvent, (_, e) => OnListPointerMoved(list, e), RoutingStrategies.Tunnel);
        list.AddHandler(PointerReleasedEvent, (_, e) => OnListPointerReleased(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerCaptureLostEvent, (_, _) => EndPageDrag(), RoutingStrategies.Bubble);
        list.ContextRequested += (_, e) =>
        {
            if (PageAt(e.Source) is { } found)
            {
                ShowPageMenu(found.Container, found.Page);
                e.Handled = true;
            }
        };
    }

    /// <summary>Shows the editor's page selection in both lists.</summary>
    private void SyncListSelection()
    {
        if (_editor is null)
        {
            return;
        }

        _isSyncingSelection = true;
        try
        {
            foreach (ListBox list in (ListBox[])[PageList, OrganiseList])
            {
                list.Selection.BeginBatchUpdate();
                list.Selection.Clear();
                for (int i = 0; i < _editor.Pages.Count; i++)
                {
                    if (_editor.SelectedPages.Contains(_editor.Pages[i].Id))
                    {
                        list.Selection.Select(i);
                    }
                }

                list.Selection.EndBatchUpdate();
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void OnListSelectionChanged(ListBox list, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _editor is null || _dragging is not null)
        {
            return;
        }

        // Pages replaced by a page action (rotated, renumbered) drop out of the list's selection; that isn't the
        // person deselecting them, and the list is re-synced once the pages are rebuilt.
        if (e.AddedItems.Count == 0 && e.RemovedItems.OfType<PageViewModel>().All(page => !_editor.Pages.Contains(page)))
        {
            return;
        }

        _editor.SetPageSelection(list.SelectedItems?.OfType<PageViewModel>().Select(page => page.Id) ?? []);

        // In the Pages panel, picking one page also goes to it.
        if (ReferenceEquals(list, PageList) && e.AddedItems.Count == 1 && list.SelectedItems?.Count == 1 && e.AddedItems[0] is PageViewModel page)
        {
            _editor.GoToPageCommand.Execute(page.Number);
        }
    }

    // ----- The page's own menu (FR-6.9) -----

    private void OnPageMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PageViewModel page } button)
        {
            ShowPageMenu(button, page);
            e.Handled = true;
        }
    }

    private void ShowPageMenu(Control target, PageViewModel page) => BuildPageMenu(page).ShowAt(target);

    /// <summary>Actions for this page, or for the whole selection when this page is part of it.</summary>
    internal MenuFlyout BuildPageMenu(PageViewModel page)
    {
        EditorViewModel editor = _editor ?? throw new InvalidOperationException("No document is open.");

        ImmutableHashSet<PageId> targets = editor.TargetsFor(page);
        int first = editor.Pages.ToList().FindIndex(p => targets.Contains(p.Id));
        int last = editor.Pages.ToList().FindLastIndex(p => targets.Contains(p.Id));
        var menu = new MenuFlyout();
        if (targets.Count > 1)
        {
            menu.Items.Add(new MenuItem { Header = $"{targets.Count} selected pages", IsEnabled = false });
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Item("Rotate left", "IconRotateLeft", editor.RotatePagesLeftCommand, page));
        menu.Items.Add(Item("Rotate right", "IconRotateRight", editor.RotatePagesRightCommand, page));
        menu.Items.Add(Item("Duplicate", "IconDuplicate", editor.DuplicatePagesCommand, page));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move up", "IconArrowUp", editor.MovePagesUpCommand, page, isEnabled: first > 0));
        menu.Items.Add(Item("Move down", "IconArrowDown", editor.MovePagesDownCommand, page, isEnabled: last < editor.Pages.Count - 1));
        menu.Items.Add(Item("Move to…", null, editor.MovePagesToCommand, page));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(targets.Count > 1 ? $"Delete {targets.Count} pages" : "Delete page", "IconDelete", editor.DeletePagesCommand, page));
        return menu;
    }

    private MenuItem Item(string header, string? icon, System.Windows.Input.ICommand command, PageViewModel page, bool isEnabled = true) => new()
    {
        Header = header,
        Command = command,
        CommandParameter = page,
        IsEnabled = isEnabled,
        Icon = icon is null ? null : new Controls.StrokeIcon { Data = (Avalonia.Media.Geometry?)this.FindResource(icon), Size = 16 },
    };

    // ----- Dragging pages to reorder them (FR-6.4) -----

    private void OnListPointerPressed(ListBox list, PointerPressedEventArgs e)
    {
        _press = null;
        if (_editor is null || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed || IsOnMenuButton(e.Source) || PageAt(e.Source) is not { } found)
        {
            return;
        }

        if (e.ClickCount == 2 && ReferenceEquals(list, OrganiseList))
        {
            // Double-click opens the page for editing.
            _editor.OpenPageCommand.Execute(found.Page);
            e.Handled = true;
            return;
        }

        // Pressing a page that's already selected may start dragging the whole selection, so remember it before the
        // list narrows the selection to the pressed page.
        ImmutableHashSet<PageId> before = _editor.SelectedPages;
        _press = new PagePress(list, found.Page, e.GetPosition(list), before.Contains(found.Page.Id) ? before : [found.Page.Id]);
    }

    private void OnListPointerMoved(ListBox list, PointerEventArgs e)
    {
        if (_press is not { } press || _editor is null || !ReferenceEquals(list, press.List))
        {
            return;
        }

        Point position = e.GetPosition(list);
        if (_dragging is null)
        {
            Vector moved = position - press.Start;
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed || Math.Abs(moved.X) + Math.Abs(moved.Y) < DragThreshold)
            {
                return;
            }

            _dragging = press.Pages;
            _editor.SetPageSelection(press.Pages);
            e.Pointer.Capture(list);
        }

        _dragGap = ReferenceEquals(list, PageList) ? GapAt(e.GetPosition(PageList)) : GridGapAt(position);
        if (ReferenceEquals(list, PageList))
        {
            ShowInsertMarker(_dragGap);
        }
        else
        {
            ShowGridMarker(_dragGap);
        }

        e.Handled = true;
    }

    private void OnListPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragging is { } pages && _dragGap is int gap && _editor is not null)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
            EndPageDrag();
            _editor.MovePages(pages, gap);
            return;
        }

        EndPageDrag();
    }

    private void EndPageDrag()
    {
        bool wasDragging = _dragging is not null;
        _press = null;
        _dragging = null;
        _dragGap = null;
        if (wasDragging)
        {
            ShowInsertMarker(null);
            ShowGridMarker(null);
            SyncListSelection();
        }
    }

    /// <summary>The gap in the Organise grid nearest the pointer: before the first page to its right or below it.</summary>
    private int GridGapAt(Point inList)
    {
        if (_editor is null)
        {
            return 0;
        }

        for (int i = 0; i < _editor.Pages.Count; i++)
        {
            if (OrganiseList.ContainerFromIndex(i) is not Control container || container.TranslatePoint(default, OrganiseList) is not { } topLeft)
            {
                continue;
            }

            var box = new Rect(topLeft, container.Bounds.Size);
            bool isAboveRow = inList.Y < box.Top;
            bool isInRowBeforeCentre = inList.Y <= box.Bottom && inList.X < box.Center.X;
            if (isAboveRow || isInRowBeforeCentre)
            {
                return i;
            }
        }

        return _editor.Pages.Count;
    }

    /// <summary>A vertical bar between pages in the Organise grid (🎨 4).</summary>
    private void ShowGridMarker(int? gap)
    {
        if (gap is not int index || _editor is null || _editor.Pages.Count == 0)
        {
            GridInsertMarker.IsVisible = false;
            return;
        }

        bool isAfterLast = index >= _editor.Pages.Count;
        if (OrganiseList.ContainerFromIndex(isAfterLast ? _editor.Pages.Count - 1 : index) is not Control container
            || container.TranslatePoint(default, OrganisePanel) is not { } topLeft)
        {
            GridInsertMarker.IsVisible = false;
            return;
        }

        double x = isAfterLast ? topLeft.X + container.Bounds.Width + GridMarkerOffset - 2 : topLeft.X - GridMarkerOffset - 2;
        Avalonia.Controls.Canvas.SetLeft(GridInsertMarker, Math.Max(0, x));
        Avalonia.Controls.Canvas.SetTop(GridInsertMarker, topLeft.Y + GridItemPadding);
        GridInsertMarker.IsVisible = true;
    }

    private static bool IsOnMenuButton(object? source) =>
        source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<Button>().Any(button => button.Classes.Contains("pageMenu"));

    private static (ListBoxItem Container, PageViewModel Page)? PageAt(object? source) =>
        source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is { DataContext: PageViewModel page } item
            ? (item, page)
            : null;

    private sealed record PagePress(ListBox List, PageViewModel Page, Point Start, ImmutableHashSet<PageId> Pages);
}
