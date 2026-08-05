using Avalonia;
using Avalonia.Controls;

namespace HaCue2.Controls;

/// <summary>
/// Positions a child at fractions (0..1) of its <see cref="FractionPanel"/>'s size, the way the mockup
/// positions everything drawn against a timescale or a canvas: <c>style="left:11%;width:37%"</c>.
/// </summary>
/// <remarks>
/// The attached properties live on this static class rather than on the panel because
/// <c>Layoutable</c> already owns <c>Width</c> and <c>Height</c>: declaring them on the panel would
/// hide the real ones, and <c>FractionPanel.Width</c> would then mean two different things depending
/// on where it was written. <c>Fraction.Width</c> cannot be confused with either.
/// </remarks>
public static class Fraction
{
    /// <summary>Left edge as a fraction of the panel width.</summary>
    public static readonly AttachedProperty<double> LeftProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Left", typeof(Fraction));

    /// <summary>Top edge as a fraction of the panel height.</summary>
    public static readonly AttachedProperty<double> TopProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Top", typeof(Fraction));

    /// <summary>Width as a fraction of the panel width; NaN measures the child instead.</summary>
    public static readonly AttachedProperty<double> WidthProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Width", typeof(Fraction), double.NaN);

    /// <summary>Height as a fraction of the panel height; NaN measures the child instead.</summary>
    public static readonly AttachedProperty<double> HeightProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Height", typeof(Fraction), double.NaN);

    public static double GetLeft(Control control) => control.GetValue(LeftProperty);
    public static void SetLeft(Control control, double value) => control.SetValue(LeftProperty, value);
    public static double GetTop(Control control) => control.GetValue(TopProperty);
    public static void SetTop(Control control, double value) => control.SetValue(TopProperty, value);
    public static double GetWidth(Control control) => control.GetValue(WidthProperty);
    public static void SetWidth(Control control, double value) => control.SetValue(WidthProperty, value);
    public static double GetHeight(Control control) => control.GetValue(HeightProperty);
    public static void SetHeight(Control control, double value) => control.SetValue(HeightProperty, value);
}

/// <summary>
/// Lays its children out at the fractions set through <see cref="Fraction"/>.
/// </summary>
/// <remarks>
/// Four surfaces need this and none can use <see cref="Canvas"/>: timeline clips and effect envelopes
/// (fractions of the visible time range), layer placements on a composition canvas, mapping sections on
/// the source and output canvases, and the transport playhead. Canvas positions in absolute pixels, so
/// every one of those would have to recompute on resize; expressing the position in the unit the data
/// is actually in — a fraction of the span — makes resize free and keeps pixels out of the view-models.
/// </remarks>
public class FractionPanel : Panel
{
    static FractionPanel()
    {
        AffectsParentArrange<FractionPanel>(
            Fraction.LeftProperty, Fraction.TopProperty,
            Fraction.WidthProperty, Fraction.HeightProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(Size.Infinity);

        // The panel claims no size of its own: it is always stretched by its container (a lane, a
        // canvas frame). Returning the children's extent instead would make a clip at 90 % demand a
        // panel ten times its own width.
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            var fw = Fraction.GetWidth(child);
            var fh = Fraction.GetHeight(child);

            var w = double.IsNaN(fw) ? child.DesiredSize.Width : fw * finalSize.Width;
            var h = double.IsNaN(fh) ? child.DesiredSize.Height : fh * finalSize.Height;

            child.Arrange(new Rect(
                Fraction.GetLeft(child) * finalSize.Width,
                Fraction.GetTop(child) * finalSize.Height,
                double.IsNaN(w) ? 0 : w,
                double.IsNaN(h) ? 0 : h));
        }

        return finalSize;
    }
}

/// <summary>
/// Keeps a normalized placement surface in the middle of a larger, equally-scaled work area.
/// </summary>
/// <remarks>
/// The inset is a FRACTION on both axes, so the child keeps the aspect ratio supplied by its
/// surrounding <see cref="AspectBox"/>. The spare area is where an off-composition placement remains
/// visible and grabbable; a fixed pixel margin would quietly change the canvas aspect ratio.
/// </remarks>
public class PlacementWorkArea : Decorator
{
    private const double MarginFraction = 0.13;

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<PlacementWorkArea, bool>(nameof(IsExpanded));

    static PlacementWorkArea() => AffectsArrange<PlacementWorkArea>(IsExpandedProperty);

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var childSize = IsExpanded
            ? new Size(
                Scale(availableSize.Width, 1 - (MarginFraction * 2)),
                Scale(availableSize.Height, 1 - (MarginFraction * 2)))
            : availableSize;

        Child?.Measure(childSize);

        return new Size(
            double.IsInfinity(availableSize.Width) ? Child?.DesiredSize.Width ?? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? Child?.DesiredSize.Height ?? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!IsExpanded)
        {
            Child?.Arrange(new Rect(finalSize));
            return finalSize;
        }

        var x = finalSize.Width * MarginFraction;
        var y = finalSize.Height * MarginFraction;
        Child?.Arrange(new Rect(
            x,
            y,
            Math.Max(0, finalSize.Width - (x * 2)),
            Math.Max(0, finalSize.Height - (y * 2))));
        return finalSize;
    }

    private static double Scale(double value, double factor) =>
        double.IsInfinity(value) ? value : Math.Max(0, value * factor);
}
