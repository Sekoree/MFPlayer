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
/// Timeline editor surface for one Timeline group: a time ruler plus one lane per child in tree
/// order. Media (and nested-group) blocks sit at <c>TimelineStartMs</c>; dragging the body moves the
/// start, edge-drags trim <c>Start/EndOffsetMs</c> (content-anchored), corner handles drag
/// <c>FadeIn/FadeOutMs</c>, zero-length cues are draggable diamond markers. All edits write through
/// the bound <see cref="CueNodeViewModel"/> properties; geometry/snap math lives in
/// <see cref="TimelineMath"/> (the <see cref="CompositionPlacementCanvas"/> manual hit-test pattern).
/// </summary>
public sealed class TimelineCanvas : Control
{
    private const int MinEffectiveMs = 100;
    private const double MinPxPerMs = 0.002; // 2 px/s
    private const double MaxPxPerMs = 1.0; // 1 px/ms
    private const int TailPaddingMs = 10_000;

    public static readonly StyledProperty<IEnumerable?> LanesProperty =
        AvaloniaProperty.Register<TimelineCanvas, IEnumerable?>(nameof(Lanes));

    public static readonly StyledProperty<double> PixelsPerMsProperty =
        AvaloniaProperty.Register<TimelineCanvas, double>(nameof(PixelsPerMs), 0.05);

    public static readonly StyledProperty<bool> SnapEnabledProperty =
        AvaloniaProperty.Register<TimelineCanvas, bool>(nameof(SnapEnabled), true);

    public static readonly StyledProperty<int> GridMsProperty =
        AvaloniaProperty.Register<TimelineCanvas, int>(nameof(GridMs), 1000);

    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<TimelineCanvas, bool>(nameof(IsEditable));

    /// <summary>Live playhead position on the group epoch, in ms; negative hides the playhead.</summary>
    public static readonly StyledProperty<double> PlayheadMsProperty =
        AvaloniaProperty.Register<TimelineCanvas, double>(nameof(PlayheadMs), -1);

    private readonly List<CueNodeViewModel> _watched = new();

    private CueNodeViewModel? _drag;
    private TimelineHitKind _dragKind;
    private double _grabOffsetMs;
    private int _dragStartMs;
    private int _dragStartOffsetMs;
    private int _dragEndOffsetMs;
    private int _dragBlockDurationMs;

    static TimelineCanvas()
    {
        AffectsRender<TimelineCanvas>(SnapEnabledProperty, GridMsProperty, IsEditableProperty, PlayheadMsProperty);
        AffectsMeasure<TimelineCanvas>(PixelsPerMsProperty, LanesProperty);
    }

    public IEnumerable? Lanes
    {
        get => GetValue(LanesProperty);
        set => SetValue(LanesProperty, value);
    }

    public double PixelsPerMs
    {
        get => GetValue(PixelsPerMsProperty);
        set => SetValue(PixelsPerMsProperty, value);
    }

    public bool SnapEnabled
    {
        get => GetValue(SnapEnabledProperty);
        set => SetValue(SnapEnabledProperty, value);
    }

    public int GridMs
    {
        get => GetValue(GridMsProperty);
        set => SetValue(GridMsProperty, value);
    }

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public double PlayheadMs
    {
        get => GetValue(PlayheadMsProperty);
        set => SetValue(PlayheadMsProperty, value);
    }

    public void ZoomIn() => SetZoom(PixelsPerMs * 1.5);

    public void ZoomOut() => SetZoom(PixelsPerMs / 1.5);

    /// <summary>Fit the whole authored span (plus tail padding) into <paramref name="viewportWidth"/>.</summary>
    public void ZoomFit(double viewportWidth)
    {
        var span = ContentSpanMs();
        if (span <= 0 || viewportWidth <= 0)
            return;
        SetZoom(viewportWidth / span);
    }

    private void SetZoom(double pxPerMs) =>
        PixelsPerMs = Math.Clamp(pxPerMs, MinPxPerMs, MaxPxPerMs);

    private List<CueNodeViewModel> LaneNodes() =>
        Lanes is null ? [] : Lanes.OfType<CueNodeViewModel>().ToList();

    private double ContentSpanMs()
    {
        double span = TailPaddingMs;
        foreach (var node in LaneNodes())
            span = Math.Max(span, Math.Max(0, node.TimelineStartMs) + TimelineMath.BlockDurationMs(node) + TailPaddingMs);
        return span;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = LaneNodes();
        return new Size(
            TimelineMath.XForMs(ContentSpanMs(), PixelsPerMs),
            TimelineMath.LanesHeight(lanes.Count));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LanesProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCol)
                oldCol.CollectionChanged -= OnLanesCollectionChanged;
            if (change.NewValue is INotifyCollectionChanged newCol)
                newCol.CollectionChanged += OnLanesCollectionChanged;
            ResubscribeItems();
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // The lane nodes outlive this window - drop the subscriptions or they pin the canvas.
        if (Lanes is INotifyCollectionChanged col)
            col.CollectionChanged -= OnLanesCollectionChanged;
        foreach (var node in _watched)
            node.PropertyChanged -= OnLaneItemPropertyChanged;
        _watched.Clear();
    }

    private void OnLanesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResubscribeItems();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ResubscribeItems()
    {
        foreach (var node in _watched)
            node.PropertyChanged -= OnLaneItemPropertyChanged;
        _watched.Clear();
        foreach (var node in LaneNodes())
        {
            node.PropertyChanged += OnLaneItemPropertyChanged;
            _watched.Add(node);
        }
    }

    private void OnLaneItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueNodeViewModel.TimelineStartMs)
            or nameof(CueNodeViewModel.DurationMs)
            or nameof(CueNodeViewModel.StartOffsetMs)
            or nameof(CueNodeViewModel.EndOffsetMs)
            or nameof(CueNodeViewModel.RolledDurationMs))
            InvalidateMeasure();
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var lanes = LaneNodes();
        var pxPerMs = PixelsPerMs;
        var width = Math.Max(Bounds.Width, TimelineMath.XForMs(ContentSpanMs(), pxPerMs));
        var height = TimelineMath.LanesHeight(Math.Max(1, lanes.Count));

        // Lane stripes (alternating) under everything.
        var stripe = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        for (var i = 0; i < lanes.Count; i++)
        {
            if (i % 2 == 0)
                ctx.FillRectangle(stripe, new Rect(0, TimelineMath.LaneTop(i), width, TimelineMath.LaneHeight));
        }

        RenderGridAndRuler(ctx, width, height, pxPerMs);

        for (var i = 0; i < lanes.Count; i++)
            RenderLaneItem(ctx, lanes[i], i, pxPerMs);

        if (PlayheadMs >= 0)
        {
            var x = TimelineMath.XForMs(PlayheadMs, pxPerMs);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), 2), new Point(x, 0), new Point(x, height));
        }
    }

    private void RenderGridAndRuler(DrawingContext ctx, double width, double height, double pxPerMs)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)));
        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xC0, 0xC0, 0xC0)));
        var labelBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0xC0, 0xC0, 0xC0));

        // Snap grid (only when readable - a 100 ms grid fully zoomed out is just noise).
        if (SnapEnabled && GridMs > 0 && GridMs * pxPerMs >= 6)
        {
            for (var ms = 0; ms * pxPerMs <= width; ms += GridMs)
            {
                var x = TimelineMath.XForMs(ms, pxPerMs);
                ctx.DrawLine(gridPen, new Point(x, TimelineMath.RulerHeight), new Point(x, height));
            }
        }

        var step = TimelineMath.RulerStepMs(pxPerMs);
        for (var ms = 0; ms * pxPerMs <= width; ms += step)
        {
            var x = TimelineMath.XForMs(ms, pxPerMs);
            ctx.DrawLine(tickPen, new Point(x, TimelineMath.RulerHeight - 6), new Point(x, TimelineMath.RulerHeight));
            ctx.DrawLine(gridPen, new Point(x, TimelineMath.RulerHeight), new Point(x, height));
            var label = new FormattedText(
                TimelineMath.FormatRulerLabel(ms), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 10, labelBrush);
            ctx.DrawText(label, new Point(x + 3, 2));
        }
        ctx.DrawLine(tickPen, new Point(0, TimelineMath.RulerHeight), new Point(width, TimelineMath.RulerHeight));
    }

    private void RenderLaneItem(DrawingContext ctx, CueNodeViewModel node, int laneIndex, double pxPerMs)
    {
        if (node.Kind == CueNodeKind.Comment)
        {
            RenderMarker(ctx, node, laneIndex, pxPerMs, Color.FromArgb(0xB0, 0x90, 0x90, 0x90));
            return;
        }

        if (TimelineMath.IsMarker(node))
        {
            RenderMarker(ctx, node, laneIndex, pxPerMs, Color.FromArgb(0xD0, 0xFF, 0xD1, 0x66));
            return;
        }

        var block = TimelineMath.BlockRect(laneIndex, node.TimelineStartMs, TimelineMath.BlockDurationMs(node), pxPerMs);
        var probed = node.Kind != CueNodeKind.Media || node.DurationMs > 0;
        var fill = new SolidColorBrush(Color.FromArgb(probed ? (byte)0x3C : (byte)0x20, 0x4F, 0x9C, 0xFF));
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x4F, 0x9C, 0xFF)), 1);
        ctx.FillRectangle(fill, block);
        ctx.DrawRectangle(null, stroke, block);

        if (TimelineMath.IsTrimmable(node))
            RenderFades(ctx, node, block, pxPerMs);

        var label = $"{node.Number} {node.Label}".Trim();
        if (label.Length > 0)
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, Brushes.White)
            {
                MaxTextWidth = Math.Max(1, block.Width - 8),
                MaxLineCount = 1,
            };
            if (block.Width > 20)
                ctx.DrawText(text, new Point(block.X + 4, block.Center.Y - text.Height / 2));
        }
    }

    private void RenderFades(DrawingContext ctx, CueNodeViewModel node, Rect block, double pxPerMs)
    {
        var fadeBrush = new SolidColorBrush(Color.FromArgb(0x46, 0x00, 0x00, 0x00));
        var fadePen = new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0xE0, 0xE0, 0xE0)), 1);

        var inCenter = TimelineMath.FadeInHandleCenter(block, node.FadeInMs, pxPerMs);
        if (node.FadeInMs > 0)
        {
            var tri = new PolylineGeometry([new Point(block.X, block.Y), inCenter, new Point(block.X, block.Bottom)], true);
            ctx.DrawGeometry(fadeBrush, null, tri);
            ctx.DrawLine(fadePen, new Point(block.X, block.Bottom), inCenter);
        }

        var outCenter = TimelineMath.FadeOutHandleCenter(block, node.FadeOutMs, pxPerMs);
        if (node.FadeOutMs > 0)
        {
            var tri = new PolylineGeometry([new Point(block.Right, block.Y), outCenter, new Point(block.Right, block.Bottom)], true);
            ctx.DrawGeometry(fadeBrush, null, tri);
            ctx.DrawLine(fadePen, outCenter, new Point(block.Right, block.Bottom));
        }

        if (IsEditable)
        {
            var handleBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            DrawHandle(ctx, handleBrush, inCenter);
            DrawHandle(ctx, handleBrush, outCenter);
        }
    }

    private static void DrawHandle(DrawingContext ctx, IBrush brush, Point center) =>
        ctx.FillRectangle(brush, new Rect(center.X - 3, center.Y - 3, 6, 6));

    private void RenderMarker(DrawingContext ctx, CueNodeViewModel node, int laneIndex, double pxPerMs, Color color)
    {
        var c = TimelineMath.MarkerCenter(laneIndex, node.TimelineStartMs, pxPerMs);
        const double r = TimelineMath.MarkerHalfPx;
        var diamond = new PolylineGeometry(
            [new Point(c.X, c.Y - r), new Point(c.X + r, c.Y), new Point(c.X, c.Y + r), new Point(c.X - r, c.Y)], true);
        ctx.DrawGeometry(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(0xFF, color.R, color.G, color.B))), diamond);

        var label = $"{node.Number} {node.Label}".Trim();
        if (label.Length > 0)
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));
            ctx.DrawText(text, new Point(c.X + r + 4, c.Y - text.Height / 2));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEditable)
            return;

        var p = e.GetPosition(this);
        var lanes = LaneNodes();
        var pxPerMs = PixelsPerMs;

        for (var i = 0; i < lanes.Count; i++)
        {
            var node = lanes[i];
            TimelineHitKind kind;
            if (TimelineMath.IsMarker(node))
            {
                kind = TimelineMath.MarkerContains(TimelineMath.MarkerCenter(i, node.TimelineStartMs, pxPerMs), p)
                    ? TimelineHitKind.Marker
                    : TimelineHitKind.None;
            }
            else
            {
                var block = TimelineMath.BlockRect(i, node.TimelineStartMs, TimelineMath.BlockDurationMs(node), pxPerMs);
                kind = TimelineMath.HitTestBlock(block, node.FadeInMs, node.FadeOutMs, pxPerMs, p, TimelineMath.IsTrimmable(node));
            }

            if (kind == TimelineHitKind.None)
                continue;

            _drag = node;
            _dragKind = kind;
            _dragStartMs = Math.Max(0, node.TimelineStartMs);
            _dragStartOffsetMs = Math.Max(0, node.StartOffsetMs);
            _dragEndOffsetMs = Math.Max(0, node.EndOffsetMs);
            _dragBlockDurationMs = TimelineMath.BlockDurationMs(node);
            _grabOffsetMs = TimelineMath.MsForX(p.X, pxPerMs) - _dragStartMs;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null)
        {
            UpdateHoverCursor(e.GetPosition(this));
            return;
        }

        var pxPerMs = PixelsPerMs;
        var pointerMs = TimelineMath.MsForX(e.GetPosition(this).X, pxPerMs);

        switch (_dragKind)
        {
            case TimelineHitKind.Block:
            case TimelineHitKind.Marker:
                _drag.TimelineStartMs = TimelineMath.Snap(
                    pointerMs - _grabOffsetMs, SnapEnabled, GridMs, EdgeCandidates(_drag), pxPerMs);
                break;

            case TimelineHitKind.LeftEdge:
            {
                // Content-anchored trim: the media keeps its absolute position; the block's left edge
                // slides over it, so TimelineStartMs and StartOffsetMs move together.
                var contentMin = _dragStartMs - _dragStartOffsetMs;
                var contentMax = _dragStartMs + _dragBlockDurationMs - MinEffectiveMs;
                var newStart = Math.Clamp(
                    TimelineMath.Snap(pointerMs, SnapEnabled, GridMs, EdgeCandidates(_drag), pxPerMs),
                    contentMin, contentMax);
                _drag.StartOffsetMs = _dragStartOffsetMs + (newStart - _dragStartMs);
                _drag.TimelineStartMs = newStart;
                break;
            }

            case TimelineHitKind.RightEdge:
            {
                var initialEnd = _dragStartMs + _dragBlockDurationMs;
                var newEnd = Math.Clamp(
                    TimelineMath.Snap(pointerMs, SnapEnabled, GridMs, EdgeCandidates(_drag), pxPerMs),
                    _dragStartMs + MinEffectiveMs, initialEnd + _dragEndOffsetMs);
                _drag.EndOffsetMs = _dragEndOffsetMs + (initialEnd - newEnd);
                break;
            }

            case TimelineHitKind.FadeInHandle:
            {
                var span = TimelineMath.BlockDurationMs(_drag);
                _drag.FadeInMs = Math.Clamp((int)Math.Round(pointerMs - _drag.TimelineStartMs), 0, span);
                break;
            }

            case TimelineHitKind.FadeOutHandle:
            {
                var span = TimelineMath.BlockDurationMs(_drag);
                var end = _drag.TimelineStartMs + span;
                _drag.FadeOutMs = Math.Clamp((int)Math.Round(end - pointerMs), 0, span);
                break;
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not null)
        {
            _drag = null;
            _dragKind = TimelineHitKind.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void UpdateHoverCursor(Point p)
    {
        if (!IsEditable)
        {
            Cursor = Cursor.Default;
            return;
        }

        var lanes = LaneNodes();
        var pxPerMs = PixelsPerMs;
        for (var i = 0; i < lanes.Count; i++)
        {
            var node = lanes[i];
            TimelineHitKind kind;
            if (TimelineMath.IsMarker(node))
            {
                kind = TimelineMath.MarkerContains(TimelineMath.MarkerCenter(i, node.TimelineStartMs, pxPerMs), p)
                    ? TimelineHitKind.Marker
                    : TimelineHitKind.None;
            }
            else
            {
                var block = TimelineMath.BlockRect(i, node.TimelineStartMs, TimelineMath.BlockDurationMs(node), pxPerMs);
                kind = TimelineMath.HitTestBlock(block, node.FadeInMs, node.FadeOutMs, pxPerMs, p, TimelineMath.IsTrimmable(node));
            }

            if (kind == TimelineHitKind.None)
                continue;

            Cursor = kind switch
            {
                TimelineHitKind.LeftEdge or TimelineHitKind.RightEdge => new Cursor(StandardCursorType.SizeWestEast),
                _ => new Cursor(StandardCursorType.Hand),
            };
            return;
        }

        Cursor = Cursor.Default;
    }

    /// <summary>Snap targets while dragging: origin plus every OTHER lane item's start and end.</summary>
    private List<int> EdgeCandidates(CueNodeViewModel dragged)
    {
        var edges = new List<int> { 0 };
        foreach (var node in LaneNodes())
        {
            if (ReferenceEquals(node, dragged))
                continue;
            var start = Math.Max(0, node.TimelineStartMs);
            edges.Add(start);
            var duration = TimelineMath.IsMarker(node) ? 0 : TimelineMath.BlockDurationMs(node);
            if (duration > 0)
                edges.Add(start + duration);
        }
        return edges;
    }
}
