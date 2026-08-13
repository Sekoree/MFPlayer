using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.ViewModels;

namespace HaCue2.Controls;

/// <summary>Which part of a clip a drag has hold of.</summary>
public enum ClipEdge
{
    /// <summary>The body: the clip moves, its length unchanged.</summary>
    Body,

    /// <summary>The left edge: the clip starts later and its content starts further in.</summary>
    Start,

    /// <summary>The right edge: the clip ends earlier or later, its start unchanged.</summary>
    End,
}

/// <summary>One clip drag, in fractions of the lane.</summary>
/// <param name="SubjectId">The cue the clip draws.</param>
/// <param name="Edge">What was grabbed.</param>
/// <param name="Left">Where the clip's left edge should end up.</param>
/// <param name="Width">How wide it should be.</param>
public sealed record ClipGesture(int Index, Guid SubjectId, ClipEdge Edge, double Left, double Width);

/// <summary>
/// Draws one timeline lane's clips and turns drags on them into position and trim edits.
/// </summary>
/// <remarks>
/// Three gestures, matching every editor an operator has used before: drag the body to move, drag the
/// right edge to change where it ends, drag the left edge to change where it starts. The left edge is
/// the one worth stating - it moves the clip AND trims the same amount into the file, so the media
/// under the cursor stays where it is instead of sliding.
/// </remarks>
public partial class ClipLane : UserControl
{
    /// <summary>How close to an edge counts as grabbing it rather than the body.</summary>
    private const double EdgeGrab = 6;

    /// <summary>Below this the clip is all edge, so the body would be unreachable.</summary>
    private const double MinimumBodyWidth = 3 * EdgeGrab;

    private Panel? _surface;
    private int _draggedIndex = -1;
    private ClipEdge _edge;
    private Point _grabbedAt;
    private double _grabbedLeft;
    private double _grabbedWidth;

    public static readonly StyledProperty<IReadOnlyList<TimelineClip>> ClipsProperty =
        AvaloniaProperty.Register<ClipLane, IReadOnlyList<TimelineClip>>(nameof(Clips), []);

    /// <summary>Whether drags edit. Off for an effect lane, whose points are edited elsewhere.</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<ClipLane, bool>(nameof(IsEditable), true);

    public ClipLane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Raised continuously during a drag.</summary>
    public event EventHandler<ClipGesture>? Gesture;

    /// <summary>Raised when a drag ends, so the view-model can close its undo step.</summary>
    public event EventHandler? GestureCompleted;

    public IReadOnlyList<TimelineClip> Clips
    {
        get => GetValue(ClipsProperty);
        set => SetValue(ClipsProperty, value);
    }

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    private Panel? Surface =>
        _surface ??= this.GetVisualDescendants().OfType<FractionPanel>().FirstOrDefault();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!IsEditable || Surface is not { Bounds.Width: > 0 } surface)
            return;

        var position = e.GetPosition(surface);
        var index = ClipAt(position, surface.Bounds.Width);
        if (index < 0)
            return;

        _draggedIndex = index;
        _grabbedAt = position;
        _grabbedLeft = Clips[index].Left;
        _grabbedWidth = Clips[index].Width;
        _edge = EdgeAt(position.X, _grabbedLeft, _grabbedWidth, surface.Bounds.Width);

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_draggedIndex < 0 || Surface is not { Bounds.Width: > 0 } surface)
            return;

        // Recomputed from the grab origin every time, never accumulated - the same reason as on
        // PlacementCanvas: a clamped increment loses whatever the clamp ate, and the clip stops
        // following the pointer once it has reached zero or the end of the lane.
        var delta = (e.GetPosition(surface).X - _grabbedAt.X) / surface.Bounds.Width;

        var (left, width) = _edge switch
        {
            ClipEdge.Start => (_grabbedLeft + delta, _grabbedWidth - delta),
            ClipEdge.End => (_grabbedLeft, _grabbedWidth + delta),
            _ => (_grabbedLeft + delta, _grabbedWidth),
        };

        Gesture?.Invoke(this, new ClipGesture(
            _draggedIndex, Clips[_draggedIndex].SubjectId, _edge, left, width));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_draggedIndex < 0)
            return;

        _draggedIndex = -1;
        _edge = ClipEdge.Body;
        e.Pointer.Capture(null);
        GestureCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A capture that ends any other way - the window deactivated, the control was torn out of the
    /// tree - must still complete the gesture: the view-model's journal composite stays open until
    /// <see cref="GestureCompleted"/>, and an unclosed one folds every later edit into the drag's
    /// undo step.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (_draggedIndex < 0)
            return;

        _draggedIndex = -1;
        _edge = ClipEdge.Body;
        GestureCompleted?.Invoke(this, EventArgs.Empty);
    }

    private int ClipAt(Point position, double laneWidth)
    {
        for (var index = Clips.Count - 1; index >= 0; index--)
        {
            var left = Clips[index].Left * laneWidth;
            var right = left + (Clips[index].Width * laneWidth);

            if (position.X >= left - EdgeGrab && position.X <= right + EdgeGrab)
                return index;
        }

        return -1;
    }

    /// <summary>
    /// Which part of the clip the pointer is over.
    /// </summary>
    /// <remarks>
    /// A clip narrower than three grab widths is treated as all body: two edge zones meeting in the
    /// middle would leave a short clip impossible to move, and moving is the commoner gesture.
    /// </remarks>
    private static ClipEdge EdgeAt(double x, double left, double width, double laneWidth)
    {
        var pixels = width * laneWidth;
        if (pixels < MinimumBodyWidth)
            return ClipEdge.Body;

        var start = left * laneWidth;

        if (Math.Abs(x - start) <= EdgeGrab)
            return ClipEdge.Start;

        return Math.Abs(x - (start + pixels)) <= EdgeGrab ? ClipEdge.End : ClipEdge.Body;
    }
}
