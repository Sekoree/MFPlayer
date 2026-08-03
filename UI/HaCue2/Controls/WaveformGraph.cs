using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace HaCue2.Controls;

/// <summary>Which handle a drag on the waveform grabbed.</summary>
public enum TrimHandle
{
    /// <summary>Neither — the pointer landed in open water and is scrubbing.</summary>
    Playhead,
    In,
    Out,
}

/// <summary>A drag on the waveform, in fractions of the file.</summary>
public sealed record TrimGesture(TrimHandle Handle, double At);

/// <summary>
/// A file's peaks, its trim window, and the playhead — the whole picture a trim is made against.
/// </summary>
/// <remarks>
/// <para>
/// Peaks arrive as normalized floats and are drawn as a column per PIXEL rather than one per bucket:
/// a four-thousand-bucket scan on a six-hundred-pixel control has to fold, and folding by MAXIMUM is
/// what keeps a transient visible. Averaging would erase the very thing somebody is looking for.
/// </para>
/// <para>
/// Everything outside the trim window is dimmed rather than hidden. What was cut is exactly what an
/// operator needs to see to know they cut in the right place, and a waveform that only showed the kept
/// part would give them no way to tell.
/// </para>
/// </remarks>
public class WaveformGraph : Control
{
    /// <summary>How close to a handle counts as grabbing it, in pixels.</summary>
    private const double GrabRadius = 7;

    public static readonly StyledProperty<IReadOnlyList<float>?> PeaksProperty =
        AvaloniaProperty.Register<WaveformGraph, IReadOnlyList<float>?>(nameof(Peaks));

    /// <summary>The trim window, as fractions of the file.</summary>
    public static readonly StyledProperty<double> TrimInProperty =
        AvaloniaProperty.Register<WaveformGraph, double>(nameof(TrimIn));

    public static readonly StyledProperty<double> TrimOutProperty =
        AvaloniaProperty.Register<WaveformGraph, double>(nameof(TrimOut), 1d);

    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<WaveformGraph, double>(nameof(Playhead));

    public static readonly StyledProperty<IBrush?> WaveBrushProperty =
        AvaloniaProperty.Register<WaveformGraph, IBrush?>(nameof(WaveBrush));

    public static readonly StyledProperty<IBrush?> TrimmedBrushProperty =
        AvaloniaProperty.Register<WaveformGraph, IBrush?>(nameof(TrimmedBrush));

    public static readonly StyledProperty<IBrush?> HandleBrushProperty =
        AvaloniaProperty.Register<WaveformGraph, IBrush?>(nameof(HandleBrush));

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<WaveformGraph, IBrush?>(nameof(PlayheadBrush));

    static WaveformGraph() =>
        AffectsRender<WaveformGraph>(
            PeaksProperty, TrimInProperty, TrimOutProperty, PlayheadProperty,
            WaveBrushProperty, TrimmedBrushProperty, HandleBrushProperty, PlayheadBrushProperty);

    public IReadOnlyList<float>? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double TrimIn
    {
        get => GetValue(TrimInProperty);
        set => SetValue(TrimInProperty, value);
    }

    public double TrimOut
    {
        get => GetValue(TrimOutProperty);
        set => SetValue(TrimOutProperty, value);
    }

    public double Playhead
    {
        get => GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    public IBrush? WaveBrush
    {
        get => GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    public IBrush? TrimmedBrush
    {
        get => GetValue(TrimmedBrushProperty);
        set => SetValue(TrimmedBrushProperty, value);
    }

    public IBrush? HandleBrush
    {
        get => GetValue(HandleBrushProperty);
        set => SetValue(HandleBrushProperty, value);
    }

    public IBrush? PlayheadBrush
    {
        get => GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    /// <summary>Raised while a handle or the playhead is being dragged.</summary>
    public event EventHandler<TrimGesture>? Gesture;

    /// <summary>Raised when the drag ends, so its undo step can be closed.</summary>
    public event EventHandler? GestureCompleted;

    private TrimHandle? _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Bounds.Width <= 0)
            return;

        var x = e.GetPosition(this).X;

        // The nearest handle within reach, or the playhead. Nearest rather than first, so a very short
        // trim window whose two handles overlap still gives the operator the one they aimed at.
        var toIn = Math.Abs(x - (TrimIn * Bounds.Width));
        var toOut = Math.Abs(x - (TrimOut * Bounds.Width));

        _dragging = Math.Min(toIn, toOut) > GrabRadius
            ? TrimHandle.Playhead
            : toIn <= toOut ? TrimHandle.In : TrimHandle.Out;

        e.Pointer.Capture(this);
        Raise(x);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragging is not null)
            Raise(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragging is null)
            return;

        _dragging = null;
        e.Pointer.Capture(null);
        GestureCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void Raise(double x)
    {
        if (_dragging is { } handle && Bounds.Width > 0)
            Gesture?.Invoke(this, new TrimGesture(handle, Math.Clamp(x / Bounds.Width, 0, 1)));
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;

        if (width <= 0 || height <= 0)
            return;

        var middle = height / 2;
        var kept = WaveBrush ?? Brushes.White;
        var cut = TrimmedBrush ?? Brushes.Gray;

        if (Peaks is { Count: > 0 } peaks)
        {
            var columns = (int)Math.Ceiling(width);

            for (var column = 0; column < columns; column++)
            {
                // The MAXIMUM of every bucket this column covers. Averaging four thousand buckets into
                // six hundred columns would flatten exactly the transients somebody is looking for.
                var from = (int)((double)column / columns * peaks.Count);
                var to = Math.Max(from + 1, (int)((double)(column + 1) / columns * peaks.Count));
                var peak = 0f;

                for (var index = from; index < to && index < peaks.Count; index++)
                {
                    if (peaks[index] > peak)
                        peak = peaks[index];
                }

                if (peak <= 0)
                    continue;

                var fraction = (column + 0.5) / columns;
                var brush = fraction >= TrimIn && fraction <= TrimOut ? kept : cut;

                // Symmetrical about the centre, which is what a peak reading looks like and what makes
                // a quiet passage read as quiet rather than as an absence.
                var half = Math.Max(0.5, peak * (middle - 1));
                context.FillRectangle(brush, new Rect(column, middle - half, 1, half * 2));
            }
        }

        var handles = HandleBrush ?? Brushes.Orange;

        // The cut regions get a wash as well as dimmed bars: a file whose trimmed part is silent has no
        // bars to dim, and the window has to be visible on one of those too.
        if (TrimIn > 0)
            context.FillRectangle(Dim(cut), new Rect(0, 0, TrimIn * width, height));

        if (TrimOut < 1)
            context.FillRectangle(Dim(cut), new Rect(TrimOut * width, 0, (1 - TrimOut) * width, height));

        context.FillRectangle(handles, new Rect((TrimIn * width) - 1, 0, 2, height));
        context.FillRectangle(handles, new Rect((TrimOut * width) - 1, 0, 2, height));

        context.FillRectangle(
            PlayheadBrush ?? Brushes.White, new Rect((Playhead * width) - 0.5, 0, 1, height));
    }

    /// <summary>The trimmed wash: the cut colour at low opacity, so what is under it still reads.</summary>
    private static IBrush Dim(IBrush brush) =>
        brush is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, 0.18)
            : new SolidColorBrush(Colors.Black, 0.35);
}
