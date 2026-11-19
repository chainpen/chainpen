using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

public partial class EditorView : UserControl
{
    private EditorViewModel? _editor;
    private int? _insertGap;

    /// <summary>How long the undo toast stays at least (FS-707); it waits longer while it has the pointer or focus.</summary>
    public static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(10);

    private readonly Avalonia.Threading.DispatcherTimer _toastTimer = new() { Interval = ToastTime };

    public EditorView()
    {
        InitializeComponent();
        TitleBar.ActsAsTitleBar(TitleStrip);
        TitleBar.KeepClearOfMenus(TitleStrip.Child!);
        AttachPagesPanelResizer();
        ShareButton.IsVisible = Platform.ShareSheet.IsAvailable;
        ShowToolLetters(ToolKeys.IsOn);
        AttachedToVisualTree += (_, _) => Platform.AppSettings.Changed += OnSettingsChanged;
        DetachedFromVisualTree += (_, _) => Platform.AppSettings.Changed -= OnSettingsChanged;
        _toastTimer.Tick += (_, _) =>
        {
            // Someone reaching for Undo, or tabbed onto it, keeps it open; it goes once they've moved on.
            if (Toast.IsPointerOver || Toast.IsKeyboardFocusWithin)
            {
                return;
            }

            _toastTimer.Stop();
            _editor?.DismissNoticeCommand.Execute(null);
        };
        AttachPageList(PageList);
        AttachPageList(OrganiseList);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        SizeChanged += (_, args) => CollapseToolbar(args.NewSize.Width);
    }

    /// <summary>Widths below which the toolbar collapses (INTERACTION-PATTERNS §3); the smallest window (S2, 1024) has both.</summary>
    private const double CompactSwitchBelowWidth = 1640;
    private const double IconsBelowWidth = 1340;
    private const double LateIconsBelowWidth = 1248;
    private const double MoreMenuBelowWidth = 1248;
    private const double DateInMoreBelowWidth = 1100;
    private const double ShortcutsBelowWidth = 1340;

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
    /// Narrower windows first turn Select, Rotate left and Rotate right into icons, then Cross and Date, then move Shapes
    /// and Draw into More, hide Keyboard shortcuts (it's in the Help menu) and show the Edit content / Organise pages
    /// switch as icons (S3). Nothing may overlap at any width down to the smallest window.
    /// Tick, Text and Sign always keep their labels (NFR-11).
    /// </summary>
    private void CollapseToolbar(double width)
    {
        bool isIconsOnly = width < IconsBelowWidth;
        // A style hides every "collapsible" label, including ones in bars that haven't been shown yet.
        Classes.Set("iconsOnly", isIconsOnly);
        Classes.Set("iconsOnlyLate", width < LateIconsBelowWidth);

        bool usesMoreMenu = width < MoreMenuBelowWidth;
        ShapesButton.IsVisible = !usesMoreMenu;
        DrawButton.IsVisible = !usesMoreMenu;
        MoreButton.IsVisible = usesMoreMenu;

        // At this width the tools row is wider than its slot and was drawn under Undo: the shortcuts are in Help.
        ShortcutsButton.IsVisible = width >= ShortcutsBelowWidth;

        // The smallest windows: Date joins Shapes and Draw in More, so Sign's arrow fits.
        bool isDateInMore = width < DateInMoreBelowWidth;
        DateButton.IsVisible = !isDateInMore;
        MoreDateItem.IsVisible = isDateInMore;
        bool isCompact = width < CompactSwitchBelowWidth;
        ExportLabel.Text = isCompact ? "Export…" : "Export final copy…";
        EditContentLabel.IsVisible = !isCompact;
        OrganiseLabel.IsVisible = !isCompact;
    }

    private readonly Dictionary<Control, (string Plain, string Letter)> _toolTips = [];

    private void OnSettingsChanged(Platform.AppSettings settings) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowToolLetters(settings.UseSingleKeyTools));

    /// <summary>
    /// Tool tooltips name the tool's letter, "(X)", only while single-letter tools are on (UX review 9): off, as they
    /// are by default, the letter would do nothing.
    /// </summary>
    private void ShowToolLetters(bool isOn)
    {
        if (_toolTips.Count == 0)
        {
            foreach (Control control in Tools.GetLogicalDescendants().OfType<Button>())
            {
                if (ToolTip.GetTip(control) is string tip && System.Text.RegularExpressions.Regex.Match(tip, @" \([A-Z]\)$") is { Success: true } letter)
                {
                    _toolTips[control] = (tip[..letter.Index], letter.Value);
                }
            }
        }

        foreach ((Control control, (string plain, string letter)) in _toolTips)
        {
            ToolTip.SetTip(control, isOn ? plain + letter : plain);
        }
    }

    /// <summary>
    /// F6 (FS-708): moves focus to the next area — toolbar, page list, page, status bar — skipping hidden ones.
    /// </summary>
    public void FocusNextArea(bool backwards)
    {
        Control?[] areas =
        [
            Tools.IsEffectivelyVisible ? Tools.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEffectivelyVisible) : EditContentSwitch,
            OrganiseList.IsEffectivelyVisible ? OrganiseList : PagesPanel.IsEffectivelyVisible ? PageList : null,
            Scroller.IsEffectivelyVisible ? Scroller : null,
            PageBox.IsEffectivelyVisible ? PageBox : null,
        ];
        Control[] visible = [.. areas.OfType<Control>()];
        if (visible.Length == 0)
        {
            return;
        }

        IInputElement? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        int current = Array.FindIndex(visible, area => focused is Visual visual && (ReferenceEquals(area, visual) || area.IsVisualAncestorOf(visual)));
        int next = current < 0 ? 0 : (current + (backwards ? visible.Length - 1 : 1)) % visible.Length;
        Control target = visible[next];
        if (target is ListBox list)
        {
            // Land on the selected or current page, as arrow keys then move from there.
            int index = list.SelectedIndex >= 0 ? list.SelectedIndex : _editor?.CurrentPageIndex ?? 0;
            if (list.ContainerFromIndex(index) is Control container)
            {
                container.Focus(NavigationMethod.Tab);
                return;
            }
        }

        target.Focus(NavigationMethod.Tab);
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

            if (args.PropertyName == nameof(EditorViewModel.Notice))
            {
                _toastTimer.Stop();
                if (editor.HasToast)
                {
                    _toastTimer.Start();
                }
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


    /// <summary>Share… on the export notice (FS-613): the system's share sheet, next to the button.</summary>
    private void OnShare(object? sender, RoutedEventArgs e)
    {
        if (_editor?.Exported is { } exported && sender is Control button)
        {
            Platform.ShareSheet.Show(exported.Path, button);
        }
    }

    private void OnCloseSaveMenu(object? sender, RoutedEventArgs e) => SaveMenuButton.Flyout?.Hide();

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

    // ----- Pages panel width -----

    // The space beside a thumbnail in the panel (its default 196 less the 116 thumbnail), and a page's proportions.
    private const double PanelAroundThumb = 80;
    private const double ThumbRatio = 150.0 / 116;
    private const double ResizeStep = 16;

    private bool _isResizingPanel;

    /// <summary>
    /// The pages panel can be made wider or narrower from its right edge, by dragging or with the arrow keys once the
    /// edge has focus; a double-click goes back to the standard width. The width is remembered, and thumbnails follow it.
    /// </summary>
    private void AttachPagesPanelResizer()
    {
        SetPagesPanelWidth(Platform.AppSettings.Load().PagesPanelWidth);
        PagesPanelResizer.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(PagesPanelResizer).Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                SetPagesPanelWidth(Platform.AppSettings.DefaultPagesPanelWidth);
                RememberPagesPanelWidth();
            }
            else
            {
                _isResizingPanel = true;
                e.Pointer.Capture(PagesPanelResizer);
            }

            e.Handled = true;
        };
        PagesPanelResizer.PointerMoved += (_, e) =>
        {
            if (_isResizingPanel && PagesPanel.Parent is Visual row)
            {
                SetPagesPanelWidth(e.GetPosition(row).X);
            }
        };
        PagesPanelResizer.PointerReleased += (_, e) =>
        {
            if (_isResizingPanel)
            {
                _isResizingPanel = false;
                e.Pointer.Capture(null);
                RememberPagesPanelWidth();
            }
        };
        PagesPanelResizer.KeyDown += (_, e) =>
        {
            double step = e.Key switch { Key.Left => -ResizeStep, Key.Right => ResizeStep, _ => 0 };
            if (step != 0)
            {
                SetPagesPanelWidth(PagesPanel.Width + step);
                RememberPagesPanelWidth();
                e.Handled = true;
            }
        };
    }

    private void SetPagesPanelWidth(double width)
    {
        width = Math.Clamp(Math.Round(width), Platform.AppSettings.MinPagesPanelWidth, Platform.AppSettings.MaxPagesPanelWidth);
        PagesPanel.Width = width;
        double thumb = width - PanelAroundThumb;
        Resources["PagesThumbWidth"] = thumb;
        Resources["PagesThumbHeight"] = Math.Round(thumb * ThumbRatio);
        InsertMarker.Width = thumb + 16;
        Avalonia.Automation.AutomationProperties.SetHelpText(PagesPanelResizer, $"{width:0} points wide. Left and Right arrow keys change it.");
    }

    /// <summary>View → Reset layout: the pages panel back at its standard width and shown, and the page fitted to the width.</summary>
    public void ResetLayout()
    {
        SetPagesPanelWidth(Platform.AppSettings.DefaultPagesPanelWidth);
        RememberPagesPanelWidth();
        if (DataContext is EditorViewModel { IsPagesPanelVisible: false } editor)
        {
            editor.IsPagesPanelVisible = true;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(FitWidth, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RememberPagesPanelWidth()
    {
        double width = PagesPanel.Width;
        Platform.AppSettings.Update(settings => settings with { PagesPanelWidth = width });
    }
}
