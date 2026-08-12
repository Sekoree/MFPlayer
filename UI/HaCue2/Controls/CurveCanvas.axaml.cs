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

    /// <summary>Delete this point — a drag that ended off the canvas.</summary>
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
public sealed record CurveGesture(CurveGestureKind Kind, int Index, double X, double Y);

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

    public static readonly StyledProperty<IReadOnlyList<CurvePoint>> PointsProperty =
        AvaloniaProperty.Register<CurveCanvas, IReadOnlyList<CurvePoint>>(nameof(Points), []);

    /// <summary>The polyline to draw. Separate from <see cref="Points"/> because a HELD segment needs
    /// a corner the point list does not contain.</summary>
    public static readonly StyledProperty<IReadOnlyList<CurvePoint>> ShapeProperty =
        AvaloniaProperty.Register<CurveCanvas, IReadOnlyList<CurvePoint>>(nameof(Shape), []);

    public static readonly StyledProperty<IReadOnlyList<CurveTangent>> TangentsProperty =
        AvaloniaProperty.Register<CurveCanvas, IReadOnlyList<CurveTangent>>(nameof(Tangents), []);

    public static readonly StyledProperty<bool> RemoveWhenDraggedOffCanvasProperty =
        AvaloniaProperty.Register<CurveCanvas, bool>(nameof(RemoveWhenDraggedOffCanvas), true);

    public CurveCanvas()
    {
        InitializeComponent();
        Focusable = true;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public event EventHandler<CurveGesture>? Gesture;

    /// <summary>Raised when a drag ends, so the view-model can close its undo step.</summary>
    public event EventHandler? GestureCompleted;

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

    public IReadOnlyList<CurveTangent> Tangents
    {
        get => GetValue(TangentsProperty);
        set => SetValue(TangentsProperty, value);
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
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            // Double-clicking an existing point is not an add — it would drop a duplicate exactly on
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
        Gesture?.Invoke(this, new CurveGesture(
            CurveGestureKind.Move, _draggedIndex,
            position.X / bounds.Width, position.Y / bounds.Height));
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
            GestureCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        var index = _draggedIndex;
        _draggedIndex = -1;
        e.Pointer.Capture(null);

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
    /// A capture that ends any other way — the window deactivated, the lane torn out of the tree —
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
            Gesture?.Invoke(this, new CurveGesture(CurveGestureKind.ClearSelection, -1, 0, 0));
            e.Handled = true;
            return;
        }

        var selected = SelectedIndex();
        if (selected < 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? NudgeStep * 5 : NudgeStep;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0d),
            Key.Right => (step, 0d),
            Key.Up => (0d, -step),
            Key.Down => (0d, step),
            _ => (0d, 0d),
        };

        if (dx == 0 && dy == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var point = Points[selected];
        Gesture?.Invoke(this, new CurveGesture(
            CurveGestureKind.Move, selected, point.X + dx, point.Y + dy));
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
