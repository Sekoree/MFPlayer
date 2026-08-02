using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HaCue2.ViewModels;

namespace HaCue2.Controls;

/// <summary>
/// Draws an effect lane's envelope: a polyline over points expressed as fractions of the control.
/// </summary>
/// <remarks>
/// A <see cref="Avalonia.Controls.Shapes.Path"/> with <c>Stretch="Fill"</c> cannot do this. Stretch maps
/// the geometry's BOUNDING BOX to the control, not the lane's coordinate domain, so an envelope that
/// occupies the middle third of a cue would be drawn across the whole lane — and a shallow ride would
/// be stretched to full height. Both are worse than wrong: they are plausible.
/// <para>
/// Drawing from fractions also means the view-model never holds pixels, and the lane rescales for free
/// when the timeline zooms.
/// </para>
/// </remarks>
public class EnvelopeGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<CurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<EnvelopeGraph, IReadOnlyList<CurvePoint>?>(nameof(Points));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<EnvelopeGraph, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<EnvelopeGraph, double>(nameof(StrokeThickness), 1d);

    static EnvelopeGraph()
    {
        AffectsRender<EnvelopeGraph>(PointsProperty, StrokeProperty, StrokeThicknessProperty);
    }

    public IReadOnlyList<CurvePoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Points is not { Count: > 1 } points || Stroke is not { } stroke)
            return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        var pen = new Pen(stroke, StrokeThickness);
        var previous = new Point(points[0].X * w, points[0].Y * h);

        for (var i = 1; i < points.Count; i++)
        {
            var next = new Point(points[i].X * w, points[i].Y * h);
            context.DrawLine(pen, previous, next);
            previous = next;
        }
    }
}
