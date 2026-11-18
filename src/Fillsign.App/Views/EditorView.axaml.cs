using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

public partial class EditorView : UserControl
{
    private EditorViewModel? _editor;
    private int? _insertGap;

    public EditorView()
    {
        InitializeComponent();
        AttachPageList(PageList);
        AttachPageList(OrganiseList);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        SizeChanged += (_, args) => CollapseToolbar(args.NewSize.Width);
    }

    /// <summary>Widths below which the toolbar collapses (INTERACTION-PATTERNS §3); the smallest window (S2, 1024) has both.</summary>
    private const double IconsBelowWidth = 1180;
    private const double MoreMenuBelowWidth = 1100;

    /// <summary>The person asked to open files (Open… or Mod+O).</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Files dropped on "Add to this document" or between pages: (paths, insert position or null for the end).</summary>
    public event Action<IReadOnlyList<string>, int?>? AddFilesRequested;

    /// <summary>Files dropped on "Open in a new window".</summary>
    public event Action<IReadOnlyList<string>>? NewWindowRequested;

    /// <summary>The Pages panel's Add button.</summary>
    public event EventHandler? AddPagesRequested;

    public void ZoomIn() => Canvas.ZoomStep(+1);

    public void ZoomOut() => Canvas.ZoomStep(-1);

    public void FitWidth() => Canvas.FitWidth();

    /// <summary>
    /// Narrower windows first turn Select, Cross, Date, Rotate left and Rotate right into icons, then move Shapes and
    /// Draw into More and show the Edit content / Organise pages switch as icons (S3).
    /// Tick, Text and Sign always keep their labels (NFR-11).
    /// </summary>
    private void CollapseToolbar(double width)
    {
        bool isIconsOnly = width < IconsBelowWidth;
        // A style hides every "collapsible" label, including ones in bars that haven't been shown yet.
        Classes.Set("iconsOnly", isIconsOnly);

        bool usesMoreMenu = width < MoreMenuBelowWidth;
        ShapesButton.IsVisible = !usesMoreMenu;
        DrawButton.IsVisible = !usesMoreMenu;
        MoreButton.IsVisible = usesMoreMenu;
        OpenLabel.IsVisible = !usesMoreMenu;
        EditContentLabel.IsVisible = !usesMoreMenu;
        OrganiseLabel.IsVisible = !usesMoreMenu;
    }

    /// <summary>Whether keyboard focus is on the page view (not the toolbar, bar or page list).</summary>
    public bool IsPageFocused(IInputElement? focused) =>
        focused is Visual visual && (ReferenceEquals(visual, Scroller) || Scroller.IsVisualAncestorOf(visual));

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not EditorViewModel editor || ReferenceEquals(editor, _editor))
        {
            return;
        }

        _editor = editor;
        Canvas.DamagedCardFactory = page => new DamagedPageCard(editor) { DataContext = page };
        Canvas.Attach(editor, Scroller);
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EditorViewModel.CurrentPageIndex) && editor.CurrentPageIndex < editor.Pages.Count)
            {
                PageList.ScrollIntoView(editor.CurrentPageIndex);
            }
        };
        editor.PageSelectionChanged += SyncListSelection;
        editor.PagesChanged += SyncListSelection;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Scroller.Focus();
    }

    private void OnOpen(object? sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddPages(object? sender, RoutedEventArgs e) => AddPagesRequested?.Invoke(this, EventArgs.Empty);

    private void OnZoomIn(object? sender, RoutedEventArgs e) => ZoomIn();

    private void OnZoomOut(object? sender, RoutedEventArgs e) => ZoomOut();

    private void OnFitWidth(object? sender, RoutedEventArgs e) => FitWidth();

    private void OnPageBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            GoToTypedPage();
            Scroller.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PageBox.Text = _editor?.CurrentPageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Scroller.Focus();
            e.Handled = true;
        }
    }

    private void OnPageBoxLostFocus(object? sender, RoutedEventArgs e) =>
        PageBox.Text = _editor?.CurrentPageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void GoToTypedPage()
    {
        if (_editor is not null && int.TryParse(PageBox.Text, out int number))
        {
            _editor.GoToPageCommand.Execute(number);
        }
    }

    // ----- Dropping files (E9) -----

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        if (_editor is { IsOrganising: true })
        {
            // Organise pages: files go between the pages where they're dropped (🎨 4).
            _insertGap = GridGapAt(e.GetPosition(OrganiseList));
            ShowGridMarker(_insertGap);
            AddTarget.Classes.Set("active", true);
            NewWindowTarget.Classes.Set("active", false);
            return;
        }

        DropOverlay.IsVisible = true;
        DropCaption.Text = "Where you let go decides: add the pages here, or open them on their own.";
        Canvas.Opacity = 0.5;

        bool isOverPages = PagesPanel.IsVisible && PagesPanel.Bounds.Contains(e.GetPosition(PagesPanel.Parent as Visual ?? this));
        _insertGap = isOverPages ? GapAt(e.GetPosition(PageList)) : null;
        ShowInsertMarker(_insertGap);

        bool isOverAdd = !isOverPages && e.GetPosition(DropOverlay).X < DropOverlay.Bounds.Width / 2;
        AddTarget.Classes.Set("active", isOverAdd || isOverPages);
        NewWindowTarget.Classes.Set("active", !isOverAdd && !isOverPages);
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => EndDrag();

    private void OnDrop(object? sender, DragEventArgs e)
    {
        bool addsHere = AddTarget.Classes.Contains("active");
        int? gap = _insertGap;
        EndDrag();
        string[] paths = [.. (e.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).OfType<string>()];
        if (paths.Length == 0)
        {
            return;
        }

        if (addsHere)
        {
            AddFilesRequested?.Invoke(paths, gap);
        }
        else
        {
            NewWindowRequested?.Invoke(paths);
        }
    }

    private void EndDrag()
    {
        ShowGridMarker(null);
        DropOverlay.IsVisible = false;
        Canvas.Opacity = 1;
        ShowInsertMarker(null);
        _insertGap = null;
    }

    /// <summary>The gap between thumbnails nearest the pointer: 0 is before page 1.</summary>
    private int GapAt(Point inList)
    {
        if (_editor is null)
        {
            return 0;
        }

        int gap = _editor.Pages.Count;
        for (int i = 0; i < _editor.Pages.Count; i++)
        {
            if (PageList.ContainerFromIndex(i) is not Control container)
            {
                continue;
            }

            Point? topLeft = container.TranslatePoint(default, PageList);
            if (topLeft is { } top && inList.Y < top.Y + (container.Bounds.Height / 2))
            {
                gap = i;
                break;
            }
        }

        return gap;
    }

    private void ShowInsertMarker(int? gap)
    {
        if (gap is not int index || _editor is null)
        {
            InsertMarker.IsVisible = false;
            return;
        }

        double y = 0;
        Control? container = index < _editor.Pages.Count
            ? PageList.ContainerFromIndex(index) as Control
            : PageList.ContainerFromIndex(_editor.Pages.Count - 1) as Control;
        if (container?.TranslatePoint(default, PageList) is { } point)
        {
            y = index < _editor.Pages.Count ? point.Y - 9 : point.Y + container.Bounds.Height - 9;
        }

        Avalonia.Controls.Canvas.SetLeft(InsertMarker, (PageList.Bounds.Width - InsertMarker.Width) / 2);
        Avalonia.Controls.Canvas.SetTop(InsertMarker, Math.Max(0, y));
        InsertMarker.IsVisible = true;
    }
}
