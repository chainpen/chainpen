using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Chainpen.Core.Model;
using Chainpen.Pdf.Signatures;

namespace Chainpen.App.Controls;

/// <summary>
/// The pad for drawing a signature (FR-4.1, D1): mouse, trackpad, touch or pen, with pen pressure where the device
/// reports it. Draws exactly the segments <see cref="SignatureImageMaker.FromInk"/> renders, so the placed signature
/// matches what was drawn. The Type tab is the non-drag alternative (NFR-4).
/// </summary>
public sealed class SignaturePad : Control
{
    public static readonly StyledProperty<IList<IReadOnlyList<InkSample>>?> StrokesProperty =
        AvaloniaProperty.Register<SignaturePad, IList<IReadOnlyList<InkSample>>?>(nameof(Strokes));

    public static readonly StyledProperty<InkColor> InkProperty =
        AvaloniaProperty.Register<SignaturePad, InkColor>(nameof(Ink), InkColor.Black);

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<SignaturePad, string?>(nameof(Hint));

    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<SignaturePad, bool>(nameof(HasError));

    // The signing line sits this far above the bottom, with a cross at its start (D1).
    private const double LineInset = 24;
    private const double LineFromBottom = 52;

    private List<InkSample>? _current;
    private IPointer? _capturedPointer;

    static SignaturePad()
    {
        AffectsRender<SignaturePad>(InkProperty, HintProperty, HasErrorProperty);
        FocusableProperty.OverrideDefaultValue<SignaturePad>(true);
        CursorProperty.OverrideDefaultValue<SignaturePad>(new Cursor(StandardCursorType.Cross));
    }

    /// <summary>Finished strokes. The pad adds each stroke when the pen lifts; clearing the list clears the pad.</summary>
    public IList<IReadOnlyList<InkSample>>? Strokes
    {
        get => GetValue(StrokesProperty);
        set => SetValue(StrokesProperty, value);
    }

    public InkColor Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    /// <summary>Shown in the middle while the pad is empty.</summary>
    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>Red border when Place was pressed with nothing drawn (FR-4.8).</summary>
    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StrokesProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldList)
            {
                oldList.CollectionChanged -= OnStrokesChanged;
            }

            if (change.NewValue is INotifyCollectionChanged newList)
            {
                newList.CollectionChanged += OnStrokesChanged;
            }

            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed || _current is not null)
        {
            return;
        }

        _current = [Sample(point)];
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_current is null || e.Pointer != _capturedPointer)
        {
            return;
        }

        // Intermediate points keep fast strokes smooth on devices that report more often than frames are drawn.
        foreach (PointerPoint point in e.GetIntermediatePoints(this))
        {
            InkSample sample = Sample(point);
            InkSample last = _current[^1];
            double dx = sample.X - last.X, dy = sample.Y - last.Y;
            if ((dx * dx) + (dy * dy) >= SignatureInk.MinSampleSpacing * SignatureInk.MinSampleSpacing)
            {
                _current.Add(sample);
            }
        }

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer == _capturedPointer)
        {
            FinishStroke();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        FinishStroke();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        // Paper in both appearances: the ink colours are chosen for a light page.
        IBrush surface = Brush("Page", Brushes.White);
        IBrush border = HasError ? Brush("Danger", Brushes.Red) : Brush("BorderControl", Brushes.Gray);
        IBrush muted = new SolidColorBrush(Color.FromRgb(0x4a, 0x50, 0x5a));
        context.DrawRectangle(surface, new Pen(border, HasError ? 2 : 1), bounds.Deflate(HasError ? 1 : 0.5), 10, 10);

        double lineY = bounds.Height - LineFromBottom;
        context.DrawLine(new Pen(Brush("BorderControl", Brushes.Gray), 1), new Point(LineInset, lineY), new Point(bounds.Width - LineInset, lineY));
        var cross = new FormattedText("×", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 20, muted);
        context.DrawText(cross, new Point(LineInset, lineY - cross.Height - 2));

        bool isEmpty = (Strokes is null || Strokes.Count == 0) && _current is null;
        if (isEmpty && !string.IsNullOrEmpty(Hint))
        {
            var hint = new FormattedText(Hint, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 15, muted)
            {
                MaxTextWidth = Math.Max(1, bounds.Width - 48),
                TextAlignment = TextAlignment.Center,
            };
            context.DrawText(hint, new Point(24, (lineY - hint.Height) / 2));
        }

        using (context.PushClip(bounds.Deflate(2)))
        {
            var brush = new SolidColorBrush(Color.FromRgb(Ink.R, Ink.G, Ink.B));
            foreach (IReadOnlyList<InkSample> stroke in Strokes ?? [])
            {
                DrawStroke(context, stroke, brush);
            }

            if (_current is not null)
            {
                DrawStroke(context, _current, brush);
            }
        }
    }

    private static void DrawStroke(DrawingContext context, IReadOnlyList<InkSample> stroke, IBrush brush)
    {
        foreach (InkSegment segment in SignatureInk.Segments(stroke))
        {
            var pen = new Pen(brush, segment.Width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                path.BeginFigure(new Point(segment.Start.X, segment.Start.Y), false);
                path.QuadraticBezierTo(new Point(segment.Control.X, segment.Control.Y), new Point(segment.End.X, segment.End.Y));
                path.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }
    }

    private void FinishStroke()
    {
        if (_current is null)
        {
            return;
        }

        List<InkSample> finished = _current;
        _current = null;
        _capturedPointer = null;
        Strokes?.Add(finished);
        InvalidateVisual();
    }

    private void OnStrokesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    /// <summary>Pens report pressure; mice and trackpads report none, which counts as normal pressure.</summary>
    private static InkSample Sample(PointerPoint point)
    {
        double pressure = point.Pointer.Type == PointerType.Pen ? point.Properties.Pressure : SignatureInk.MousePressure;
        return new InkSample(point.Position.X, point.Position.Y, pressure);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : fallback;
}
