using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Fillsign.App.Rendering;
using Fillsign.App.ViewModels;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.App.Controls;

/// <summary>
/// Phase 3: items on the pages, selection feedback (E3, E5), the in-place text box (E2), the signature being placed
/// (E4), and pointer input passed to the editor in page points.
/// </summary>
public sealed partial class PagesCanvas
{
    /// <summary>Resize handles are 12 px squares (E3); they can be grabbed a little outside the square.</summary>
    private const double HandleSize = 12;
    private const double HandleReach = 9;

    /// <summary>Selection outlines sit this far outside the item, in pixels.</summary>
    private const double OutlineGap = 3;

    /// <summary>How close to a thin line a click still grabs it, in pixels at any zoom.</summary>
    private const double GrabDistance = 4;

    private static readonly DashStyle s_selectionDash = new([3, 2], 0);

    private readonly ItemPainter _painter = new();
    private TextBox? _typingBox;
    private ItemId? _typingBoxItem;
    private double _typingBaselineOffset;

    // The page a drag started on: the drag stays on that page (FR-5.3) even when the pointer leaves it.
    private int _dragPage = -1;

    private void AttachItems(EditorViewModel editor)
    {
        editor.ItemsChanged += () =>
        {
            SyncTypingBox();
            _layer.InvalidateVisual();
        };
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(EditorViewModel.Tool) or nameof(EditorViewModel.PlacingSignature))
            {
                UpdatePointerCursor(null);
            }
        };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += (_, _) => _editor?.PointerExited();
        PointerCaptureLost += (_, _) =>
        {
            if (_dragPage >= 0 && _editor is not null && _editor.IsDrawingGesture)
            {
                _editor.BackOut();
            }

            _dragPage = -1;
        };
        DetachedFromVisualTree += (_, _) => _painter.ReleaseImages();
    }

    // ----- Drawing -----

    private void DrawItemsOn(DrawingContext context, PageViewModel page, Rect rect)
    {
        if (_editor is null)
        {
            return;
        }

        EditorViewModel editor = _editor;
        double zoom = Zoom;
        using (context.PushClip(rect))
        using (context.PushTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(rect.X, rect.Y)))
        {
            foreach (Item item in editor.State.ItemsOn(page.Id))
            {
                // The text box shows the text being typed.
                if (item.Id != editor.TypingItem)
                {
                    _painter.Draw(context, item);
                }
            }

            if (editor.Preview is { } preview && preview.Page == page.Id)
            {
                _painter.Draw(context, preview);
            }

            if (editor.PlacingSignature is { } placing && editor.Cursor is { } cursor && cursor.Page == page.Id)
            {
                _painter.Draw(context, editor.SignatureAt(page.Id, cursor.Point, placing.Image, placing.Kind), opacity: 0.55);
            }
        }

        DrawOverlays(context, page, rect);
    }

    /// <summary>Selection and focus on the page use the light-theme blue, even in dark mode (INTERACTION-PATTERNS §4).</summary>
    private void DrawOverlays(DrawingContext context, PageViewModel page, Rect rect)
    {
        EditorViewModel editor = _editor!;
        IBrush blue = Brush("SelectionOnPage", Brushes.RoyalBlue);
        Color blueColour = blue is ISolidColorBrush solid ? solid.Color : Colors.RoyalBlue;
        var tint = new SolidColorBrush(blueColour, 0.06);

        foreach (Item item in editor.SelectedItems())
        {
            if (item.Page == page.Id)
            {
                Rect box = ViewBox(item, rect).Inflate(OutlineGap);
                context.DrawRectangle(null, new Pen(blue, 1.5, s_selectionDash), box, 3, 3);
            }
        }

        if (SingleSelected() is { } single && single.Page == page.Id)
        {
            var handlePen = new Pen(blue, 2);
            foreach (Point corner in HandleCentres(ViewBox(single, rect).Inflate(OutlineGap)))
            {
                var handle = new Rect(corner.X - (HandleSize / 2), corner.Y - (HandleSize / 2), HandleSize, HandleSize);
                context.DrawRectangle(Brushes.White, handlePen, handle.Deflate(1), 3, 3);
            }
        }

        foreach (Item item in editor.State.ItemsOn(page.Id))
        {
            if (editor.BoxCandidates.Contains(item.Id))
            {
                context.DrawRectangle(tint, new Pen(blue, 1.5), ViewBox(item, rect).Inflate(OutlineGap), 3, 3);
            }
        }

        if (editor.SelectionBox is { } selectionBox && selectionBox.Page == page.Id)
        {
            context.DrawRectangle(new SolidColorBrush(blueColour, 0.08), new Pen(blue, 1), ToView(selectionBox.Box, rect));
        }

        if (editor.TypingItem is { } typing && editor.State.FindItem(typing) is TextItem text && text.Page == page.Id)
        {
            // E2: a solid outline around the text being typed, with room for the caret.
            Rect box = ViewBox(text, rect);
            double minWidth = text.FontSize * Zoom;
            box = box.WithWidth(Math.Max(box.Width, minWidth));
            context.DrawRectangle(tint, new Pen(blue, 1.5), box.Inflate(new Thickness(6, 2)), 4, 4);
        }

        if (editor.PlacingSignature is { } placing && editor.Cursor is { } ghost && ghost.Page == page.Id)
        {
            // E4: the signature follows the pointer inside a dashed box until it's placed.
            SignatureItem preview = editor.SignatureAt(page.Id, ghost.Point, placing.Image, placing.Kind);
            context.DrawRectangle(null, new Pen(blue, 1.5, new DashStyle([4, 3], 0)), ViewBox(preview, rect).Inflate(OutlineGap), 3, 3);
        }

        if (editor.IsKeyboardCursor && editor.Cursor is { } cursor && cursor.Page == page.Id)
        {
            // Where Enter will place, when placing with the keyboard (NFR-4).
            Point centre = rect.TopLeft + new Point(cursor.Point.X * Zoom, cursor.Point.Y * Zoom);
            var pen = new Pen(Brush("Text", Brushes.Black), 1.5);
            context.DrawLine(pen, centre - new Point(11, 0), centre + new Point(11, 0));
            context.DrawLine(pen, centre - new Point(0, 11), centre + new Point(0, 11));
            context.DrawEllipse(null, new Pen(blue, 1.5), centre, 6, 6);
        }
    }

    private Item? SingleSelected()
    {
        if (_editor is null || _editor.TypingItem is not null || _editor.IsDrawingGesture && _editor.SelectionBox is not null)
        {
            return null;
        }

        return _editor.SelectedItems().Take(2).ToList() is [var only] ? only : null;
    }

    private Rect ViewBox(Item item, Rect pageRect) => ToView(ItemGeometry.Bounds(item, _editor!.Measurer), pageRect);

    private Rect ToView(PageRect box, Rect pageRect) =>
        new(pageRect.X + (box.X * Zoom), pageRect.Y + (box.Y * Zoom), box.Width * Zoom, box.Height * Zoom);

    private static Point[] HandleCentres(Rect box) => [box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight];

    private static readonly Corner[] s_corners = [Corner.TopLeft, Corner.TopRight, Corner.BottomLeft, Corner.BottomRight];

    /// <summary>Where a page point is in this control's coordinates (for tests and placing aids).</summary>
    public Point PageToCanvas(int pageIndex, PagePoint point)
    {
        Rect rect = PageRect(pageIndex);
        return new Point(rect.X + (point.X * Zoom), rect.Y + (point.Y * Zoom));
    }

    // ----- Pointer input -----

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_editor is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point position = e.GetPosition(this);
        if (_typingBox is not null && _typingBox.IsVisible && _typingBox.Bounds.Contains(position))
        {
            return; // The text box handles its own clicks.
        }

        _scroller?.Focus();
        e.Handled = true;
        KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool isCommand = e.KeyModifiers.HasFlag(command);

        if (HandleAt(position) is { } corner)
        {
            _dragPage = _editor.State.IndexOf(SingleSelected()!.Page);
            _editor.BeginResize(corner, ToPage(_dragPage, position));
            e.Pointer.Capture(this);
            return;
        }

        int pageIndex = PageAt(position);
        if (pageIndex < 0)
        {
            _editor.FinishTyping();
            if (_editor.Tool == EditorTool.Select && !isShift && !isCommand)
            {
                _editor.BackOut();
            }

            return;
        }

        _dragPage = pageIndex;
        _editor.PointerPressed(pageIndex, ToPage(pageIndex, position), isShift, isCommand, e.ClickCount, GrabDistance / Zoom);
        if (_editor.IsDrawingGesture)
        {
            e.Pointer.Capture(this);
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        Point position = e.GetPosition(this);
        if (_dragPage >= 0 && _editor.IsDrawingGesture)
        {
            _editor.PointerMoved(_dragPage, ToPage(_dragPage, position));
            return;
        }

        int pageIndex = PageAt(position);
        if (pageIndex >= 0)
        {
            _editor.PointerMoved(pageIndex, ToPage(pageIndex, position));
        }
        else
        {
            _editor.PointerExited();
        }

        UpdatePointerCursor(position);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_editor is null || _dragPage < 0)
        {
            return;
        }

        int page = _dragPage;
        _dragPage = -1;
        _editor.PointerReleased(ToPage(page, e.GetPosition(this)));
        e.Pointer.Capture(null);
    }

    private int PageAt(Point position)
    {
        if (_editor is null)
        {
            return -1;
        }

        var (first, last) = VisibleRange(Viewport());
        for (int i = Math.Max(0, first); i <= last && i < _editor.Pages.Count; i++)
        {
            if (PageRect(i).Contains(position))
            {
                return i;
            }
        }

        return -1;
    }

    private PagePoint ToPage(int pageIndex, Point position)
    {
        Rect rect = PageRect(pageIndex);
        return new PagePoint((position.X - rect.X) / Zoom, (position.Y - rect.Y) / Zoom);
    }

    private Corner? HandleAt(Point position)
    {
        if (_editor?.Tool != EditorTool.Select || SingleSelected() is not { } item)
        {
            return null;
        }

        int index = _editor.State.IndexOf(item.Page);
        if (index < 0 || index >= _tops.Length)
        {
            return null;
        }

        Point[] centres = HandleCentres(ViewBox(item, PageRect(index)).Inflate(OutlineGap));
        for (int i = 0; i < centres.Length; i++)
        {
            if (Math.Abs(position.X - centres[i].X) <= HandleReach && Math.Abs(position.Y - centres[i].Y) <= HandleReach)
            {
                return s_corners[i];
            }
        }

        return null;
    }

    private void UpdatePointerCursor(Point? position)
    {
        if (_editor is null)
        {
            return;
        }

        StandardCursorType type = _editor.Tool switch
        {
            EditorTool.Text => StandardCursorType.Ibeam,
            EditorTool.Select => StandardCursorType.Arrow,
            _ => StandardCursorType.Cross,
        };

        if (_editor.Tool == EditorTool.Select && position is { } p)
        {
            if (HandleAt(p) is { } corner)
            {
                type = corner is Corner.TopLeft or Corner.BottomRight ? StandardCursorType.TopLeftCorner : StandardCursorType.TopRightCorner;
            }
            else if (PageAt(p) is var index and >= 0
                && ItemHitTest.TopmostAt(_editor.State, _editor.Pages[index].Id, ToPage(index, p), GrabDistance / Zoom, _editor.Measurer) is not null)
            {
                type = StandardCursorType.SizeAll;
            }
        }

        Cursor = new Cursor(type);
    }

    // ----- The in-place text box (E2) -----

    private void SyncTypingBox()
    {
        if (_editor is null)
        {
            return;
        }

        ItemId? typing = _editor.TypingItem;
        if (typing is null || _editor.State.FindItem(typing.Value) is not TextItem text)
        {
            if (_typingBox is not null && _typingBox.IsVisible)
            {
                _typingBoxItem = null;
                _typingBox.IsVisible = false;
                _scroller?.Focus();
            }

            return;
        }

        if (_typingBox is null)
        {
            _typingBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = ItemPainter.NotoSans,
                FontFeatures = ItemPainter.NoKerning,
                Classes = { "onPage" },
            };
            AutomationProperties.SetName(_typingBox, "Text on the page");
            _typingBox.TextChanged += (_, _) =>
            {
                if (_typingBoxItem is not null)
                {
                    _editor.UpdateTyping(_typingBox.Text ?? string.Empty);
                }
            };
            _typingBox.LostFocus += (_, _) =>
            {
                if (_typingBoxItem is not null && _typingBoxItem == _editor.TypingItem)
                {
                    _editor.FinishTyping();
                }
            };
            Children.Add(_typingBox);
        }

        double fontSize = text.FontSize * Zoom;
        _typingBox.FontSize = fontSize;
        _typingBox.LineHeight = fontSize * TextItem.LineHeight;
        _typingBox.Foreground = new SolidColorBrush(ItemPainter.ToColor(text.Color));
        _typingBox.MinWidth = fontSize;
        _typingBaselineOffset = BaselineOffset(fontSize);

        if (_typingBoxItem != typing)
        {
            _typingBoxItem = null; // Don't report the text we're about to set as typing.
            _typingBox.Text = text.Text;
            _typingBoxItem = typing;
            _typingBox.IsVisible = true;
            _typingBox.CaretIndex = text.Text.Length;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _typingBox.Focus());
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    private void MeasureTypingBox() => _typingBox?.Measure(Size.Infinity);

    private void ArrangeTypingBox()
    {
        if (_editor is null || _typingBox is null || !_typingBox.IsVisible || _editor.TypingItem is not { } id || _editor.State.FindItem(id) is not TextItem text)
        {
            return;
        }

        int index = _editor.State.IndexOf(text.Page);
        if (index < 0 || index >= _tops.Length)
        {
            return;
        }

        Rect page = PageRect(index);
        var origin = new Point(page.X + (text.X * Zoom), page.Y + (text.Y * Zoom) + _typingBaselineOffset);
        _typingBox.Arrange(new Rect(origin, _typingBox.DesiredSize));
    }

    /// <summary>
    /// The page draws each line with its top at y and the baseline at the font's ascent; a text box with a set line
    /// height places its first baseline differently. This shift keeps the typed text exactly where it will be drawn.
    /// </summary>
    private static double BaselineOffset(double fontSize)
    {
        var typeface = new Typeface(ItemPainter.NotoSans);
        using var natural = new TextLayout("Ag", typeface, fontSize, Brushes.Black);
        using var spaced = new TextLayout(
            "Ag",
            typeface,
            fontSize,
            Brushes.Black,
            lineHeight: fontSize * TextItem.LineHeight);
        return natural.TextLines[0].Baseline - spaced.TextLines[0].Baseline;
    }
}
