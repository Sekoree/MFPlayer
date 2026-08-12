using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HaCue2.ViewModels;

namespace HaCue2.Controls;

/// <summary>Draws cubic tangent arms and their grab handles. Hit testing remains in CurveCanvas so a
/// handle drag and a keyframe drag share pointer capture and one undo boundary.</summary>
public sealed class TangentGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<CurveTangent>?> TangentsProperty =
        AvaloniaProperty.Register<TangentGraph, IReadOnlyList<CurveTangent>?>(nameof(Tangents));
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TangentGraph, IBrush?>(nameof(Stroke));

    static TangentGraph() => AffectsRender<TangentGraph>(TangentsProperty, StrokeProperty);

    public IReadOnlyList<CurveTangent>? Tangents
    {
        get => GetValue(TangentsProperty);
        set => SetValue(TangentsProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Tangents is not { Count: > 0 } tangents || Stroke is not { } stroke
            || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var pen = new Pen(stroke, 1);
        foreach (var tangent in tangents)
        {
            var anchor = new Point(tangent.AnchorX * Bounds.Width, tangent.AnchorY * Bounds.Height);
            var handle = new Point(tangent.X * Bounds.Width, tangent.Y * Bounds.Height);
            context.DrawLine(pen, anchor, handle);
            context.DrawEllipse(stroke, new Pen(stroke, 1), handle, 3.5, 3.5);
        }
    }
}
