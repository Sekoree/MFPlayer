using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace HaCue2.Controls;

/// <summary>
/// A progress bar you can drag the playhead on.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="ProgressBar"/> with a pointer contract, rather than a <see cref="Slider"/>: a slider
/// carries a thumb, a track, keyboard focus and a two-way <c>Value</c> that fights a four-times-a-second
/// poll — every tick would either snap the thumb out from under the operator's finger or, worse, be
/// interpreted as a seek back to where the show already was.
/// </para>
/// <para>
/// So the bar is READ-ONLY until it is touched. While a drag is in progress it shows the dragged
/// position and ignores incoming ticks; on release it raises <see cref="Seeked"/> exactly once with the
/// fraction, and goes back to following the show. That is the difference between scrubbing and a bar
/// that fights you.
/// </para>
/// </remarks>
public class SeekBar : ProgressBar
{
    /// <summary>Raised once per gesture, with the fraction of the whole the operator landed on.</summary>
    public static readonly RoutedEvent<SeekEventArgs> SeekedEvent =
        RoutedEvent.Register<SeekBar, SeekEventArgs>(nameof(Seeked), RoutingStrategies.Bubble);

    public event EventHandler<SeekEventArgs>? Seeked
    {
        add => AddHandler(SeekedEvent, value);
        remove => RemoveHandler(SeekedEvent, value);
    }

    /// <summary>Whether this bar accepts a seek at all. A clip of unknown length cannot be scrubbed.</summary>
    public static readonly StyledProperty<bool> IsSeekableProperty =
        AvaloniaProperty.Register<SeekBar, bool>(nameof(IsSeekable), true);

    public bool IsSeekable
    {
        get => GetValue(IsSeekableProperty);
        set => SetValue(IsSeekableProperty, value);
    }

    private bool _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!IsSeekable || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        e.Pointer.Capture(this);
        Preview(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_dragging)
            return;

        Preview(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_dragging)
            return;

        _dragging = false;
        e.Pointer.Capture(null);

        var fraction = Fraction(e);
        Value = Minimum + (fraction * (Maximum - Minimum));
        RaiseEvent(new SeekEventArgs(SeekedEvent, fraction));
        e.Handled = true;
    }

    /// <summary>
    /// Shows where the release would land, without asking the show to go there.
    /// </summary>
    /// <remarks>
    /// A seek per pointer-move would ask the engine to re-open and re-buffer the clip on every pixel of
    /// a drag. The bar follows the finger; the show follows on release.
    /// </remarks>
    private void Preview(PointerEventArgs e) =>
        Value = Minimum + (Fraction(e) * (Maximum - Minimum));

    private double Fraction(PointerEventArgs e)
    {
        var width = Bounds.Width;

        return width <= 0 ? 0 : Math.Clamp(e.GetPosition(this).X / width, 0, 1);
    }

    /// <summary>Whether a poll may write to <see cref="ProgressBar.Value"/> right now.</summary>
    /// <remarks>
    /// Read by the binding so a tick landing mid-drag does not snap the bar back to the show's
    /// position — which on a long clip is the whole width of the control away from the finger.
    /// </remarks>
    public bool IsScrubbing => _dragging;
}

/// <param name="Fraction">Where the gesture landed, 0–1 of the whole clip.</param>
public sealed class SeekEventArgs(RoutedEvent routedEvent, double fraction) : RoutedEventArgs(routedEvent)
{
    public double Fraction { get; } = fraction;
}
