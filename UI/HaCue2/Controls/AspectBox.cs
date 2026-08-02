using Avalonia;
using Avalonia.Controls;

namespace HaCue2.Controls;

/// <summary>
/// Gives its child the aspect ratio of the composition it represents, filling the width it is given.
/// </summary>
/// <remarks>
/// Every canvas in the Video view stands for a real frame — 1920 × 1080, 1280 × 720 — and a canvas
/// stretched to whatever height the panel happens to have is actively misleading: a placement drawn on
/// a 3:1 canvas does not look like what will hit the wall. Avalonia has no declarative
/// height-follows-width, and <see cref="Viewbox"/> solves a different problem (it scales content,
/// including stroke widths and text).
/// </remarks>
public class AspectBox : Decorator
{
    /// <summary>Width ÷ height. Defaults to 16:9, the common case for a composition.</summary>
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectBox, double>(nameof(Ratio), 16d / 9d);

    static AspectBox()
    {
        AffectsMeasure<AspectBox>(RatioProperty);
    }

    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var ratio = Ratio > 0 ? Ratio : 16d / 9d;

        // Prefer fitting the available width; fall back to the height when the width is unconstrained
        // (inside a horizontal StackPanel, say), and to the child's own desire when neither is given.
        var size = availableSize;
        if (!double.IsInfinity(size.Width))
            size = new Size(size.Width, size.Width / ratio);
        else if (!double.IsInfinity(size.Height))
            size = new Size(size.Height * ratio, size.Height);
        else
            size = new Size(0, 0);

        // A tall, narrow slot would otherwise produce a box taller than the space it was offered.
        if (!double.IsInfinity(availableSize.Height) && size.Height > availableSize.Height)
            size = new Size(availableSize.Height * ratio, availableSize.Height);

        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
