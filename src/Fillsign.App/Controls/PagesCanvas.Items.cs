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
            InvalidateLayers();
        };
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(EditorViewModel.Tool) or nameof(EditorViewModel.PlacingSignature))
            {
                UpdatePointerCursor(null);
            }
        };
        AttachFields(editor);
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

        }

        DrawOverlays(context, page, rect);
    }

    /// <summary>The possible gap being checked (E10): an amber outline with a label beside it.</summary>
    private void DrawCheckingGap(DrawingContext context, Rect pageRect, Fillsign.Core.Model.PageRect area, string badge)
    {
        var amber = Color.FromRgb(0xc2, 0x41, 0x0c);
        var outline = new Rect(pageRect.X + (area.X * Zoom), pageRect.Y + (area.Y * Zoom), area.Width * Zoom, area.Height * Zoom).Inflate(6);
        context.DrawRectangle(new SolidColorBrush(amber, 0.06), new Pen(new SolidColorBrush(amber), 2), outline, 6, 6);

        var text = new FormattedText(badge, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 12, Brushes.White);
        // Above the outline: beside it would cover the words that go with a box.
        double width = text.Width + 16, height = text.Height + 4;
        var pill = new Rect(Math.Min(outline.X, pageRect.Right - width - 4), outline.Y - height - 4, width, height);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0e)), null, pill, height / 2, height / 2);
        context.DrawText(text, new Point(pill.X + 8, pill.Y + 2));
    }

    private static readonly DashStyle s_signHereDash = new([4, 3], 0);
    private static readonly Typeface s_signHereFace = new("Inter");

    /// <summary>Selection and focus on the page use the light-theme blue, even in dark mode (INTERACTION-PATTERNS §4).</summary>
    private void DrawOverlays(DrawingContext context, PageViewModel page, Rect rect)
    {
        EditorViewModel editor = _editor!;
        IBrush blue = Brush("SelectionOnPage", Brushes.RoyalBlue);
        Color blueColour = blue is ISolidColorBrush solid ? solid.Color : Colors.RoyalBlue;
        var tint = new SolidColorBrush(blueColour, 0.06);

        if (editor.CheckingGap is { } gap && gap.Page == page.Id)
        {
            DrawCheckingGap(context, rect, gap.Area, editor.CheckingBadge);
        }

        // UX review 4: an empty signature field is easy to miss, so it's outlined with "Sign here" while editing.
        foreach (FieldHit unsigned in editor.UnsignedSignatureFields(page.Id))
        {
            var box = new Rect(rect.X + (unsigned.Box.X * Zoom), rect.Y + (unsigned.Box.Y * Zoom), unsigned.Box.Width * Zoom, unsigned.Box.Height * Zoom);
            context.DrawRectangle(tint, new Pen(blue, 1.5, s_signHereDash), box, 3, 3);
            var signHere = new FormattedText("Sign here", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, s_signHereFace, Math.Clamp(box.Height * 0.4, 11, 14), blue);
            if (signHere.Width + 16 <= box.Width && signHere.Height <= box.Height)
            {
                context.DrawText(signHere, new Point(box.X + 8, box.Y + ((box.Height - signHere.Height) / 2)));
            }
        }

        // FS-950: the field in use, ringed like a focused control: the one Tab reached, or the text field being typed in.
        if ((editor.FocusedField ?? editor.EditingField?.Hit) is { } ringed && ringed.Page == page.Id)
        {
            Fillsign.Core.Model.PageRect ringBox = ringed.Box;
            var ring = new Rect(rect.X + (ringBox.X * Zoom), rect.Y + (ringBox.Y * Zoom), ringBox.Width * Zoom, ringBox.Height * Zoom).Inflate(3);
            context.DrawRectangle(null, new Pen(Brush("FocusRing", blue), 2), ring, 3, 3);
        }

        if (editor.FocusedField is { } focused && focused.Page == page.Id)
        {
            Fillsign.Core.Model.PageRect box = focused.Box;

            // In a multi-select list, the row the arrow keys are on.
            if (focused.Field is { IsMultiSelect: true, IsList: true } list && (editor.FocusedOption + 1) * list.ListRowHeight <= box.Height)
            {
                double top = box.Y + 1 + (editor.FocusedOption * list.ListRowHeight);
                var row = new Rect(rect.X + ((box.X + 1) * Zoom), rect.Y + (top * Zoom), (box.Width - 2) * Zoom, list.ListRowHeight * Zoom);
                context.DrawRectangle(null, new Pen(Brush("FocusRing", blue), 1, new DashStyle([2, 2], 0)), row);
            }
        }

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
            foreach ((_, Point corner) in Handles(single, rect))
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

    }

    /// <summary>
    /// What follows the pointer: the tick or cross about to be placed, the signature being placed and the keyboard
    /// cursor. It has a layer of its own, so moving the mouse repaints only this, not every page and item under it.
    /// </summary>
    private void DrawHover(DrawingContext context)
    {
        if (_editor is not { Cursor: { } cursor } editor || editor.State.IndexOf(cursor.Page) is not (var index and >= 0) || index >= _tops.Length)
        {
            return;
        }

        Rect rect = PageRect(index);
        double zoom = Zoom;
        IBrush blue = Brush("SelectionOnPage", Brushes.RoyalBlue);
        using (context.PushClip(rect))
        {
            using (context.PushTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(rect.X, rect.Y)))
            {
                if (editor.PlacingSignature is { } placing)
                {
                    _painter.Draw(context, editor.SignatureAt(cursor.Page, cursor.Point, placing.Image, placing.Kind), opacity: 0.55);
                }

                // Tick and Cross: a faint preview of the mark, exactly where a click would place it.
                if (editor.MarkPreview is { } mark)
                {
                    _painter.Draw(context, mark, opacity: 0.45);
                }
            }

            if (editor.PlacingSignature is { } ghost)
            {
                // E4: the signature follows the pointer inside a dashed box until it's placed.
                SignatureItem preview = editor.SignatureAt(cursor.Page, cursor.Point, ghost.Image, ghost.Kind);
                context.DrawRectangle(null, new Pen(blue, 1.5, new DashStyle([4, 3], 0)), ViewBox(preview, rect).Inflate(OutlineGap), 3, 3);
            }

            if (editor.IsKeyboardCursor)
            {
                // Where Enter will place, when placing with the keyboard (NFR-4).
                Point centre = rect.TopLeft + new Point(cursor.Point.X * zoom, cursor.Point.Y * zoom);
                var pen = new Pen(Brush("Text", Brushes.Black), 1.5);
                context.DrawLine(pen, centre - new Point(11, 0), centre + new Point(11, 0));
                context.DrawLine(pen, centre - new Point(0, 11), centre + new Point(0, 11));
                context.DrawEllipse(null, new Pen(blue, 1.5), centre, 6, 6);
            }
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

    /// <summary>
    /// Resize handles: one at each corner, just outside the item. A line or arrow has one on each end instead, so an
    /// end can be swung to any angle (FR-5.4).
    /// </summary>
    private (Corner Corner, Point Centre)[] Handles(Item item, Rect pageRect)
    {
        if (item is ShapeItem { IsLine: true } line)
        {
            Rect ends = ViewBox(line, pageRect);
            (Corner start, Corner end) = ItemGeometry.LineEndCorners(line);
            return [(start, CornerOf(ends, start)), (end, CornerOf(ends, end))];
        }

        Rect box = ViewBox(item, pageRect).Inflate(OutlineGap);
        return [.. s_corners.Select(corner => (corner, CornerOf(box, corner)))];
    }

    private static Point CornerOf(Rect box, Corner corner) => corner switch
    {
        Corner.TopLeft => box.TopLeft,
        Corner.TopRight => box.TopRight,
        Corner.BottomLeft => box.BottomLeft,
        _ => box.BottomRight,
    };

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

        if (_fieldBox is { IsVisible: true })
        {
            if (_fieldBox.Bounds.Contains(position))
            {
                return;
            }

            // A click away from a form field's text box finishes it, and goes on to do what it would anyway.
            CommitFieldBox();
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

        foreach ((Corner corner, Point centre) in Handles(item, PageRect(index)))
        {
            if (Math.Abs(position.X - centre.X) <= HandleReach && Math.Abs(position.Y - centre.Y) <= HandleReach)
            {
                return corner;
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

        // FS-950: over one of the PDF's own fields, with nothing of Fillsign's on top, a click fills it.
        if (type is StandardCursorType.Arrow or StandardCursorType.Ibeam && position is { } at && PageAt(at) is var fieldPage and >= 0
            && ItemHitTest.TopmostAt(_editor.State, _editor.Pages[fieldPage].Id, ToPage(fieldPage, at), GrabDistance / Zoom, _editor.Measurer) is null
            && IsOverField(fieldPage, at))
        {
            type = StandardCursorType.Hand;
        }
        string? why = null;
        if (type is not StandardCursorType.Hand && type is StandardCursorType.Arrow or StandardCursorType.Ibeam && position is { } over && PageAt(over) is var readOnlyPage and >= 0
            && ItemHitTest.TopmostAt(_editor.State, _editor.Pages[readOnlyPage].Id, ToPage(readOnlyPage, over), GrabDistance / Zoom, _editor.Measurer) is null)
        {
            // A field the form fills in itself (a total), or a form button: the pointer says why before a click does.
            why = _editor.WhyNotFillable(_editor.Pages[readOnlyPage].Id, ToPage(readOnlyPage, over));
            if (why is not null)
            {
                type = StandardCursorType.No;
            }
        }

        if (!Equals(ToolTip.GetTip(this), why))
        {
            ToolTip.SetTip(this, why);
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
                MaxLength = TextItem.MaxLength,
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
        _typingBox.FontWeight = text.IsBold ? FontWeight.Bold : FontWeight.Normal;
        _typingBox.TextAlignment = text.Align switch
        {
            TextAlign.Centre => TextAlignment.Center,
            TextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
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
