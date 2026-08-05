using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
    private const double EdgeGrab = 12;

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
    private Canvas? _guideLayer;

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

    /// <summary>
    /// Whether a resize keeps the rectangle's start-of-drag aspect ratio. Shift temporarily reverses
    /// this choice, so an occasional free resize does not require leaving the canvas.
    /// </summary>
    public static readonly StyledProperty<bool> PreserveAspectProperty =
        AvaloniaProperty.Register<PlacementCanvas, bool>(nameof(PreserveAspect), true);

    /// <summary>
    /// Gives the composition a surrounding work area and allows hit-testing boxes that overhang it.
    /// The document command remains the authority on how far a rectangle may travel.
    /// </summary>
    public static readonly StyledProperty<bool> AllowOutsideProperty =
        AvaloniaProperty.Register<PlacementCanvas, bool>(nameof(AllowOutside));

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

    public bool PreserveAspect
    {
        get => GetValue(PreserveAspectProperty);
        set => SetValue(PreserveAspectProperty, value);
    }

    public bool AllowOutside
    {
        get => GetValue(AllowOutsideProperty);
        set => SetValue(AllowOutsideProperty, value);
    }

    /// <summary>How near a guide a dragged edge has to come before it takes, in pixels.</summary>
    private const double SnapPixels = 6;

    public PlacementCanvas()
    {
        InitializeComponent();
        _guideLayer = this.FindControl<Canvas>("GuideLayer");
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

        if (Surface is not { } surface)
            return;

        if (_draggedIndex < 0)
        {
            UpdateHoverCursor(e.GetPosition(surface));
            return;
        }

        var position = e.GetPosition(surface);
        var dx = (position.X - _grabbedAt.X) / surface.Bounds.Width;
        var dy = (position.Y - _grabbedAt.Y) / surface.Bounds.Height;

        // Always computed from where the grab STARTED, never accumulated frame by frame: an
        // incremental sum drifts, and a clamped increment loses the movement that was clipped, so the
        // box stops following the pointer once it has touched an edge.
        var moved = Resize(_grabbedRect, _edges, dx, dy);

        // Aspect is the normal editing mode. Shift temporarily reverses the visible option: it unlocks
        // the usual locked editor and locks an editor whose option was deliberately switched off.
        var keepAspect = _edges != ResizeEdges.None
                         && PreserveAspect != e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (keepAspect)
            moved = WithAspect(_grabbedRect, moved, _edges);

        // Alt suspends snapping for one gesture — the escape hatch for placing something a few pixels
        // off a guide, which is otherwise impossible once the guide has it.
        if (SnapEnabled && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            moved = Snap(moved, _edges, surface);

        // A snapped resize may have changed only one dragged edge. Reapply the constraint so a guide
        // never introduces the one-pixel distortion the aspect option exists to prevent.
        if (keepAspect)
            moved = WithAspect(_grabbedRect, moved, _edges);

        UpdateActiveGuides(
            moved,
            surface,
            SnapEnabled && !e.KeyModifiers.HasFlag(KeyModifiers.Alt));

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
        ClearGuides();
        Cursor = Cursor.Default;
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
        NormalizedRect rect, ResizeEdges edges, double dx, double dy)
    {
        if (edges == ResizeEdges.None)
            return new NormalizedRect(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);

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

    /// <summary>Keeps the start rectangle's shape, anchored opposite the edge being dragged.</summary>
    private static NormalizedRect WithAspect(
        NormalizedRect original, NormalizedRect resized, ResizeEdges edges)
    {
        if (original is not { Width: > 0, Height: > 0 })
            return resized;

        var horizontal = edges.HasFlag(ResizeEdges.Left) || edges.HasFlag(ResizeEdges.Right);
        var vertical = edges.HasFlag(ResizeEdges.Top) || edges.HasFlag(ResizeEdges.Bottom);
        if (!horizontal && !vertical)
            return resized;

        var aspect = original.Width / original.Height;
        var width = resized.Width;
        var height = resized.Height;

        if (horizontal && vertical)
        {
            var widthChange = Math.Abs((width / original.Width) - 1);
            var heightChange = Math.Abs((height / original.Height) - 1);
            if (widthChange >= heightChange)
                height = width / aspect;
            else
                width = height * aspect;
        }
        else if (horizontal)
        {
            height = width / aspect;
        }
        else
        {
            width = height * aspect;
        }

        var x = horizontal
            ? edges.HasFlag(ResizeEdges.Left) ? original.X + original.Width - width : original.X
            : original.X + ((original.Width - width) / 2);
        var y = vertical
            ? edges.HasFlag(ResizeEdges.Top) ? original.Y + original.Height - height : original.Y
            : original.Y + ((original.Height - height) / 2);

        return new NormalizedRect(x, y, width, height);
    }

    /// <summary>Changes the cursor before the press, using the same generous edge hit test as the drag.</summary>
    private void UpdateHoverCursor(Point position)
    {
        var index = BoxAt(position);
        if (index < 0)
        {
            Cursor = Cursor.Default;
            return;
        }

        var edges = EdgesAt(position, RectOf(Boxes[index]));
        Cursor = edges switch
        {
            ResizeEdges.Left or ResizeEdges.Right => new Cursor(StandardCursorType.SizeWestEast),
            ResizeEdges.Top or ResizeEdges.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
            ResizeEdges.Left | ResizeEdges.Top or ResizeEdges.Right | ResizeEdges.Bottom =>
                new Cursor(StandardCursorType.TopLeftCorner),
            ResizeEdges.Right | ResizeEdges.Top or ResizeEdges.Left | ResizeEdges.Bottom =>
                new Cursor(StandardCursorType.TopRightCorner),
            _ => new Cursor(StandardCursorType.SizeAll),
        };
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_draggedIndex < 0)
            Cursor = Cursor.Default;
    }

    /// <summary>Draws only the guides the current snapped rectangle is actually aligned with.</summary>
    private void UpdateActiveGuides(NormalizedRect rect, Panel surface, bool visible)
    {
        ClearGuides();
        if (!visible || _guideLayer is null)
            return;

        const double epsilon = 0.000001;
        var xs = new[] { rect.X, rect.X + (rect.Width / 2), rect.X + rect.Width };
        var ys = new[] { rect.Y, rect.Y + (rect.Height / 2), rect.Y + rect.Height };
        var activeX = Guides(GuidesX, horizontal: true)
            .Where(guide => xs.Any(edge => Math.Abs(edge - guide) < epsilon))
            .Distinct()
            .ToList();
        var activeY = Guides(GuidesY, horizontal: false)
            .Where(guide => ys.Any(edge => Math.Abs(edge - guide) < epsilon))
            .Distinct()
            .ToList();

        var stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66));
        foreach (var guide in activeX)
        {
            _guideLayer.Children.Add(new Line
            {
                StartPoint = new Point(guide * surface.Bounds.Width, 0),
                EndPoint = new Point(guide * surface.Bounds.Width, surface.Bounds.Height),
                Stroke = stroke,
                StrokeThickness = 1,
            });
        }

        foreach (var guide in activeY)
        {
            _guideLayer.Children.Add(new Line
            {
                StartPoint = new Point(0, guide * surface.Bounds.Height),
                EndPoint = new Point(surface.Bounds.Width, guide * surface.Bounds.Height),
                Stroke = stroke,
                StrokeThickness = 1,
            });
        }
    }

    private void ClearGuides() => _guideLayer?.Children.Clear();

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
