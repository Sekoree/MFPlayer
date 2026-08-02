using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HaCue2.Controls;

/// <summary>
/// The mockup's <c>.mtr .bar</c>: a narrow vertical level bar with a peak-hold tick and a clip latch.
/// </summary>
/// <remarks>
/// Drawn rather than templated because the two things it shows — a fill whose height is a fraction of
/// the control and a 1 px rule at another fraction — are exactly what a template cannot express without
/// a converter per binding.
/// <para>
/// It renders whatever it is given and invents nothing. A meter with no telemetry behind it must read
/// as silence, not as a plausible-looking signal: in the first extraction attempt the output strip
/// deliberately never faked levels for the same reason. In this shell the values come from sample data,
/// which is why <see cref="Level"/> and <see cref="Peak"/> are plain properties with no animation,
/// decay or ballistics — those belong to the real meter, driven by <c>ProgramBusMeter</c>.
/// </para>
/// </remarks>
public class LevelMeter : Control
{
    /// <summary>Current level, 0..1 of the bar height.</summary>
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));

    /// <summary>Peak-hold position, 0..1; not drawn when zero.</summary>
    public static readonly StyledProperty<double> PeakProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Peak));

    /// <summary>Sticky clip latch — reddens the border, matching <c>.mtr.clip</c>.</summary>
    public static readonly StyledProperty<bool> IsClippingProperty =
        AvaloniaProperty.Register<LevelMeter, bool>(nameof(IsClipping));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<IBrush?> PeakBrushProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(PeakBrush));

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(
            LevelProperty, PeakProperty, IsClippingProperty,
            FillProperty, TrackBrushProperty, BorderBrushProperty, PeakBrushProperty);
    }

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public double Peak
    {
        get => GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public bool IsClipping
    {
        get => GetValue(IsClippingProperty);
        set => SetValue(IsClippingProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public IBrush? PeakBrush
    {
        get => GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        context.FillRectangle(TrackBrush ?? Brushes.Transparent, new Rect(0, 0, w, h));

        var level = Math.Clamp(Level, 0, 1);
        if (level > 0 && Fill is { } fill)
        {
            // The gradient is defined over the WHOLE bar (green → amber → red at the ceiling), so the
            // fill has to be clipped out of a full-height rectangle rather than painted into a short
            // one — otherwise a quiet signal would be drawn with the red end of the gradient inside it.
            using (context.PushClip(new Rect(0, h - (h * level), w, h * level)))
                context.FillRectangle(fill, new Rect(0, 0, w, h));
        }

        var peak = Math.Clamp(Peak, 0, 1);
        if (peak > 0 && PeakBrush is { } peakBrush)
            context.FillRectangle(peakBrush, new Rect(0, Math.Max(0, h - (h * peak) - 1), w, 1));

        if (BorderBrush is { } border)
        {
            var pen = new Pen(border);
            context.DrawRectangle(null, pen, new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)));
        }
    }
}
