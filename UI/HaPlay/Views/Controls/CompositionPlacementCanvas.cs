using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HaPlay.ViewModels;

namespace HaPlay.Views.Controls;

/// <summary>
/// Visual editor for a cue's video placements. Draws the composition canvas (aspect-correct) with each
/// placement as a draggable, resizable box positioned from its normalized destination rectangle. Dragging
/// the body moves it; the bottom-right handle resizes it; clicking selects it. All state lives on the
/// bound <see cref="CueVideoPlacementViewModel"/>s - this control only edits their DestX/Y/Width/Height.
/// </summary>
public sealed class CompositionPlacementCanvas : Control
{
    private const double Pad = 8;
    private const double HandleSize = 12;

    public static readonly StyledProperty<IEnumerable?> PlacementsProperty =
        AvaloniaProperty.Register<CompositionPlacementCanvas, IEnumerable?>(nameof(Placements));

    public static readonly StyledProperty<CueVideoPlacementViewModel?> SelectedPlacementProperty =
        AvaloniaProperty.Register<CompositionPlacementCanvas, CueVideoPlacementViewModel?>(
            nameof(SelectedPlacement), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Canvas aspect ratio (width / height). Defaults to 16:9. This is the COMPOSITION's aspect -
    /// never an output window's, since placements are normalized against the composition.</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<CompositionPlacementCanvas, double>(nameof(AspectRatio), 16.0 / 9.0);

    /// <summary>Pull a dragged placement's edges and centre onto the composition's edges and centre (see
    /// <see cref="PlacementSnapMath"/>). On by default: it is what makes "flush to the edge" and "dead
    /// centre" reachable at all, since both are single exact values. Turn it off for free positioning -
    /// out-of-bounds placement works either way.</summary>
    public static readonly StyledProperty<bool> SnapToEdgesProperty =
        AvaloniaProperty.Register<CompositionPlacementCanvas, bool>(nameof(SnapToEdges), true);

    public bool SnapToEdges
    {
        get => GetValue(SnapToEdgesProperty);
        set => SetValue(SnapToEdgesProperty, value);
    }

    /// <summary>Normalized (0..1) tight bounds of the selected TEXT cue's rendered text within its canvas, or
    /// <c>null</c> for a non-text cue. Drawn as a dashed outline inside each placement box so the operator sees the
    /// actual text extent inside the placed frame (the text sits at its FontSizePx size within the full canvas).</summary>
    public static readonly StyledProperty<Rect?> TextBoundsProperty =
        AvaloniaProperty.Register<CompositionPlacementCanvas, Rect?>(nameof(TextBounds));

    public Rect? TextBounds
    {
        get => GetValue(TextBoundsProperty);
        set => SetValue(TextBoundsProperty, value);
    }

    private readonly List<CueVideoPlacementViewModel> _watched = new();
    private CueVideoPlacementViewModel? _drag;
    private bool _resizing;
    private Point _dragGrabNorm; // pointer offset within the box, normalized, at drag start
    private double _dragAspect = 1; // normalized DestWidth/DestHeight captured at resize start (aspect lock)

    static CompositionPlacementCanvas()
    {
        AffectsRender<CompositionPlacementCanvas>(
            SelectedPlacementProperty, AspectRatioProperty, TextBoundsProperty, SnapToEdgesProperty);
    }

    public IEnumerable? Placements
    {
        get => GetValue(PlacementsProperty);
        set => SetValue(PlacementsProperty, value);
    }

    public CueVideoPlacementViewModel? SelectedPlacement
    {
        get => GetValue(SelectedPlacementProperty);
        set => SetValue(SelectedPlacementProperty, value);
    }

    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlacementsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCol)
                oldCol.CollectionChanged -= OnPlacementsCollectionChanged;
            if (change.NewValue is INotifyCollectionChanged newCol)
                newCol.CollectionChanged += OnPlacementsCollectionChanged;
            ResubscribeItems();
            InvalidateVisual();
        }
    }

    private void OnPlacementsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResubscribeItems();
        InvalidateVisual();
    }

    private void ResubscribeItems()
    {
        foreach (var p in _watched)
            p.PropertyChanged -= OnPlacementPropertyChanged;
        _watched.Clear();
        if (Placements is not null)
        {
            foreach (var p in Placements.OfType<CueVideoPlacementViewModel>())
            {
                p.PropertyChanged += OnPlacementPropertyChanged;
                _watched.Add(p);
            }
        }
    }

    private void OnPlacementPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    /// <summary>Fraction of the control reserved AROUND the composition on each side, so a placement dragged
    /// out of bounds has somewhere to be seen. Without it the composition filled the control and an
    /// off-canvas placement was clipped at the control edge - which looks exactly like the box being
    /// resized/cropped rather than moved out, and left nothing to grab to drag it back. It costs a little
    /// canvas size; being able to see what you are doing is worth more.</summary>
    private const double WorkAreaMargin = 0.13;

    private Rect CanvasRect()
    {
        var aspect = AspectRatio <= 0 ? 16.0 / 9.0 : AspectRatio;
        var marginX = Bounds.Width * WorkAreaMargin;
        var marginY = Bounds.Height * WorkAreaMargin;
        var availW = Math.Max(1, Bounds.Width - Math.Max(Pad, marginX) * 2);
        var availH = Math.Max(1, Bounds.Height - Math.Max(Pad, marginY) * 2);
        var cw = Math.Min(availW, availH * aspect);
        var ch = cw / aspect;
        if (ch > availH) { ch = availH; cw = ch * aspect; }
        var cx = (Bounds.Width - cw) / 2;
        var cy = (Bounds.Height - ch) / 2;
        return new Rect(cx, cy, cw, ch);
    }

    private static Rect BoxRect(Rect canvas, CueVideoPlacementViewModel p) => new(
        canvas.X + p.DestX * canvas.Width,
        canvas.Y + p.DestY * canvas.Height,
        Math.Max(1, p.DestWidth * canvas.Width),
        Math.Max(1, p.DestHeight * canvas.Height));

    public override void Render(DrawingContext ctx)
    {
        var canvas = CanvasRect();
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 18)), canvas);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80))), canvas);

        if (Placements is null) return;

        // A placement may now sit partly or wholly OUTSIDE the composition, so boxes are drawn clipped to
        // this control rather than allowed to paint over the surrounding form. The part outside the canvas
        // outline is still visible (that is the point - the operator has to see where an off-canvas
        // placement went), it simply cannot escape the editor.
        using var clip = ctx.PushClip(new Rect(Bounds.Size));
        foreach (var p in Placements.OfType<CueVideoPlacementViewModel>().OrderBy(p => p.LayerIndex))
        {
            var box = BoxRect(canvas, p);
            var selected = ReferenceEquals(p, SelectedPlacement);
            var fill = selected
                ? new SolidColorBrush(Color.FromArgb(0x44, 0x4F, 0x9C, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x22, 0xC0, 0xC0, 0xC0));
            var stroke = selected
                ? new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0x9C, 0xFF)), 2)
                : new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xC0, 0xC0, 0xC0)), 1);
            // Two passes so "outside the composition" is unmistakable: the whole box faintly (that part is
            // authored but will never be rendered), then the same box again clipped to the composition at full
            // strength. Anything beyond the outline is therefore visibly ghosted rather than silently cut off.
            if (!canvas.Contains(box))
            {
                using var ghost = ctx.PushOpacity(0.35);
                ctx.FillRectangle(fill, box);
                ctx.DrawRectangle(null, stroke, box);
            }

            using (ctx.PushClip(canvas))
            {
                ctx.FillRectangle(fill, box);
                ctx.DrawRectangle(null, stroke, box);
            }

            // Text cue: outline the actual rendered-text extent inside the placed frame, so the operator sizes/
            // positions against the visible text rather than the (mostly empty) full text canvas.
            if (TextBounds is { } tb && tb.Width > 0 && tb.Height > 0)
            {
                var textRect = new Rect(
                    box.X + tb.X * box.Width, box.Y + tb.Y * box.Height,
                    Math.Max(1, tb.Width * box.Width), Math.Max(1, tb.Height * box.Height));
                var dash = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xD1, 0x66)), 1)
                {
                    DashStyle = new DashStyle([3, 3], 0),
                };
                ctx.DrawRectangle(null, dash, textRect);
            }

            var label = $"L{p.LayerIndex.ToString(CultureInfo.InvariantCulture)}";
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, Brushes.White);
            if (box.Width > text.Width + 6 && box.Height > text.Height + 4)
                ctx.DrawText(text, new Point(box.X + 4, box.Y + 3));

            if (selected)
            {
                var handle = new Rect(box.Right - HandleSize, box.Bottom - HandleSize, HandleSize, HandleSize);
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x4F, 0x9C, 0xFF)), handle);
            }
        }

        // The composition outline goes on TOP of every box: it is the reference the operator is placing
        // against, so it must never be hidden by a layer that covers or overhangs it.
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0xA0, 0xA0, 0xA0)), 1), canvas);
        DrawActiveGuides(ctx, canvas);
    }

    /// <summary>Draws the composition guides the dragged placement is currently aligned to. Only while
    /// dragging, and only for guides it is actually ON: a guide that appears when nothing is aligned teaches
    /// the operator to distrust them, which is worse than not drawing any.</summary>
    private void DrawActiveGuides(DrawingContext ctx, Rect canvas)
    {
        if (_drag is null || !SnapToEdges)
            return;

        var (xs, ys) = PlacementSnapMath.ActiveGuides(
            _drag.DestX, _drag.DestY, _drag.DestWidth, _drag.DestHeight, canvas);
        if (xs.Count == 0 && ys.Count == 0)
            return;

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xD1, 0x66)), 1)
        {
            DashStyle = new DashStyle([4, 3], 0),
        };
        foreach (var gx in xs)
        {
            var x = canvas.X + gx * canvas.Width;
            ctx.DrawLine(pen, new Point(x, canvas.Y), new Point(x, canvas.Bottom));
        }

        foreach (var gy in ys)
        {
            var y = canvas.Y + gy * canvas.Height;
            ctx.DrawLine(pen, new Point(canvas.X, y), new Point(canvas.Right, y));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var canvas = CanvasRect();
        if (canvas.Width <= 0 || Placements is null) return;
        var pt = e.GetPosition(this);

        // Topmost (highest layer index) wins for both resize-handle and body hits.
        foreach (var p in Placements.OfType<CueVideoPlacementViewModel>().OrderByDescending(p => p.LayerIndex))
        {
            var box = BoxRect(canvas, p);
            var handle = new Rect(box.Right - HandleSize, box.Bottom - HandleSize, HandleSize, HandleSize);
            var onHandle = ReferenceEquals(p, SelectedPlacement) && handle.Contains(pt);
            if (onHandle || box.Contains(pt))
            {
                SelectedPlacement = p;
                _drag = p;
                _resizing = onHandle;
                _dragAspect = p.DestHeight > 0 ? p.DestWidth / p.DestHeight : 1;
                _dragGrabNorm = new Point(
                    (pt.X - box.X) / canvas.Width,
                    (pt.Y - box.Y) / canvas.Height);
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }
    }

    /// <summary>Cursor feedback BEFORE the press, so which gesture a drag will be is never a guess. It
    /// matters most for a full-canvas layer: its resize handle sits in the composition's bottom-right corner,
    /// which is exactly where an operator reaches to push the layer out to the right - a move that silently
    /// became a resize. The pointer now says which one it will be.</summary>
    private void UpdateHoverCursor(Point pt)
    {
        var canvas = CanvasRect();
        if (canvas.Width <= 0 || Placements is null)
        {
            Cursor = Cursor.Default;
            return;
        }

        foreach (var p in Placements.OfType<CueVideoPlacementViewModel>().OrderByDescending(p => p.LayerIndex))
        {
            var box = BoxRect(canvas, p);
            if (ReferenceEquals(p, SelectedPlacement)
                && new Rect(box.Right - HandleSize, box.Bottom - HandleSize, HandleSize, HandleSize).Contains(pt))
            {
                Cursor = new Cursor(StandardCursorType.BottomRightCorner);
                return;
            }

            if (box.Contains(pt))
            {
                Cursor = new Cursor(StandardCursorType.SizeAll);
                return;
            }
        }

        Cursor = Cursor.Default;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null)
        {
            UpdateHoverCursor(e.GetPosition(this));
            return;
        }

        var canvas = CanvasRect();
        if (canvas.Width <= 0 || canvas.Height <= 0) return;
        var pt = e.GetPosition(this);
        var nx = (pt.X - canvas.X) / canvas.Width;
        var ny = (pt.Y - canvas.Y) / canvas.Height;

        // Snapping is suppressed while Ctrl is held, the usual "let me place it exactly here" escape - so the
        // operator can override a guide without leaving the toggle off.
        var snap = SnapToEdges && !e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (_resizing)
        {
            var w = PlacementSnapMath.SnapResizeAxis(
                _drag.DestX, nx - _drag.DestX, PlacementSnapMath.Threshold(canvas.Width), snap);
            var h = PlacementSnapMath.SnapResizeAxis(
                _drag.DestY, ny - _drag.DestY, PlacementSnapMath.Threshold(canvas.Height), snap);

            // Aspect-locked by default: keep the box's start-of-drag proportions (so a source-sized
            // box stays at the video's aspect). Hold Shift to resize width/height freely. The height
            // follows the WIDTH, so the width's snap is what the operator feels; re-clamping both keeps
            // an aspect-derived edge inside the usable size range.
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _dragAspect > 0)
            {
                h = Math.Clamp(w / _dragAspect, PlacementSnapMath.MinSize, PlacementSnapMath.MaxSize);
                w = Math.Clamp(h * _dragAspect, PlacementSnapMath.MinSize, PlacementSnapMath.MaxSize);
            }

            _drag.SetDestRect(_drag.DestX, _drag.DestY, w, h);
        }
        else
        {
            var moved = PlacementSnapMath.SnapAndClampMove(
                nx - _dragGrabNorm.X, ny - _dragGrabNorm.Y,
                _drag.DestWidth, _drag.DestHeight, canvas, snap);
            _drag.SetDestRect(moved.X, moved.Y, _drag.DestWidth, _drag.DestHeight);
        }

        InvalidateVisual(); // guide lines follow the drag
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not null)
        {
            _drag = null;
            _resizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
