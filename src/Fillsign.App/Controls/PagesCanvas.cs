using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Fillsign.App.Rendering;
using Fillsign.App.ViewModels;
using Fillsign.Core.Model;

namespace Fillsign.App.Controls;

/// <summary>
/// The continuous page view (FS-204, FS-205): one control for every page, drawing only those in the viewport.
/// Pages are laid out in points × zoom; a page's last image is stretched while zooming and replaced by a sharp render
/// 150 ms after zooming stops (FR-2.4). Damaged pages keep their size and show a card (D13) as a child control.
/// </summary>
public sealed partial class PagesCanvas : Panel
{
    public const double SidePadding = 24;
    public const double TopPadding = 28;

    /// <summary>Space between pages; holds the page number under each page (FR-2.5).</summary>
    public const double PageGap = 36;

    private const double CaptionOffset = 8;
    private static readonly TimeSpan s_sharpenDelay = TimeSpan.FromMilliseconds(150);
    private static readonly BoxShadows s_pageShadow = BoxShadows.Parse("0 1 3 0 #29000000, 0 8 24 0 #14000000");

    private readonly DispatcherTimer _sharpenTimer;
    private readonly Dictionary<PageId, Control> _cards = [];

    // Panel.Render is sealed, so pages are drawn by a full-size layer under the damaged-page cards.
    private readonly DrawingLayer _layer;

    // Above the pages: only what follows the pointer, repainted on every mouse move (DrawHover).
    private readonly DrawingLayer _hoverLayer;
    private EditorViewModel? _editor;
    private ScrollViewer? _scroller;
    private double[] _tops = [];
    private double _renderZoom;
    private bool _hasFitted;

    // Zoom when a touch pinch started; PinchEventArgs.Scale is relative to it.
    private double _pinchStartZoom;

    public PagesCanvas()
    {
        _sharpenTimer = new DispatcherTimer(s_sharpenDelay, DispatcherPriority.Background, (_, _) => Sharpen());
        ClipToBounds = true;
        _layer = new DrawingLayer(DrawPages);
        Children.Add(_layer);
        _hoverLayer = new DrawingLayer(DrawHover) { IsHitTestVisible = false };
        Children.Add(_hoverLayer);
    }

    /// <summary>Creates the D13 card for a damaged page; set by the view.</summary>
    public Func<PageViewModel, Control>? DamagedCardFactory { get; set; }

    private double Zoom => _editor?.Zoom ?? 1;

    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    public void Attach(EditorViewModel editor, ScrollViewer scroller)
    {
        _editor = editor;
        _scroller = scroller;
        _renderZoom = editor.Zoom;
        editor.PagesChanged += OnPagesChanged;
        editor.ScrollToPageRequested += ScrollToPage;
        editor.ScrollToAreaRequested += ScrollToArea;
        editor.HoverChanged += () => _hoverLayer.InvalidateVisual();
        editor.Renderer.PageRendered += _ => InvalidateLayers();
        AttachItems(editor);
        scroller.ScrollChanged += (_, _) =>
        {
            InvalidateLayers();
            if (_cards.Count > 0)
            {
                InvalidateArrange();
            }

            UpdateCurrentPage();
            RequestVisible();
        };
        scroller.SizeChanged += (_, _) =>
        {
            if (!_hasFitted && scroller.Viewport.Width > 0)
            {
                _hasFitted = true;
                FitWidth();
            }
        };
        scroller.AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroller.AddHandler(PointerTouchPadGestureMagnifyEvent, OnMagnify, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroller.AddHandler(PinchEvent, OnPinch, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroller.AddHandler(PinchEndedEvent, (_, _) => _pinchStartZoom = 0, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroller.GestureRecognizers.Add(new PinchGestureRecognizer());
        OnPagesChanged();
    }

    /// <summary>A document first opens zoomed to fit the page width (FR-2.2).</summary>
    public void FitWidth()
    {
        if (_editor is null || _scroller is null || _editor.Pages.Count == 0)
        {
            return;
        }

        double widest = _editor.Pages.Max(page => page.DisplaySize.Width);
        double zoom = (_scroller.Viewport.Width - (2 * SidePadding)) / widest;
        SetZoom(zoom, new Point(_scroller.Viewport.Width / 2, 0));
    }

    /// <summary>Steps to the next zoom level up or down, keeping the middle of the view in place (FR-2.3).</summary>
    public void ZoomStep(int direction)
    {
        double zoom = Zoom;
        double next = direction > 0
            ? EditorViewModel.ZoomSteps.FirstOrDefault(step => step > zoom + 0.001, EditorViewModel.MaxZoom)
            : EditorViewModel.ZoomSteps.LastOrDefault(step => step < zoom - 0.001, EditorViewModel.MinZoom);
        SetZoom(next, ViewportCentre());
    }

    /// <summary>Zooms about a point in viewport coordinates, keeping the document point under it fixed (FR-2.3).</summary>
    public void SetZoom(double zoom, Point viewportAnchor)
    {
        if (_editor is null || _scroller is null)
        {
            return;
        }

        zoom = Math.Clamp(zoom, EditorViewModel.MinZoom, EditorViewModel.MaxZoom);
        double oldZoom = Zoom;
        Vector anchorOld = _scroller.Offset + viewportAnchor;

        // Convert the anchor to document space: x relative to the centred column, y through the page layout.
        double contentWidthOld = Bounds.Width;
        double centreOld = contentWidthOld / 2;
        double docX = (anchorOld.X - centreOld) / oldZoom;
        double docY = DocumentY(anchorOld.Y, oldZoom);

        _editor.Zoom = zoom;
        InvalidateMeasure();
        _scroller.UpdateLayout();

        double centreNew = Bounds.Width / 2;
        double newX = (docX * zoom) + centreNew - viewportAnchor.X;
        double newY = ViewY(docY, zoom) - viewportAnchor.Y;
        _scroller.Offset = new Vector(Math.Max(0, newX), Math.Max(0, newY));
        InvalidateLayers();

        // Stretch the images we have now; render sharply once zooming pauses.
        _sharpenTimer.Stop();
        _sharpenTimer.Start();
    }

    public void ScrollToPage(int index)
    {
        if (_scroller is null || index < 0 || index >= _tops.Length)
        {
            return;
        }

        _scroller.Offset = new Vector(_scroller.Offset.X, Math.Max(0, PageTop(index) - 16));
    }

    /// <summary>Brings an area of a page into view, a third of the way down, as when stepping to a possible gap.</summary>
    public void ScrollToArea(int index, Fillsign.Core.Model.PageRect area)
    {
        if (_scroller is null || index < 0 || index >= _tops.Length)
        {
            return;
        }

        Rect page = PageRect(index);
        var view = new Rect(page.X + (area.X * Zoom), page.Y + (area.Y * Zoom), area.Width * Zoom, area.Height * Zoom);
        if (Viewport().Contains(view))
        {
            return;
        }

        double y = PageTop(index) + (area.Y * Zoom) - (_scroller.Viewport.Height / 3);
        double x = page.Width > _scroller.Viewport.Width ? page.X + (area.X * Zoom) - (_scroller.Viewport.Width / 3) : _scroller.Offset.X;
        _scroller.Offset = new Vector(Math.Max(0, x), Math.Max(0, y));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_editor is null)
        {
            return default;
        }

        var pages = _editor.Pages;
        if (_tops.Length != pages.Count)
        {
            _tops = new double[pages.Count];
        }

        double zoom = Zoom, y = TopPadding, maxWidth = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            _tops[i] = y;
            y += (pages[i].DisplaySize.Height * zoom) + PageGap;
            maxWidth = Math.Max(maxWidth, pages[i].DisplaySize.Width * zoom);
        }

        foreach (Control card in _cards.Values)
        {
            card.Measure(new Size(420, double.PositiveInfinity));
        }

        _layer.Measure(availableSize);
        _hoverLayer.Measure(availableSize);
        MeasureTypingBox();
        MeasureFieldBox();

        double viewportWidth = _scroller?.Viewport.Width ?? 0;
        return new Size(Math.Max(viewportWidth, maxWidth + (2 * SidePadding)), y);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layer.Arrange(new Rect(finalSize));
        _hoverLayer.Arrange(new Rect(finalSize));
        if (_editor is null)
        {
            return finalSize;
        }

        ArrangeTypingBox();
        ArrangeFieldBox();

        for (int i = 0; i < _editor.Pages.Count; i++)
        {
            PageViewModel page = _editor.Pages[i];
            if (_cards.TryGetValue(page.Id, out Control? card))
            {
                // Centre the card in the part of the page that's on screen, so it's visible at any zoom.
                Rect rect = PageRect(i);
                Rect visible = rect.Intersect(Viewport());
                Rect area = visible.Height > card.DesiredSize.Height ? visible : rect;
                Size size = card.DesiredSize;
                double y = Math.Clamp(area.Center.Y - (size.Height / 2), rect.Top + 16, Math.Max(rect.Top + 16, rect.Bottom - size.Height - 16));
                card.Arrange(new Rect(rect.Center.X - (size.Width / 2), y, size.Width, size.Height));
            }
        }

        return finalSize;
    }

    private void DrawPages(DrawingContext context)
    {
        if (_editor is null || _scroller is null || _editor.Pages.Count == 0)
        {
            return;
        }

        Rect viewport = Viewport();
        var (first, last) = VisibleRange(viewport);
        IBrush pageBrush = Brush("Page", Brushes.White);
        IBrush skeleton = Brush("SurfaceSunken", Brushes.LightGray);
        IBrush muted = Brush("TextMuted", Brushes.Gray);
        double scale = Zoom * RenderScaling;

        for (int i = first; i <= last; i++)
        {
            PageViewModel page = _editor.Pages[i];
            Rect rect = PageRect(i);
            if (page.IsDamaged)
            {
                DrawDamaged(context, rect);
            }
            else
            {
                context.DrawRectangle(pageBrush, null, new RoundedRect(rect), s_pageShadow);
                RenderedPage? image = page.IsLoaded ? _editor.Renderer.TryGet(page.Entry.SourceIndex, page.Entry.Rotation, scale) : null;
                if (image is not null)
                {
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
                    {
                        context.DrawImage(image.Bitmap, image.SourceRect, rect);
                    }
                }
                else if (!page.IsLoaded)
                {
                    DrawSkeletonLines(context, rect, skeleton);
                }
            }

            DrawItemsOn(context, page, rect);

            var caption = new FormattedText(
                $"{page.Number}",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default),
                12,
                muted);
            context.DrawText(caption, new Point(rect.Center.X - (caption.Width / 2), rect.Bottom + CaptionOffset));
        }
    }

    private void OnPagesChanged()
    {
        if (_editor is null)
        {
            return;
        }

        SyncCards();
        InvalidateMeasure();
        InvalidateLayers();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateCurrentPage();
            RequestVisible();
        }, DispatcherPriority.Background);
    }

    private void SyncCards()
    {
        if (_editor is null)
        {
            return;
        }

        var wanted = _editor.Pages.Where(_editor.ShowsDamagedCard).ToDictionary(page => page.Id);
        foreach (PageId gone in _cards.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            Children.Remove(_cards[gone]);
            _cards.Remove(gone);
        }

        foreach (var (id, page) in wanted)
        {
            if (_cards.TryGetValue(id, out Control? existing))
            {
                existing.DataContext = page;
                continue;
            }

            if (DamagedCardFactory is { } factory)
            {
                Control card = factory(page);
                _cards[id] = card;
                Children.Add(card);
            }
        }
    }

    private void Sharpen()
    {
        _sharpenTimer.Stop();
        _renderZoom = Zoom;
        RequestVisible();
        InvalidateLayers();
    }

    private void RequestVisible()
    {
        if (_editor is null || _scroller is null || _editor.Pages.Count == 0)
        {
            return;
        }

        var (first, last) = VisibleRange(Viewport());
        double scale = _renderZoom * RenderScaling;

        // Visible pages first, then two either side so normal scrolling finds them ready.
        var order = new List<int>();
        for (int i = first; i <= last; i++)
        {
            order.Add(i);
        }

        for (int d = 1; d <= 2; d++)
        {
            if (last + d < _editor.Pages.Count)
            {
                order.Add(last + d);
            }

            if (first - d >= 0)
            {
                order.Add(first - d);
            }
        }

        _editor.Renderer.Request(
            RenderLane.View,
            order.Select(i => _editor.Pages[i])
                .Where(page => page.IsLoaded && !page.IsDamaged)
                .Select(page => new RenderRequest(page.Entry.SourceIndex, page.Entry.Rotation, scale)));
    }

    /// <summary>The current page is the one filling most of the viewport.</summary>
    private void UpdateCurrentPage()
    {
        if (_editor is null || _scroller is null || _editor.Pages.Count == 0)
        {
            return;
        }

        Rect viewport = Viewport();
        var (first, last) = VisibleRange(viewport);
        int best = first;
        double bestArea = -1;
        for (int i = first; i <= last; i++)
        {
            Rect visible = PageRect(i).Intersect(viewport);
            double area = visible.Width * visible.Height;
            if (area > bestArea)
            {
                (best, bestArea) = (i, area);
            }
        }

        _editor.CurrentPageIndex = best;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl on Windows and Linux, ⌘ on macOS: zoom about the pointer instead of scrolling.
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || _scroller is null)
        {
            return;
        }

        e.Handled = true;
        SetZoom(Zoom * Math.Pow(1.0015, e.Delta.Y * 100), e.GetPosition(_scroller));
    }

    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        if (_scroller is null)
        {
            return;
        }

        e.Handled = true;
        SetZoom(Zoom * (1 + e.Delta.X), e.GetPosition(_scroller));
    }

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        if (_scroller is null)
        {
            return;
        }

        if (_pinchStartZoom == 0)
        {
            _pinchStartZoom = Zoom;
        }

        e.Handled = true;
        SetZoom(_pinchStartZoom * e.Scale, e.ScaleOrigin);
    }

    private void DrawDamaged(DrawingContext context, Rect rect)
    {
        // Hatched placeholder that keeps the page's real size (D13).
        IBrush stripeA = Brush("SurfaceApp", Brushes.WhiteSmoke);
        IBrush stripeB = Brush("BorderSubtle", Brushes.Gainsboro);
        context.FillRectangle(stripeA, rect);
        using (context.PushClip(rect))
        {
            var pen = new Pen(stripeB, 6);
            for (double x = rect.Left - rect.Height; x < rect.Right; x += 16)
            {
                context.DrawLine(pen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
            }
        }

        var border = new Pen(Brush("BorderControlOnSunken", Brushes.Gray), 2, new DashStyle([4, 3], 0));
        context.DrawRectangle(null, border, rect.Deflate(1));
    }

    private static void DrawSkeletonLines(DrawingContext context, Rect rect, IBrush brush)
    {
        double margin = rect.Width * 0.1;
        double lineHeight = Math.Max(4, rect.Height * 0.012);
        for (double y = rect.Top + (rect.Height * 0.1); y < rect.Bottom - (rect.Height * 0.1); y += lineHeight * 3)
        {
            double width = (rect.Width - (2 * margin)) * (0.6 + (0.4 * Math.Abs(Math.Sin(y))));
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(rect.Left + margin, y, width, lineHeight), 2));
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : fallback;

    private Rect Viewport() =>
        _scroller is null ? default : new Rect(_scroller.Offset.X, _scroller.Offset.Y, _scroller.Viewport.Width, _scroller.Viewport.Height);

    private Point ViewportCentre() => _scroller is null ? default : new Point(_scroller.Viewport.Width / 2, _scroller.Viewport.Height / 2);

    private double PageTop(int index) => _tops[index];

    private Rect PageRect(int index)
    {
        PageSize size = _editor!.Pages[index].DisplaySize;
        double w = size.Width * Zoom, h = size.Height * Zoom;
        return new Rect(Math.Max(SidePadding, (Bounds.Width - w) / 2), _tops[index], w, h);
    }

    /// <summary>A y offset in the view → (page index + fraction within the page), so zooming keeps the same spot.</summary>
    private double DocumentY(double viewY, double zoom)
    {
        if (_editor is null || _tops.Length == 0)
        {
            return 0;
        }

        int index = LowerBound(viewY, zoom);
        double pageHeight = _editor.Pages[index].DisplaySize.Height * zoom;
        // Not clamped: above the first page (the top padding) gives a negative fraction, so it's kept too.
        return index + Math.Min(1, (viewY - _tops[index]) / (pageHeight + PageGap));
    }

    private double ViewY(double documentY, double zoom)
    {
        if (_editor is null || _tops.Length == 0)
        {
            return 0;
        }

        int index = Math.Clamp((int)documentY, 0, _tops.Length - 1);
        double pageHeight = _editor.Pages[index].DisplaySize.Height * zoom;
        return _tops[index] + ((documentY - index) * (pageHeight + PageGap));
    }

    private (int First, int Last) VisibleRange(Rect viewport)
    {
        if (_tops.Length == 0)
        {
            return (0, -1);
        }

        int first = LowerBound(viewport.Top, Zoom);
        int last = first;
        while (last + 1 < _tops.Length && _tops[last + 1] < viewport.Bottom)
        {
            last++;
        }

        return (first, last);
    }

    /// <summary>The first page whose bottom (including its gap) is below <paramref name="y"/>: O(log n) per frame.</summary>
    private int LowerBound(double y, double zoom)
    {
        int lo = 0, hi = _tops.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_tops[mid] + (_editor!.Pages[mid].DisplaySize.Height * zoom) + PageGap < y)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private void InvalidateLayers()
    {
        _layer.InvalidateVisual();
        _hoverLayer.InvalidateVisual();
    }

    private sealed class DrawingLayer(Action<DrawingContext> draw) : Control
    {
        public override void Render(DrawingContext context) => draw(context);
    }
}
