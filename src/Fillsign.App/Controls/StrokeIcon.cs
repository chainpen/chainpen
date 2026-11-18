using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Fillsign.App.Controls;

/// <summary>
/// A line icon from the design system: a path in a 24-unit box, stroked in the current text colour with round caps,
/// scaled to <see cref="Size"/> (18 px in toolbars and buttons). Purely decorative: the button it sits in carries the
/// accessible name.
/// </summary>
public sealed class StrokeIcon : Avalonia.Controls.Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<StrokeIcon, double>(nameof(Size), 18);

    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeThickness), 1.75);

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    private const double DesignSize = 24;

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, StrokeThicknessProperty, ForegroundProperty);
        AffectsMeasure<StrokeIcon>(SizeProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null)
        {
            return;
        }

        double scale = Size / DesignSize;
        var pen = new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, pen, Data);
        }
    }
}
