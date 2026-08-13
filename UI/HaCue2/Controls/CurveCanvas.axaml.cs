using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.ViewModels;

namespace HaCue2.Controls;

/// <summary>What a gesture on the curve canvas asks for.</summary>
public enum CurveGestureKind
{
    /// <summary>Put the point at this position.</summary>
    Move,

    /// <summary>Add a point here.</summary>
    Add,

    /// <summary>Delete this point - a drag that ended off the canvas.</summary>
    Remove,

    /// <summary>Just select it, without changing anything.</summary>
    Select,

    ToggleSelection,
    RangeSelection,
    ClearSelection,
    RemoveSelection,
    MoveIncomingTangent,
    MoveOutgoingTangent,
}

/// <summary>One curve edit, in fractions of the canvas. Y is already flipped to level space.</summary>
public sealed record CurveGesture(
    CurveGestureKind Kind,
    int Index,
    double X,
    double Y,
    bool BypassSnap = false,
    bool ConstrainAxis = false,
    bool IsNudge = false,
    bool Accelerated = false)
{
    /// <summary>Set by the handler when an <see cref="CurveGestureKind.Add"/> actually created a keyframe.
    /// A refused add (one already sits at this time) must NOT leave the canvas dragging: it used to arm its
    /// "resolve the new point on the first move" sentinel unconditionally, and that sentinel then resolved
    /// to whatever was previously SELECTED - so a press on empty canvas near an existing key dragged an
    /// unrelated keyframe to the pointer.</summary>
    public bool Accepted { get; set; }
}

/// <summary>
/// Draws a curve and turns drags on its points into edits.
/// </summary>
/// <remarks>
/// The gestures are the ones the editor's own hint has promised since the shell was a dummy:
/// double-click adds a point, drag moves one, drag off the canvas removes it. Right-click toggles a
/// point's hold, which is the fourth thing a curve can do and had no gesture at all.
/// </remarks>
public partial class CurveCanvas : UserControl
{
    /// <summary>How close to a point counts as grabbing it.</summary>
    private const double PointGrab = 9;

    /// <summary>How far outside the canvas a release has to land to mean "delete".</summary>
    private const double OffCanvas = 14;

    private const double NudgeStep = 0.01;

    private Panel? _surface;
    private int _draggedIndex = -1;
    private CurveTangent? _draggedTangent;
    private IPointer? _capturedPointer;

    public static readonly StyledProperty<IReadOnlyList<CurvePoint>> PointsProperty =
        AvaloniaProperty.Register<CurveCanvas, IReadOnlyList<CurvePoint>>(nameof(Points), []);

    /// <summary>The polyline to draw. Separate from <see cref="Points"/> because a HELD segment needs
    /// a corner the point list does not contain.</summary>
    public static readonly StyledProperty<IReadOnlyList<CurvePoint>> ShapeProperty =
        AvaloniaProperty.Register<CurveCanvas, IReadOnlyList<CurvePoint>>(nameof(Shape), []);

    /// <summary>
    /// A second, read-only curve drawn behind the editable one.
    /// </summary>
    /// <remarks>
    /// What the OTHER side of the same gesture does. A crossfade is one drawing and two ramps - the
    /// outgoing cue rides it forwards, the incoming one reads it from the far end - and an editor that
    /// showed only the half being dragged left the operator to imagine the half they were also authoring.
    /// Empty for every curve that has only one side, which is most of them.
    /// </remarks>
    public static readonly StyledProperty<IReadOnlyList<CurvePoint>> CompanionShapeProperty =
        AvaloniaProperty.Register<CurveCanvas, IReadOnlyList<CurvePoint>>(nameof(CompanionShape), []);

    public static readonly StyledProperty<IReadOnlyList<CurveTangent>> TangentsProperty =
        AvaloniaProperty.Register<CurveCanvas, IReadOnlyList<CurveTangent>>(nameof(Tangents), []);

    public static readonly StyledProperty<bool> RemoveWhenDraggedOffCanvasProperty =
        AvaloniaProperty.Register<CurveCanvas, bool>(nameof(RemoveWhenDraggedOffCanvas), true);

    public static readonly StyledProperty<bool> AddOnEmptyPointerPressProperty =
        AvaloniaProperty.Register<CurveCanvas, bool>(nameof(AddOnEmptyPointerPress));

    public static readonly StyledProperty<bool> LogicalNudgesProperty =
        AvaloniaProperty.Register<CurveCanvas, bool>(nameof(LogicalNudges));

    /// <summary>Optional read-only audio context drawn behind an automation curve.</summary>
    public static readonly StyledProperty<IReadOnlyList<float>?> WaveformPeaksProperty =
        AvaloniaProperty.Register<CurveCanvas, IReadOnlyList<float>?>(nameof(WaveformPeaks));

    /// <summary>
    /// Where the cursor is, as a fraction of the visible span, or NaN for no cursor.
    /// </summary>
    /// <remarks>
    /// The editor had a cursor - the slider set it, keys were added at it, SEEK sent the show to it - and
    /// nowhere on the plot showed where it was. Reading a ramp against a position you cannot see means
    /// running the cue to find out what you drew.
    /// </remarks>
    public static readonly StyledProperty<double> CursorFractionProperty =
        AvaloniaProperty.Register<CurveCanvas, double>(nameof(CursorFraction), double.NaN);

    public CurveCanvas()
    {
        InitializeComponent();
        Focusable = true;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public event EventHandler<CurveGesture>? Gesture;

    /// <summary>Raised when a drag ends, so the view-model can close its undo step.</summary>
    public event EventHandler? GestureCompleted;
    public event EventHandler? GestureCancelled;

    /// <summary>Raised on a right-click, to toggle the point's hold.</summary>
    public event EventHandler<int>? HoldToggled;

    public event EventHandler? CopyRequested;
    public event EventHandler? PasteRequested;
    public event EventHandler? SelectAllRequested;

    public IReadOnlyList<CurvePoint> Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IReadOnlyList<CurvePoint> Shape
    {
        get => GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    public IReadOnlyList<CurvePoint> CompanionShape
    {
        get => GetValue(CompanionShapeProperty);
        set => SetValue(CompanionShapeProperty, value);
    }

    public IReadOnlyList<CurveTangent> Tangents
    {
        get => GetValue(TangentsProperty);
        set => SetValue(TangentsProperty, value);
    }

    public IReadOnlyList<float>? WaveformPeaks
    {
        get => GetValue(WaveformPeaksProperty);
        set => SetValue(WaveformPeaksProperty, value);
    }

    public double CursorFraction
    {
        get => GetValue(CursorFractionProperty);
        set => SetValue(CursorFractionProperty, value);
    }

    /// <summary>Whether the cursor is inside the visible span, so the line is worth drawing. A direct
    /// property rather than a computed one, because the template binds its visibility and a plain getter
    /// would never tell it the cursor had moved off screen.</summary>
    public static readonly DirectProperty<CurveCanvas, bool> HasCursorProperty =
        AvaloniaProperty.RegisterDirect<CurveCanvas, bool>(nameof(HasCursor), canvas => canvas.HasCursor);

    private bool _hasCursor;

    public bool HasCursor
    {
        get => _hasCursor;
        private set => SetAndRaise(HasCursorProperty, ref _hasCursor, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CursorFractionProperty)
            HasCursor = double.IsFinite(CursorFraction) && CursorFraction is >= 0 and <= 1;
    }

    /// <summary>
    /// Whether deliberately releasing beyond the canvas deletes the dragged point. Timeline-style
    /// editors disable this because losing the lane while navigating a long cue must not destroy a key.
    /// </summary>
    public bool RemoveWhenDraggedOffCanvas
    {
        get => GetValue(RemoveWhenDraggedOffCanvasProperty);
        set => SetValue(RemoveWhenDraggedOffCanvasProperty, value);
    }

    /// <summary>Timeline lanes opt in to click-and-drag creation; normalized curve editors keep
    /// double-click creation so their existing selection gesture is unchanged.</summary>
    public bool AddOnEmptyPointerPress
    {
        get => GetValue(AddOnEmptyPointerPressProperty);
        set => SetValue(AddOnEmptyPointerPressProperty, value);
    }

    /// <summary>When true arrow gestures carry logical +/-1 units for a time/value editor to map to
    /// its current grids. Normalized curve editors retain their existing one-percent nudges.</summary>
    public bool LogicalNudges
    {
        get => GetValue(LogicalNudgesProperty);
        set => SetValue(LogicalNudgesProperty, value);
    }

    private Panel? Surface =>
        _surface ??= this.GetVisualDescendants().OfType<FractionPanel>().FirstOrDefault();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Surface is not { Bounds: { Width: > 0, Height: > 0 } bounds } surface)
            return;

        var position = e.GetPosition(surface);
        var index = PointAt(position, bounds);
        var tangent = TangentAt(position, bounds);
        Focus();

        if (e.GetCurrentPoint(surface).Properties.IsRightButtonPressed)
        {
            if (index >= 0)
            {
                HoldToggled?.Invoke(this, index);
                e.Handled = true;
            }

            return;
        }

        if (tangent is not null)
        {
            _draggedTangent = tangent;
            Gesture?.Invoke(this, new CurveGesture(CurveGestureKind.Select, tangent.Index, 0, 0));
            e.Pointer.Capture(this);
            _capturedPointer = e.Pointer;
            e.Handled = true;
            return;
        }

        if (index < 0 && AddOnEmptyPointerPress)
        {
            var add = new CurveGesture(
                CurveGestureKind.Add,
                -1,
                position.X / bounds.Width,
                position.Y / bounds.Height,
                e.KeyModifiers.HasFlag(KeyModifiers.Alt),
                e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            Gesture?.Invoke(this, add);
            if (!add.Accepted)
            {
                // Refused (a key already occupies this time). Take no capture and arm no sentinel, so the
                // gesture is inert rather than turning into a drag of the previous selection.
                e.Handled = true;
                return;
            }

            // The binding projection of the newly-selected key may update after this handler returns.
            // Keep a sentinel until the first move can resolve the selected point from the new list.
            _draggedIndex = int.MaxValue;
            e.Pointer.Capture(this);
            _capturedPointer = e.Pointer;
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            // Double-clicking an existing point is not an add - it would drop a duplicate exactly on
            // top of the one being aimed at, which is the opposite of what the gesture looks like.
            if (index < 0)
            {
                Gesture?.Invoke(this, new CurveGesture(
                    CurveGestureKind.Add, -1, position.X / bounds.Width, position.Y / bounds.Height));
                GestureCompleted?.Invoke(this, EventArgs.Empty);
            }

            e.Handled = true;
            return;
        }

        if (index < 0)
        {
            Gesture?.Invoke(this, new CurveGesture(CurveGestureKind.ClearSelection, -1, 0, 0));
            return;
        }

        _draggedIndex = index;
        var selection = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            ? CurveGestureKind.ToggleSelection
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                ? CurveGestureKind.RangeSelection
                : CurveGestureKind.Select;
        Gesture?.Invoke(this, new CurveGesture(selection, index, 0, 0));
        e.Pointer.Capture(this);
        _capturedPointer = e.Pointer;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_draggedIndex < 0 && _draggedTangent is null
            || Surface is not { Bounds: { Width: > 0, Height: > 0 } bounds } surface)
            return;

        var position = e.GetPosition(surface);
        if (_draggedTangent is { } tangent)
        {
            Gesture?.Invoke(this, new CurveGesture(
                tangent.Incoming ? CurveGestureKind.MoveIncomingTangent : CurveGestureKind.MoveOutgoingTangent,
                tangent.Index, position.X / bounds.Width, position.Y / bounds.Height));
            e.Handled = true;
            return;
        }
        if (_draggedIndex == int.MaxValue)
        {
            var addedIndex = SelectedIndex();
            if (addedIndex < 0)
                return;
            _draggedIndex = addedIndex;
        }
        Gesture?.Invoke(this, new CurveGesture(
            CurveGestureKind.Move, _draggedIndex,
            position.X / bounds.Width, position.Y / bounds.Height,
            e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_draggedIndex < 0 && _draggedTangent is null)
            return;

        if (_draggedTangent is not null)
        {
            _draggedTangent = null;
            e.Pointer.Capture(null);
            _capturedPointer = null;
            GestureCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        var index = _draggedIndex;
        _draggedIndex = -1;
        e.Pointer.Capture(null);
        _capturedPointer = null;

        if (RemoveWhenDraggedOffCanvas
            && Surface is { Bounds: { Width: > 0, Height: > 0 } bounds } surface
            && IsOffCanvas(e.GetPosition(surface), bounds))
        {
            // The whole drag is already one coalesced step, so the removal folds into it: undo puts the
            // point back where it started rather than replaying the drag first.
            Gesture?.Invoke(this, new CurveGesture(CurveGestureKind.Remove, index, 0, 0));
        }

        GestureCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A capture that ends any other way - the window deactivated, the lane torn out of the tree -
    /// must still complete the gesture: the view-model's journal composite stays open until
    /// <see cref="GestureCompleted"/>, and an unclosed one folds every later edit into the drag's undo
    /// step. Leaving the drag fields set is the second half of the same bug: the next pointer MOVE
    /// would keep editing with no button held.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the off-canvas removal <see cref="OnPointerReleased"/> performs. Losing a
    /// capture is not the operator letting go somewhere, so it ends the drag where it stands rather
    /// than deleting the point they were holding.
    /// </remarks>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (_draggedIndex < 0 && _draggedTangent is null)
            return;

        _draggedIndex = -1;
        _draggedTangent = null;
        _capturedPointer = null;
        GestureCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Arrow keys nudge the selected point, for the precision a mouse cannot give.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.A:
                    if (SelectAllRequested is null)
                        break;
                    SelectAllRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                case Key.C:
                    if (CopyRequested is null)
                        break;
                    CopyRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                case Key.V:
                    if (PasteRequested is null)
                        break;
                    PasteRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            Gesture?.Invoke(this, new CurveGesture(CurveGestureKind.RemoveSelection, -1, 0, 0));
            GestureCompleted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_draggedIndex >= 0 || _draggedTangent is not null)
            {
                _draggedIndex = -1;
                _draggedTangent = null;
                _capturedPointer?.Capture(null);
                _capturedPointer = null;
                if (GestureCancelled is not null)
                    GestureCancelled.Invoke(this, EventArgs.Empty);
                else
                    GestureCompleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Gesture?.Invoke(this, new CurveGesture(CurveGestureKind.ClearSelection, -1, 0, 0));
            }
            e.Handled = true;
            return;
        }

        var selected = SelectedIndex();
        if (selected < 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var (dx, dy) = e.Key switch
        {
            Key.Left => (-1d, 0d),
            Key.Right => (1d, 0d),
            Key.Up => (0d, -1d),
            Key.Down => (0d, 1d),
            _ => (0d, 0d),
        };

        if (dx == 0 && dy == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        if (LogicalNudges)
        {
            Gesture?.Invoke(this, new CurveGesture(
                CurveGestureKind.Move,
                selected,
                dx,
                dy,
                e.KeyModifiers.HasFlag(KeyModifiers.Alt),
                IsNudge: true,
                Accelerated: e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
        }
        else
        {
            var multiplier = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
            var point = Points[selected];
            Gesture?.Invoke(this, new CurveGesture(
                CurveGestureKind.Move,
                selected,
                point.X + (dx * NudgeStep * multiplier),
                point.Y + (dy * NudgeStep * multiplier)));
        }
        GestureCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private int SelectedIndex()
    {
        for (var index = 0; index < Points.Count; index++)
            if (Points[index].IsSelected)
                return index;

        return -1;
    }

    private int PointAt(Point position, Rect bounds)
    {
        var best = -1;
        var nearest = PointGrab;

        for (var index = 0; index < Points.Count; index++)
        {
            var dx = (Points[index].X * bounds.Width) - position.X;
            var dy = (Points[index].Y * bounds.Height) - position.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));

            // NEAREST within the grab radius, not the first one found: points crowd together on a
            // steep curve, and grabbing whichever came first in the list would pick the wrong one.
            if (distance > nearest)
                continue;

            nearest = distance;
            best = index;
        }

        return best;
    }

    private CurveTangent? TangentAt(Point position, Rect bounds)
    {
        CurveTangent? best = null;
        var nearest = PointGrab;
        foreach (var tangent in Tangents)
        {
            var dx = (tangent.X * bounds.Width) - position.X;
            var dy = (tangent.Y * bounds.Height) - position.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance > nearest)
                continue;
            nearest = distance;
            best = tangent;
        }
        return best;
    }

    private static bool IsOffCanvas(Point position, Rect bounds) =>
        position.X < -OffCanvas || position.Y < -OffCanvas
        || position.X > bounds.Width + OffCanvas || position.Y > bounds.Height + OffCanvas;
}
