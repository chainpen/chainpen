using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Fillsign.App.Localization;
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
        SizeChanged += (_, args) =>
        {
            CollapseToolbar(args.NewSize.Width);
            PlaceGuideArrow();
        };
    }

    private Button? _guideArrowTarget;

    /// <summary>Restarts the arrow's bounce (a style animation on the <c>bouncing</c> class) as it arrives at a button.</summary>
    private void BounceGuideArrow()
    {
        GuideArrow.Classes.Remove("bouncing");
        if (Platform.ReducedMotion.IsOn)
        {
            // The arrow still points, it just doesn't move.
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => GuideArrow.Classes.Add("bouncing"), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Points the practice guide's arrow up at the button it wants next, once that button's been laid out. Hidden when
    /// there's no guide, or several buttons are lit (the nudge).
    /// </summary>
    private void PlaceGuideArrow() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                Button[] lit = _editor is { IsGuiding: true }
                    ? [.. ToolbarBar.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("guided") && button.IsEffectivelyVisible)]
                    : [];
                if (lit is [Button target] && target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height), GuideArrowLayer) is { } tip)
                {
                    Avalonia.Controls.Canvas.SetLeft(GuideArrow, tip.X - 11);
                    Avalonia.Controls.Canvas.SetTop(GuideArrow, tip.Y + 3);
                    GuideArrow.IsVisible = true;
                    if (!ReferenceEquals(target, _guideArrowTarget))
                    {
                        _guideArrowTarget = target;
                        BounceGuideArrow();
                    }
                }
                else
                {
                    GuideArrow.IsVisible = false;
                    _guideArrowTarget = null;
                    GuideArrow.Classes.Remove("bouncing");
                }
            },
            Avalonia.Threading.DispatcherPriority.Background);

    /// <summary>The last stage of <see cref="ApplyCollapse"/>, with everything collapsed for the smallest window.</summary>
    private const int MostCollapsed = 5;

    /// <summary>Room kept free at the end of the toolbar, so it never looks crammed just before the next stage.</summary>
    private const double ToolbarSlack = 8;

    /// <summary>Files dropped on "Add to this document" or between pages: (paths, insert position or null for the end).</summary>
    public event Action<IReadOnlyList<string>, int?>? AddFilesRequested;

    /// <summary>Files dropped on "Open in a new window".</summary>
    public event Action<IReadOnlyList<string>>? NewWindowRequested;

    /// <summary>The Pages panel's Add button.</summary>
    public event EventHandler? AddPagesRequested;

    /// <summary>For a view rebuilt over an open document: keep its zoom rather than fitting the width.</summary>
    public void KeepZoom() => Canvas.KeepZoom();

    /// <summary>Scrolls the page with this index into view.</summary>
    public void ShowPage(int index) => Canvas.ScrollToPage(index);

    public void ZoomIn() => Canvas.ZoomStep(+1);

    public void ZoomOut() => Canvas.ZoomStep(-1);

    public void FitWidth() => Canvas.FitWidth();

    /// <summary>
    /// Narrower windows collapse the toolbar in the stages of <see cref="ApplyCollapse"/> (S3), as far as it takes to fit
    /// the labels in the interface's language. Nothing may overlap at any width down to the smallest window.
    /// Tick, Text and Sign always keep their labels (NFR-11).
    /// </summary>
    private void CollapseToolbar(double width)
    {
        // Measured rather than at fixed widths, so it suits every language: German labels collapse sooner, Japanese
        // ones later. Each stage collapses a little more until the toolbar fits.
        if (width <= 0 || ToolbarBar.Child is not { } content)
        {
            return;
        }

        for (int stage = 0; stage <= MostCollapsed; stage++)
        {
            ApplyCollapse(stage);

            // Showing or hiding a label only marks the label itself for measuring; the panels around it would answer
            // from their last measurement, so they're all measured afresh.
            foreach (Layoutable layoutable in content.GetVisualDescendants().OfType<Layoutable>().Prepend(content))
            {
                layoutable.InvalidateMeasure();
            }

            content.Measure(Size.Infinity);
            if (content.DesiredSize.Width + ToolbarBar.Padding.Left + ToolbarBar.Padding.Right + ToolbarSlack <= width)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Stage 1: the Edit content / Organise pages switch shows icons and Export is shortened. 2: Select, Rotate left and
    /// Rotate right become icons and Keyboard shortcuts goes (it's in the Help menu). 3: Cross and Date become icons
    /// and Shapes and Draw move into More. 4: Date joins them in More, so Sign's arrow fits. 5: for the smallest windows
    /// in longer languages, Initials, More and Save become icons too (they keep their accessible names).
    /// </summary>
    private void ApplyCollapse(int stage)
    {
        bool isCompact = stage >= 1;
        ExportLabel.Text = isCompact ? Strings.Editor_Export : Strings.Editor_ExportFinalCopy;
        EditContentLabel.IsVisible = !isCompact;
        OrganiseLabel.IsVisible = !isCompact;

        // A style hides every "collapsible" label, including ones in bars that haven't been shown yet. The toolbar's own
        // are also set here, so measuring sees them straight away.
        Classes.Set("iconsOnly", stage >= 2);
        Classes.Set("iconsOnlyLate", stage >= 3);
        foreach (TextBlock label in ToolbarBar.GetLogicalDescendants().OfType<TextBlock>())
        {
            if (label.Classes.Contains("collapsible"))
            {
                label.IsVisible = stage < 2;
            }
            else if (label.Classes.Contains("collapsibleLate"))
            {
                label.IsVisible = stage < 3;
            }
        }

        ShortcutsButton.IsVisible = stage < 2;

        bool usesMoreMenu = stage >= 3;
        ShapesButton.IsVisible = !usesMoreMenu;
        DrawButton.IsVisible = !usesMoreMenu;
        MoreButton.IsVisible = usesMoreMenu;

        bool isLeastRoom = stage >= 5;
        InitialsLabel.IsVisible = !isLeastRoom;
        MoreLabel.IsVisible = !isLeastRoom;
        SaveLabel.IsVisible = !isLeastRoom;

        bool isDateInMore = stage >= 4;
        DateButton.IsVisible = !isDateInMore;
        MoreDateItem.IsVisible = isDateInMore;
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
        editor.PropertyChanged += OnEditorPropertyChanged;
        editor.PageSelectionChanged += SyncListSelection;
        editor.PagesChanged += SyncListSelection;
    }

    /// <summary>
    /// Stops following the document, for a view that's being replaced while the document stays open (a new document,
    /// or the interface rebuilt in another language). Otherwise the old view would keep handling its events, and its
    /// toast timer would close a toast the new view is showing.
    /// </summary>
    public void Detach()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        editor.PropertyChanged -= OnEditorPropertyChanged;
        editor.PageSelectionChanged -= SyncListSelection;
        editor.PagesChanged -= SyncListSelection;
        Canvas.Detach();
        _toastTimer.Stop();
        _editor = null;
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_editor is not { } editor)
        {
            return;
        }

        if (args.PropertyName == nameof(EditorViewModel.CurrentPageIndex) && editor.CurrentPageIndex < editor.Pages.Count)
        {
            PageList.ScrollIntoView(editor.CurrentPageIndex);
        }

        // Organise pages has a different toolbar, so it's fitted again.
        if (args.PropertyName == nameof(EditorViewModel.IsOrganising))
        {
            CollapseToolbar(Bounds.Width);
        }

        // Switching to Organise pages hides the tools, so the arrow moves or goes with them.
        if (args.PropertyName is nameof(EditorViewModel.IsGuiding) or nameof(EditorViewModel.IsOrganising) or nameof(EditorViewModel.GuideLightsTick)
            or nameof(EditorViewModel.GuideLightsText) or nameof(EditorViewModel.GuideLightsSign) or nameof(EditorViewModel.GuideLightsExport))
        {
            PlaceGuideArrow();
        }

        if (args.PropertyName == nameof(EditorViewModel.Notice))
        {
            _toastTimer.Stop();
            if (editor.HasToast)
            {
                _toastTimer.Start();
            }
        }
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
        DropCaption.Text = Strings.Editor_WhereYouLetGoDecides;
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
        Avalonia.Automation.AutomationProperties.SetHelpText(PagesPanelResizer, Text.Format(Strings.Editor_PagesPanelWidthHelp, width));
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
