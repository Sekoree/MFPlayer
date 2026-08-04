using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.Core.Journal;
using HaCue2.ViewModels;

namespace HaCue2.Controls;

/// <summary>Which edges a drag is moving. <see cref="None"/> moves the whole box.</summary>
[Flags]
public enum ResizeEdges
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
}

/// <summary>One move or resize, in fractions of the canvas.</summary>
/// <param name="Index">Which box, by its position in <see cref="PlacementCanvas.Boxes"/>.</param>
/// <param name="SubjectId">The document object it stands for — a cue, or a mapping section.</param>
/// <param name="Layer">
/// Which of the subject's placements. A cue can appear on several canvases at once, so the cue id
/// alone does not say which rectangle was dragged.
/// </param>
/// <param name="Rect">Where the box should end up.</param>
public sealed record PlacementGesture(int Index, Guid SubjectId, int Layer, NormalizedRect Rect);

/// <summary>
/// Draws fraction-positioned boxes on a canvas and turns drags on them into rectangle edits.
/// </summary>
/// <remarks>
/// Register item 22 asks for three input modes on a mapping: mouse drag, numeric entry, and arrow-key
/// nudge. Two of them live here (the numeric fields are ordinary bound text boxes) and both emit the
/// same gesture, so all three routes end in one command and one undo step.
/// </remarks>
public partial class PlacementCanvas : UserControl
{
    /// <summary>How close to an edge counts as grabbing it rather than the body.</summary>
    private const double EdgeGrab = 7;

    /// <summary>Arrow-key step, and the coarse step with Shift held.</summary>
    private const double NudgeStep = 0.005;

    private const double CoarseNudgeStep = 0.05;

    /// <summary>
    /// The panel the boxes are laid out in — the surface every coordinate here is measured against.
    /// </summary>
    /// <remarks>
    /// Not the control's own bounds: the canvas border insets the panel by a pixel, and measuring
    /// against the outer box would offset every hit test and skew every delta by that inset.
    /// </remarks>
    private Panel? _surface;

    private int _draggedIndex = -1;
    private ResizeEdges _edges;
    private Point _grabbedAt;
    private NormalizedRect _grabbedRect;

    public static readonly StyledProperty<IReadOnlyList<PlacementBox>> BoxesProperty =
        AvaloniaProperty.Register<PlacementCanvas, IReadOnlyList<PlacementBox>>(nameof(Boxes), []);

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<PlacementCanvas, bool>(nameof(ShowGrid), true);

    /// <summary>Whether drags edit. Off for a canvas that only illustrates.</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<PlacementCanvas, bool>(nameof(IsEditable), true);

    /// <summary>
    /// Extra vertical guides to snap to, in fractions of the canvas.
    /// </summary>
    /// <remarks>
    /// The canvas supplies its own edges and centre; these are what the SHOW adds — the boundaries
    /// between the output slices of a composition layout, so a picture can be lined up exactly with one
    /// screen of a wall without arithmetic.
    /// </remarks>
    public static readonly StyledProperty<IReadOnlyList<double>> GuidesXProperty =
        AvaloniaProperty.Register<PlacementCanvas, IReadOnlyList<double>>(nameof(GuidesX), []);

    public static readonly StyledProperty<IReadOnlyList<double>> GuidesYProperty =
        AvaloniaProperty.Register<PlacementCanvas, IReadOnlyList<double>>(nameof(GuidesY), []);

    /// <summary>Whether a drag snaps at all. Alt suspends it for one gesture.</summary>
    public static readonly StyledProperty<bool> SnapEnabledProperty =
        AvaloniaProperty.Register<PlacementCanvas, bool>(nameof(SnapEnabled), true);

    public IReadOnlyList<double> GuidesX
    {
        get => GetValue(GuidesXProperty);
        set => SetValue(GuidesXProperty, value);
    }

    public IReadOnlyList<double> GuidesY
    {
        get => GetValue(GuidesYProperty);
        set => SetValue(GuidesYProperty, value);
    }

    public bool SnapEnabled
    {
        get => GetValue(SnapEnabledProperty);
        set => SetValue(SnapEnabledProperty, value);
    }

    /// <summary>How near a guide a dragged edge has to come before it takes, in pixels.</summary>
    private const double SnapPixels = 6;

    public PlacementCanvas()
    {
        InitializeComponent();
        Focusable = true;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Finds the layout panel once it exists.
    /// </summary>
    /// <remarks>
    /// It lives inside an <c>ItemsPanelTemplate</c>, so it is not reachable by name from the control's
    /// own name scope and is not built until the ItemsControl first measures.
    /// </remarks>
    private Panel? Surface => _surface ??= this.GetVisualDescendants().OfType<FractionPanel>().FirstOrDefault();

    /// <summary>Raised continuously during a drag, and once per arrow-key nudge.</summary>
    public event EventHandler<PlacementGesture>? Gesture;

    /// <summary>Raised when a drag ends, so the view-model can close its undo step.</summary>
    public event EventHandler? GestureCompleted;

    /// <summary>Raised when a box is clicked, so the view-model can select it.</summary>
    public event EventHandler<int>? BoxSelected;

    public IReadOnlyList<PlacementBox> Boxes
    {
        get => GetValue(BoxesProperty);
        set => SetValue(BoxesProperty, value);
    }

    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!IsEditable || Surface is not { Bounds.Width: > 0, Bounds.Height: > 0 } surface)
            return;

        var position = e.GetPosition(surface);
        var index = BoxAt(position);
        if (index < 0)
            return;

        Focus();
        BoxSelected?.Invoke(this, index);

        _draggedIndex = index;
        _grabbedAt = position;
        _grabbedRect = RectOf(Boxes[index]);
        _edges = EdgesAt(position, _grabbedRect);

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_draggedIndex < 0 || Surface is not { } surface)
            return;

        var position = e.GetPosition(surface);
        var dx = (position.X - _grabbedAt.X) / surface.Bounds.Width;
        var dy = (position.Y - _grabbedAt.Y) / surface.Bounds.Height;

        // Always computed from where the grab STARTED, never accumulated frame by frame: an
        // incremental sum drifts, and a clamped increment loses the movement that was clipped, so the
        // box stops following the pointer once it has touched an edge.
        var moved = Resize(
            _grabbedRect,
            _edges,
            dx,
            dy,
            // Shift keeps a corner drag on the box's own aspect ratio, which is what stops a resize
            // quietly distorting a picture that was placed to match its source.
            e.KeyModifiers.HasFlag(KeyModifiers.Shift));

        // Alt suspends snapping for one gesture — the escape hatch for placing something a few pixels
        // off a guide, which is otherwise impossible once the guide has it.
        if (SnapEnabled && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            moved = Snap(moved, _edges, surface);

        Gesture?.Invoke(this, new PlacementGesture(
            _draggedIndex,
            Boxes[_draggedIndex].SubjectId,
            Boxes[_draggedIndex].LayerIndex,
            moved));
        e.Handled = true;
    }

    /// <summary>
    /// Pulls the edges a drag is moving onto the nearest guide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A MOVE snaps whichever of the box's own edges — and its centre — comes nearest a guide, and
    /// carries the whole box with it, so a rectangle can be aligned by its left edge, its right edge or
    /// its middle without choosing a mode. A RESIZE snaps only the edges being dragged, because the
    /// opposite side is the thing being measured from.
    /// </para>
    /// <para>
    /// The tolerance is in PIXELS rather than fractions: it has to feel the same on a canvas drawn
    /// small in the inspector and on the same canvas filling the video pane.
    /// </para>
    /// </remarks>
    private NormalizedRect Snap(NormalizedRect rect, ResizeEdges edges, Panel surface)
    {
        var toleranceX = surface.Bounds.Width > 0 ? SnapPixels / surface.Bounds.Width : 0;
        var toleranceY = surface.Bounds.Height > 0 ? SnapPixels / surface.Bounds.Height : 0;

        var guidesX = Guides(GuidesX, horizontal: true);
        var guidesY = Guides(GuidesY, horizontal: false);

        var (x, y, width, height) = (rect.X, rect.Y, rect.Width, rect.Height);

        if (edges == ResizeEdges.None)
        {
            if (Nearest([x, x + (width / 2), x + width], guidesX, toleranceX) is { } shiftX)
                x += shiftX;
            if (Nearest([y, y + (height / 2), y + height], guidesY, toleranceY) is { } shiftY)
                y += shiftY;

            return new NormalizedRect(x, y, width, height);
        }

        if (edges.HasFlag(ResizeEdges.Left) && Nearest([x], guidesX, toleranceX) is { } left)
        {
            x += left;
            width -= left;
        }
        else if (edges.HasFlag(ResizeEdges.Right)
                 && Nearest([x + width], guidesX, toleranceX) is { } right)
        {
            width += right;
        }

        if (edges.HasFlag(ResizeEdges.Top) && Nearest([y], guidesY, toleranceY) is { } top)
        {
            y += top;
            height -= top;
        }
        else if (edges.HasFlag(ResizeEdges.Bottom)
                 && Nearest([y + height], guidesY, toleranceY) is { } bottom)
        {
            height += bottom;
        }

        return new NormalizedRect(x, y, width, height);
    }

    /// <summary>
    /// Everything a drag can snap to on one axis.
    /// </summary>
    /// <remarks>
    /// The canvas edges and its centre always, the caller's own guides, and the edges and centres of
    /// every OTHER box on the canvas — lining two placements up with each other is the commonest
    /// alignment there is and the one arithmetic helps with least.
    /// </remarks>
    private List<double> Guides(IReadOnlyList<double> extra, bool horizontal)
    {
        var guides = new List<double> { 0, 0.5, 1 };
        guides.AddRange(extra);

        for (var index = 0; index < Boxes.Count; index++)
        {
            if (index == _draggedIndex)
                continue;

            var box = RectOf(Boxes[index]);
            var near = horizontal ? box.X : box.Y;
            var far = horizontal ? box.X + box.Width : box.Y + box.Height;

            guides.Add(near);
            guides.Add((near + far) / 2);
            guides.Add(far);
        }

        return guides;
    }

    /// <summary>The smallest shift that puts one of <paramref name="edges"/> on a guide, or null.</summary>
    private static double? Nearest(
        IReadOnlyList<double> edges, IReadOnlyList<double> guides, double tolerance)
    {
        if (tolerance <= 0)
            return null;

        double? best = null;

        foreach (var edge in edges)
        {
            foreach (var guide in guides)
            {
                var shift = guide - edge;

                if (Math.Abs(shift) <= tolerance && (best is not { } current || Math.Abs(shift) < Math.Abs(current)))
                    best = shift;
            }
        }

        return best;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_draggedIndex < 0)
            return;

        _draggedIndex = -1;
        _edges = ResizeEdges.None;
        e.Pointer.Capture(null);
        GestureCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Arrow keys nudge the selected box; Shift makes the step coarse.
    /// </summary>
    /// <remarks>
    /// The reason register item 22 asks for this: a projector's alignment is decided in single pixels
    /// at the far end of a room, and a mouse cannot reliably deliver one.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var selected = SelectedIndex();
        if (!IsEditable || selected < 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? CoarseNudgeStep : NudgeStep;
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

        var rect = RectOf(Boxes[selected]);
        Gesture?.Invoke(this, new PlacementGesture(
            selected,
            Boxes[selected].SubjectId,
            Boxes[selected].LayerIndex,
            new NormalizedRect(rect.X + dx, rect.Y + dy, rect.Width, rect.Height)));

        // Each keypress is its own gesture: holding an arrow key should still collapse into one undo
        // step, but a pause between two presses should not.
        GestureCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private int SelectedIndex()
    {
        for (var index = 0; index < Boxes.Count; index++)
            if (Boxes[index].IsSelected)
                return index;

        return -1;
    }

    /// <summary>
    /// The topmost box under the pointer.
    /// </summary>
    /// <remarks>
    /// Searched back to front so an overlapping box on top is the one grabbed — the same order it is
    /// drawn in, which is the only order that matches what the operator sees.
    /// </remarks>
    private int BoxAt(Point position)
    {
        for (var index = Boxes.Count - 1; index >= 0; index--)
            if (PixelRect(RectOf(Boxes[index])).Inflate(EdgeGrab).Contains(position))
                return index;

        return -1;
    }

    private ResizeEdges EdgesAt(Point position, NormalizedRect rect)
    {
        var pixels = PixelRect(rect);
        var edges = ResizeEdges.None;

        if (Math.Abs(position.X - pixels.Left) <= EdgeGrab)
            edges |= ResizeEdges.Left;
        else if (Math.Abs(position.X - pixels.Right) <= EdgeGrab)
            edges |= ResizeEdges.Right;

        if (Math.Abs(position.Y - pixels.Top) <= EdgeGrab)
            edges |= ResizeEdges.Top;
        else if (Math.Abs(position.Y - pixels.Bottom) <= EdgeGrab)
            edges |= ResizeEdges.Bottom;

        return edges;
    }

    private static NormalizedRect Resize(
        NormalizedRect rect, ResizeEdges edges, double dx, double dy, bool keepAspect = false)
    {
        if (edges == ResizeEdges.None)
            return new NormalizedRect(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);

        // A CORNER drag with Shift moves both axes by the same proportion, so the box keeps the shape
        // it had. Driven by the larger of the two movements: following one axis alone makes the corner
        // lag the pointer diagonally.
        if (keepAspect
            && rect is { Width: > 0, Height: > 0 }
            && (edges.HasFlag(ResizeEdges.Left) || edges.HasFlag(ResizeEdges.Right))
            && (edges.HasFlag(ResizeEdges.Top) || edges.HasFlag(ResizeEdges.Bottom)))
        {
            var growX = edges.HasFlag(ResizeEdges.Left) ? -dx : dx;
            var growY = edges.HasFlag(ResizeEdges.Top) ? -dy : dy;
            var scale = Math.Abs(growX / rect.Width) >= Math.Abs(growY / rect.Height)
                ? growX / rect.Width
                : growY / rect.Height;

            growX = scale * rect.Width;
            growY = scale * rect.Height;

            dx = edges.HasFlag(ResizeEdges.Left) ? -growX : growX;
            dy = edges.HasFlag(ResizeEdges.Top) ? -growY : growY;
        }

        var (x, y, width, height) = (rect.X, rect.Y, rect.Width, rect.Height);

        if (edges.HasFlag(ResizeEdges.Left))
        {
            x += dx;
            width -= dx;
        }
        else if (edges.HasFlag(ResizeEdges.Right))
        {
            width += dx;
        }

        if (edges.HasFlag(ResizeEdges.Top))
        {
            y += dy;
            height -= dy;
        }
        else if (edges.HasFlag(ResizeEdges.Bottom))
        {
            height += dy;
        }

        return new NormalizedRect(x, y, width, height);
    }

    private Rect PixelRect(NormalizedRect rect)
    {
        var bounds = Surface?.Bounds ?? Bounds;

        return new Rect(
            rect.X * bounds.Width, rect.Y * bounds.Height,
            rect.Width * bounds.Width, rect.Height * bounds.Height);
    }

    private static NormalizedRect RectOf(PlacementBox box) =>
        new(box.Left, box.Top, box.Width, box.Height);
}
