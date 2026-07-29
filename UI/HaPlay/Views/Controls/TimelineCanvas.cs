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
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.ViewModels;

namespace HaPlay.Views.Controls;

/// <summary>
/// Timeline editor surface for one Timeline group: a time ruler plus one lane per child in tree
/// order. Media (and nested-group) blocks sit at the AUDIBLE start
/// (<see cref="TimelineMath.BlockStartMs"/> = <c>TimelineStartMs + PreWaitMs</c>, with a dimmed
/// pre-wait strip back to the authored start); dragging the body moves the
/// start, edge-drags trim <c>Start/EndOffsetMs</c> (content-anchored), corner handles drag
/// <c>FadeIn/FadeOutMs</c>, zero-length cues are draggable diamond markers. Media lanes overlay the
/// volume envelope (Phase B): click the line to add a point, drag a point to move it (dB/time
/// readout), Delete removes the selected point, right-click a segment cycles its curve - every edit
/// writes a NEW list to <see cref="CueNodeViewModel.VolumeEnvelope"/>. All edits write through the
/// bound <see cref="CueNodeViewModel"/> properties; geometry/snap math lives in
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

    /// <summary>Toolbar toggle: overlay the volume-envelope polyline on media lanes (default on).</summary>
    public static readonly StyledProperty<bool> ShowEnvelopesProperty =
        AvaloniaProperty.Register<TimelineCanvas, bool>(nameof(ShowEnvelopes), true);

    private readonly List<CueNodeViewModel> _watched = new();

    private CueNodeViewModel? _drag;
    private TimelineHitKind _dragKind;
    private double _grabOffsetMs;
    private int _dragStartMs;
    private int _dragStartOffsetMs;
    private int _dragEndOffsetMs;
    private int _dragBlockDurationMs;
    private int _dragLaneIndex;
    private int _dragEnvelopeIndex = -1;

    // Envelope selection (Delete target) + the transient drag/right-click readout.
    private CueNodeViewModel? _selectedEnvelopeNode;
    private int _selectedEnvelopeIndex = -1;
    private string? _readoutText;
    private Point _readoutAnchor;
    private readonly DispatcherTimer _readoutClearTimer;

    static TimelineCanvas()
    {
        AffectsRender<TimelineCanvas>(
            SnapEnabledProperty, GridMsProperty, IsEditableProperty, PlayheadMsProperty, ShowEnvelopesProperty);
        AffectsMeasure<TimelineCanvas>(PixelsPerMsProperty, LanesProperty);
    }

    public TimelineCanvas()
    {
        Focusable = true; // Delete/Backspace remove the selected envelope point.
        _readoutClearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _readoutClearTimer.Tick += (_, _) => ClearReadout();
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

    public bool ShowEnvelopes
    {
        get => GetValue(ShowEnvelopesProperty);
        set => SetValue(ShowEnvelopesProperty, value);
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
            span = Math.Max(span, TimelineMath.BlockStartMs(node) + TimelineMath.BlockDurationMs(node) + TailPaddingMs);
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
        _readoutClearTimer.Stop();
        try { _waveformCts.Cancel(); } catch { /* best effort */ }
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
            or nameof(CueNodeViewModel.PreWaitMs) // shifts the block position (audible start)
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
            RenderLaneItem(ctx, lanes[i], i, width, pxPerMs);

        if (PlayheadMs >= 0)
        {
            var x = TimelineMath.XForMs(PlayheadMs, pxPerMs);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), 2), new Point(x, 0), new Point(x, height));
        }

        RenderReadout(ctx, width);
    }

    /// <summary>Tooltip-style dB/time (or curve-name) readout near the pointer while an envelope
    /// point drags or a segment curve was just cycled. Clamped to the SCROLLED VIEWPORT, not to the
    /// canvas extent: the canvas is many screens wide at normal zoom, so clamping against the extent
    /// let the readout land off-screen whenever the anchor sat near the right of the visible area.</summary>
    private void RenderReadout(DrawingContext ctx, double width)
    {
        if (_readoutText is null)
            return;
        var text = new FormattedText(_readoutText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Brushes.White);
        var (visibleLeft, visibleRight) = VisibleXRange(width);
        var minX = visibleLeft + 4;
        var pos = new Point(
            Math.Clamp(_readoutAnchor.X, minX, Math.Max(minX, visibleRight - text.Width - 8)),
            Math.Max(TimelineMath.RulerHeight, _readoutAnchor.Y));
        ctx.FillRectangle(
            new SolidColorBrush(Color.FromArgb(0xD8, 0x20, 0x20, 0x20)),
            new Rect(pos.X - 4, pos.Y - 2, text.Width + 8, text.Height + 4));
        ctx.DrawText(text, pos);
    }

    /// <summary>The canvas-local x range that is actually on screen: the host
    /// <see cref="ScrollViewer"/>'s horizontal window, or the whole extent when the canvas is not
    /// scrolled (design surface, tests).</summary>
    private (double Left, double Right) VisibleXRange(double width)
    {
        foreach (var ancestor in this.GetVisualAncestors())
        {
            if (ancestor is not ScrollViewer { Viewport.Width: > 0 } scroll)
                continue;
            var left = Math.Clamp(scroll.Offset.X, 0, Math.Max(0, width));
            return (left, Math.Min(width, left + scroll.Viewport.Width));
        }
        return (0, width);
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
            // Clamp to the extent: the last tick sits at (or within a few px of) the right edge, so a
            // label drawn at x+3 ran past the measured width and was clipped mid-digit.
            ctx.DrawText(label, new Point(Math.Max(0, Math.Min(x + 3, width - label.Width)), 2));
        }
        ctx.DrawLine(tickPen, new Point(0, TimelineMath.RulerHeight), new Point(width, TimelineMath.RulerHeight));
    }

    private void RenderLaneItem(DrawingContext ctx, CueNodeViewModel node, int laneIndex, double width, double pxPerMs)
    {
        if (node.Kind == CueNodeKind.Comment)
        {
            RenderMarker(ctx, node, laneIndex, width, pxPerMs, Color.FromArgb(0xB0, 0x90, 0x90, 0x90));
            return;
        }

        if (TimelineMath.IsMarker(node))
        {
            RenderMarker(ctx, node, laneIndex, width, pxPerMs, Color.FromArgb(0xD0, 0xFF, 0xD1, 0x66));
            return;
        }

        var block = TimelineMath.BlockRect(laneIndex, TimelineMath.BlockStartMs(node), TimelineMath.BlockDurationMs(node), pxPerMs);
        var probed = node.Kind != CueNodeKind.Media || node.DurationMs > 0;
        var fill = new SolidColorBrush(Color.FromArgb(probed ? (byte)0x3C : (byte)0x20, 0x4F, 0x9C, 0xFF));
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x4F, 0x9C, 0xFF)), 1);

        // Pre-wait span: a dimmed strip from the authored start to the block's (audible) left edge,
        // so a pre-waited lane reads as "arms here, sounds there".
        if (node.PreWaitMs > 0)
        {
            var preWaitX = TimelineMath.XForMs(Math.Max(0, node.TimelineStartMs), pxPerMs);
            var strip = new Rect(
                preWaitX, block.Y + block.Height * 0.35, Math.Max(0, block.X - preWaitX), block.Height * 0.3);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(0x28, 0x4F, 0x9C, 0xFF)), strip);
            ctx.DrawLine(
                new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x4F, 0x9C, 0xFF)), 1),
                new Point(preWaitX, block.Y), new Point(preWaitX, block.Bottom));
        }

        ctx.FillRectangle(fill, block);
        ctx.DrawRectangle(null, stroke, block);

        if (node.Kind == CueNodeKind.Media)
            RenderWaveform(ctx, node, block);

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

        if (ShowEnvelopes && node.Kind == CueNodeKind.Media)
            RenderEnvelope(ctx, node, block, pxPerMs);
    }

    // Whole-file peak cache for waveform-in-block rendering (timeline doc Phase C). Keyed by source
    // path; null = extraction failed (don't retry every frame). Extraction runs off-thread via the
    // Preview tab's WaveformExtractor; partial snapshots repaint left-to-right. All dictionary access
    // is UI-thread only (extraction results marshal back via Dispatcher.UIThread.Post).
    private readonly Dictionary<string, float[]?> _waveformPeaks = new();
    private readonly HashSet<string> _waveformLoading = new();
    private readonly System.Threading.CancellationTokenSource _waveformCts = new();
    private long _lastWaveformPartialInvalidate;

    /// <summary>Waveform inside a media block: the whole-file peaks sliced to the trimmed window
    /// (<see cref="TimelineMath.WaveformWindow"/>), drawn as centered vertical bars every 2 px at low
    /// opacity so fades and the envelope overlay stay readable. File-backed sources only; extraction
    /// kicks off on first sight of a path and repaints as snapshots arrive.</summary>
    private void RenderWaveform(DrawingContext ctx, CueNodeViewModel node, Rect block)
    {
        if (node.MediaSourceItem is not HaPlay.Models.FilePlaylistItem file
            || string.IsNullOrEmpty(file.Path)
            || node.DurationMs <= 0
            || block.Width < 8)
            return;

        if (!_waveformPeaks.TryGetValue(file.Path, out var peaks))
        {
            BeginWaveformExtraction(file.Path);
            return;
        }

        if (peaks is not { Length: > 0 })
            return;

        var (startFrac, endFrac) = TimelineMath.WaveformWindow(
            node.StartOffsetMs, node.EffectiveDurationMs, node.EndOffsetMs);
        var windowSpan = endFrac - startFrac;
        if (windowSpan <= 0)
            return;

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x48, 0xCF, 0xE8, 0xFF)), 1);
        var midY = block.Center.Y;
        var halfMax = block.Height / 2 - 2;
        for (var x = block.X + 1; x < block.Right - 1; x += 2)
        {
            var frac = startFrac + (x - block.X) / block.Width * windowSpan;
            var bucket = Math.Clamp((int)(frac * peaks.Length), 0, peaks.Length - 1);
            var half = Math.Max(0.5, peaks[bucket] * halfMax);
            ctx.DrawLine(pen, new Point(x, midY - half), new Point(x, midY + half));
        }
    }

    private void BeginWaveformExtraction(string path)
    {
        if (!_waveformLoading.Add(path))
            return;
        var ct = _waveformCts.Token;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            float[]? peaks = null;
            try
            {
                peaks = await HaPlay.Playback.WaveformExtractor.ExtractAsync(path, ct, partial =>
                {
                    // Throttled left-to-right fill-in (the extractor already rate-limits snapshots,
                    // this guard just coalesces bursts).
                    var now = Environment.TickCount64;
                    if (now - System.Threading.Interlocked.Read(ref _lastWaveformPartialInvalidate) < 150)
                        return;
                    System.Threading.Interlocked.Exchange(ref _lastWaveformPartialInvalidate, now);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested)
                            return;
                        _waveformPeaks[path] = partial;
                        InvalidateVisual();
                    });
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // canvas closed - drop the result
            }
            catch (Exception)
            {
                // Extraction failure = no waveform; the null cache entry below stops per-frame retries.
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested)
                    return;
                _waveformPeaks[path] = peaks;
                _waveformLoading.Remove(path);
                InvalidateVisual();
            });
        }, ct);
    }

    /// <summary>
    /// Volume-envelope overlay for one media block: the level curve sampled every ~4 px (plus every
    /// keyframe x, so vertices land exactly) through <see cref="TimelineMath.EnvelopeLevelDbAt"/> -
    /// non-linear segments draw the same gain-domain shape playback applies. In edit mode it adds the
    /// dotted 0 dB reference line and the keyframe dots (selected = ringed); an EMPTY envelope shows
    /// only a dashed unity line while editing, as the click target for the first point.
    /// </summary>
    private void RenderEnvelope(DrawingContext ctx, CueNodeViewModel node, Rect block, double pxPerMs)
    {
        var envelope = node.VolumeEnvelope;
        var editable = IsEditable && TimelineMath.IsTrimmable(node);
        if (envelope.Count == 0 && !editable)
            return;

        var lineColor = Color.FromArgb(0xE6, 0x7F, 0xD8, 0x62);
        if (envelope.Count == 0)
        {
            var flatPen = new Pen(
                new SolidColorBrush(Color.FromArgb(0x80, lineColor.R, lineColor.G, lineColor.B)), 1)
            { DashStyle = DashStyle.Dash };
            var unityY = TimelineMath.EnvelopeYForDb(block, 0);
            ctx.DrawLine(flatPen, new Point(block.X, unityY), new Point(block.Right, unityY));
            return;
        }

        if (editable)
        {
            var refPen = new Pen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), 1)
            { DashStyle = DashStyle.Dot };
            var refY = TimelineMath.EnvelopeYForDb(block, 0);
            ctx.DrawLine(refPen, new Point(block.X, refY), new Point(block.Right, refY));
        }

        var xs = new List<double>();
        for (var x = block.X; x < block.Right; x += 4)
            xs.Add(x);
        xs.Add(block.Right);
        foreach (var point in envelope)
        {
            var px = block.X + Math.Max(0, point.TimeMs) * pxPerMs;
            if (px <= block.Right)
                xs.Add(px);
        }
        xs.Sort();

        var line = new List<Point>(xs.Count);
        foreach (var x in xs)
        {
            var levelDb = TimelineMath.EnvelopeLevelDbAt(envelope, (x - block.X) / pxPerMs);
            line.Add(new Point(x, TimelineMath.EnvelopeYForDb(block, levelDb)));
        }
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(lineColor), 1.5), new PolylineGeometry(line, false));

        if (!editable)
            return;

        // KNOWN LIMITATION (not fixable from here): TimelineMath.EnvelopePointCenter deliberately
        // clamps a keyframe authored past the trimmed-in right edge onto that edge - a contract
        // TimelineEnvelopeMathTests.EnvelopePointCenter_ClampsToBlockRightEdge pins - so after a
        // right-edge trim SEVERAL out-of-range keyframes can land on the same pixel column. They then
        // draw as one dot, and TimelineMath.EnvelopePointHit's nearest-point search resolves the tie to
        // the LAST of them, so only that one can be dragged back into range (repeated Delete does peel
        // them off one at a time, since each removal exposes the next). Fixing it means fanning the
        // clamped points apart in BOTH EnvelopePointCenter and EnvelopePointHit so drawing and hit
        // testing stay in agreement - i.e. in TimelineMath, alongside its tests, not here: duplicating
        // the geometry canvas-side would put the renderer and the shared math permanently out of sync.
        var dotBrush = new SolidColorBrush(lineColor);
        var selectedPen = new Pen(Brushes.White, 1.5);
        for (var i = 0; i < envelope.Count; i++)
        {
            var center = TimelineMath.EnvelopePointCenter(block, envelope[i], pxPerMs);
            var selected = ReferenceEquals(node, _selectedEnvelopeNode) && i == _selectedEnvelopeIndex;
            var radius = TimelineMath.EnvelopePointRadiusPx + (selected ? 1.5 : 0);
            ctx.DrawEllipse(dotBrush, selected ? selectedPen : null, center, radius, radius);
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

    private void RenderMarker(
        DrawingContext ctx, CueNodeViewModel node, int laneIndex, double width, double pxPerMs, Color color)
    {
        var c = TimelineMath.MarkerCenter(laneIndex, TimelineMath.BlockStartMs(node), pxPerMs);
        const double r = TimelineMath.MarkerHalfPx;
        var diamond = new PolylineGeometry(
            [new Point(c.X, c.Y - r), new Point(c.X + r, c.Y), new Point(c.X, c.Y + r), new Point(c.X - r, c.Y)], true);
        ctx.DrawGeometry(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(0xFF, color.R, color.G, color.B))), diamond);

        var label = $"{node.Number} {node.Label}".Trim();
        if (label.Length > 0)
        {
            // Bounded exactly like the block label above: an unbounded FormattedText let a long
            // comment/marker name draw straight past the measured extent (and, on the last lane,
            // outside the canvas entirely).
            var labelX = c.X + r + 4;
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)))
            {
                MaxTextWidth = Math.Max(1, width - labelX - 4),
                MaxLineCount = 1,
            };
            ctx.DrawText(text, new Point(labelX, c.Y - text.Height / 2));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEditable)
            return;
        Focus(); // Delete/Backspace target the selected envelope point.

        var p = e.GetPosition(this);
        var lanes = LaneNodes();
        var pxPerMs = PixelsPerMs;

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            // Envelope-line right-click (curve cycling) wins; otherwise a right-click on a media
            // block's body asks the window for the block context menu ("Duck under…").
            if (ShowEnvelopes && TryCycleEnvelopeCurve(lanes, pxPerMs, p))
            {
                e.Handled = true;
                return;
            }

            var contextNode = HitTestMediaBlockBody(lanes, pxPerMs, p);
            if (contextNode is not null)
            {
                BlockContextRequested?.Invoke(this, new TimelineBlockContextEventArgs(contextNode, p));
                e.Handled = true;
            }
            return;
        }

        for (var i = 0; i < lanes.Count; i++)
        {
            var node = lanes[i];
            TimelineHitKind kind;
            if (TimelineMath.IsMarker(node))
            {
                kind = TimelineMath.MarkerContains(TimelineMath.MarkerCenter(i, TimelineMath.BlockStartMs(node), pxPerMs), p)
                    ? TimelineHitKind.Marker
                    : TimelineHitKind.None;
            }
            else
            {
                var block = TimelineMath.BlockRect(i, TimelineMath.BlockStartMs(node), TimelineMath.BlockDurationMs(node), pxPerMs);
                kind = TimelineMath.HitTestBlock(block, node.FadeInMs, node.FadeOutMs, pxPerMs, p, TimelineMath.IsTrimmable(node));

                // Envelope hits beat the edge grips and the body (points are precise targets) but
                // never the corner fade handles.
                if (CanEditEnvelope(node)
                    && kind is not (TimelineHitKind.FadeInHandle or TimelineHitKind.FadeOutHandle))
                {
                    var envelope = node.VolumeEnvelope;
                    var pointIndex = TimelineMath.EnvelopePointHit(block, envelope, pxPerMs, p);
                    if (pointIndex < 0 && kind == TimelineHitKind.Block
                        && TimelineMath.EnvelopeLineHit(block, envelope, pxPerMs, p))
                    {
                        // Click ON the line (not on a point): add a keyframe at that time, sitting on
                        // the line so nothing jumps, then drag it from here.
                        var timeMs = TimelineMath.EnvelopeTimeForX(block, p.X, pxPerMs, node.EffectiveDurationMs);
                        var levelDb = RoundLevel(TimelineMath.EnvelopeLevelDbAt(envelope, timeMs));
                        pointIndex = TimelineMath.EnvelopeInsertIndex(envelope, timeMs);
                        var added = new List<CueAutomationPoint>(envelope);
                        added.Insert(pointIndex, new CueAutomationPoint { TimeMs = timeMs, LevelDb = levelDb });
                        node.VolumeEnvelope = added;
                    }

                    if (pointIndex >= 0)
                    {
                        kind = TimelineHitKind.EnvelopePoint;
                        _dragEnvelopeIndex = pointIndex;
                        SelectEnvelopePoint(node, pointIndex);
                    }
                }
            }

            if (kind == TimelineHitKind.None)
                continue;

            if (kind != TimelineHitKind.EnvelopePoint)
                ClearEnvelopeSelection();
            _drag = node;
            _dragKind = kind;
            _dragLaneIndex = i;
            _dragStartMs = TimelineMath.BlockStartMs(node); // block coordinates (audible start)
            _dragStartOffsetMs = Math.Max(0, node.StartOffsetMs);
            _dragEndOffsetMs = Math.Max(0, node.EndOffsetMs);
            _dragBlockDurationMs = TimelineMath.BlockDurationMs(node);
            _grabOffsetMs = TimelineMath.MsForX(p.X, pxPerMs) - _dragStartMs;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        ClearEnvelopeSelection(); // clicked empty canvas
    }

    /// <summary>Raised (edit mode only) when a right-click lands on a trimmable media block's body
    /// without hitting the envelope line - the window shows the block's context menu (Phase D
    /// "Duck under…"). The position is in canvas coordinates.</summary>
    public event EventHandler<TimelineBlockContextEventArgs>? BlockContextRequested;

    /// <summary>The topmost trimmable media block whose body contains <paramref name="p"/> - the
    /// duck helper needs a probed trimmed span to clamp the dip against, same gate as the envelope
    /// and trim handles.</summary>
    private CueNodeViewModel? HitTestMediaBlockBody(List<CueNodeViewModel> lanes, double pxPerMs, Point p)
    {
        for (var i = 0; i < lanes.Count; i++)
        {
            var node = lanes[i];
            if (TimelineMath.IsMarker(node) || !TimelineMath.IsTrimmable(node))
                continue;
            var block = TimelineMath.BlockRect(
                i, TimelineMath.BlockStartMs(node), TimelineMath.BlockDurationMs(node), pxPerMs);
            if (block.Contains(p))
                return node;
        }
        return null;
    }

    /// <summary>Envelope editing needs a media block whose trimmed span is known - same probe gate as
    /// the trim/fade handles (the envelope time range has nothing to clamp against otherwise).</summary>
    private bool CanEditEnvelope(CueNodeViewModel node) =>
        ShowEnvelopes && node.Kind == CueNodeKind.Media && TimelineMath.IsTrimmable(node);

    private static double RoundLevel(double levelDb) =>
        Math.Round(levelDb / TimelineMath.EnvelopeLevelStepDb) * TimelineMath.EnvelopeLevelStepDb;

    /// <summary>Right-click on the envelope line: cycle that segment's <c>CurveToNext</c>
    /// (Linear → EqualPower → Exponential → SCurve) and flash the new curve's name - same enum names
    /// the drawer's fade-curve combos display. The flat lead-in/tail have no segment to curve.</summary>
    private bool TryCycleEnvelopeCurve(List<CueNodeViewModel> lanes, double pxPerMs, Point p)
    {
        for (var i = 0; i < lanes.Count; i++)
        {
            var node = lanes[i];
            if (!CanEditEnvelope(node))
                continue;
            var block = TimelineMath.BlockRect(i, TimelineMath.BlockStartMs(node), TimelineMath.BlockDurationMs(node), pxPerMs);
            var envelope = node.VolumeEnvelope;
            if (!TimelineMath.EnvelopeLineHit(block, envelope, pxPerMs, p))
                continue;

            var segment = TimelineMath.EnvelopeSegmentAt(envelope, (p.X - block.X) / pxPerMs);
            if (segment < 0)
                return false;
            var next = TimelineMath.NextCurve(envelope[segment].CurveToNext);
            var updated = new List<CueAutomationPoint>(envelope);
            updated[segment] = envelope[segment] with { CurveToNext = next };
            node.VolumeEnvelope = updated;
            ShowReadout(next.ToString(), new Point(p.X + 10, p.Y - 22), transient: true);
            return true;
        }
        return false;
    }

    private void SelectEnvelopePoint(CueNodeViewModel node, int index)
    {
        _selectedEnvelopeNode = node;
        _selectedEnvelopeIndex = index;
        InvalidateVisual();
    }

    private void ClearEnvelopeSelection()
    {
        if (_selectedEnvelopeNode is null)
            return;
        _selectedEnvelopeNode = null;
        _selectedEnvelopeIndex = -1;
        InvalidateVisual();
    }

    private void ShowReadout(string text, Point anchor, bool transient)
    {
        _readoutText = text;
        _readoutAnchor = anchor;
        _readoutClearTimer.Stop();
        if (transient)
            _readoutClearTimer.Start();
        InvalidateVisual();
    }

    private void ClearReadout()
    {
        _readoutClearTimer.Stop();
        if (_readoutText is null)
            return;
        _readoutText = null;
        InvalidateVisual();
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
        var pointer = e.GetPosition(this);
        var pointerMs = TimelineMath.MsForX(pointer.X, pxPerMs);

        switch (_dragKind)
        {
            case TimelineHitKind.Block:
            case TimelineHitKind.Marker:
            {
                // Drag operates on the BLOCK (audible) start; the authored TimelineStartMs is the
                // block start minus the cue's own pre-wait - floored at 0, so a block can never sit
                // left of its pre-wait span.
                var snapped = TimelineMath.Snap(
                    pointerMs - _grabOffsetMs, SnapEnabled, GridMs, EdgeCandidates(_drag), pxPerMs);
                _drag.TimelineStartMs = Math.Max(0, snapped - Math.Max(0, _drag.PreWaitMs));
                break;
            }

            case TimelineHitKind.LeftEdge:
            {
                // Content-anchored trim: the media keeps its absolute position; the block's left edge
                // slides over it, so TimelineStartMs and StartOffsetMs move together. All in block
                // (audible-start) coordinates; the pre-wait floor keeps TimelineStartMs non-negative.
                var preWait = Math.Max(0, _drag.PreWaitMs);
                var contentMin = Math.Max(preWait, _dragStartMs - _dragStartOffsetMs);
                var contentMax = _dragStartMs + _dragBlockDurationMs - MinEffectiveMs;
                var newStart = Math.Clamp(
                    TimelineMath.Snap(pointerMs, SnapEnabled, GridMs, EdgeCandidates(_drag), pxPerMs),
                    contentMin, contentMax);
                _drag.StartOffsetMs = _dragStartOffsetMs + (newStart - _dragStartMs);
                _drag.TimelineStartMs = newStart - preWait;
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
                _drag.FadeInMs = Math.Clamp((int)Math.Round(pointerMs - TimelineMath.BlockStartMs(_drag)), 0, span);
                break;
            }

            case TimelineHitKind.FadeOutHandle:
            {
                var span = TimelineMath.BlockDurationMs(_drag);
                var end = TimelineMath.BlockStartMs(_drag) + span;
                _drag.FadeOutMs = Math.Clamp((int)Math.Round(end - pointerMs), 0, span);
                break;
            }

            case TimelineHitKind.EnvelopePoint:
            {
                var envelope = _drag.VolumeEnvelope;
                if (_dragEnvelopeIndex < 0 || _dragEnvelopeIndex >= envelope.Count)
                    break;

                // Time clamps between the neighbors and the trimmed clip; level clamps −60..+12.
                // The record is immutable - every step writes a NEW list through the VM.
                var block = TimelineMath.BlockRect(
                    _dragLaneIndex, TimelineMath.BlockStartMs(_drag), TimelineMath.BlockDurationMs(_drag), pxPerMs);
                var timeMs = TimelineMath.EnvelopeClampDragTime(
                    envelope, _dragEnvelopeIndex, (pointer.X - block.X) / pxPerMs, _drag.EffectiveDurationMs);
                var levelDb = RoundLevel(TimelineMath.EnvelopeDbForY(block, pointer.Y));
                var updated = new List<CueAutomationPoint>(envelope);
                updated[_dragEnvelopeIndex] = envelope[_dragEnvelopeIndex] with { TimeMs = timeMs, LevelDb = levelDb };
                _drag.VolumeEnvelope = updated;

                var center = TimelineMath.EnvelopePointCenter(block, updated[_dragEnvelopeIndex], pxPerMs);
                ShowReadout(
                    $"{TimelineMath.FormatDbLabel(levelDb)} · {TimelineMath.FormatRulerLabel(timeMs)}",
                    new Point(center.X + 10, center.Y - 22), transient: false);
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
            if (_dragKind == TimelineHitKind.EnvelopePoint)
                ClearReadout(); // the drag readout; the point stays selected for Delete
            _drag = null;
            _dragKind = TimelineHitKind.None;
            _dragEnvelopeIndex = -1;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    /// <summary>Delete/Backspace removes the selected envelope point (selection set by clicking or
    /// adding a point; the canvas focuses itself on press so the keys land here).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEditable || e.Key is not (Key.Delete or Key.Back))
            return;
        var node = _selectedEnvelopeNode;
        if (node is null || _selectedEnvelopeIndex < 0 || _selectedEnvelopeIndex >= node.VolumeEnvelope.Count)
            return;

        var updated = new List<CueAutomationPoint>(node.VolumeEnvelope);
        updated.RemoveAt(_selectedEnvelopeIndex);
        node.VolumeEnvelope = updated;
        ClearEnvelopeSelection();
        e.Handled = true;
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
            var envelopeLine = false;
            if (TimelineMath.IsMarker(node))
            {
                kind = TimelineMath.MarkerContains(TimelineMath.MarkerCenter(i, TimelineMath.BlockStartMs(node), pxPerMs), p)
                    ? TimelineHitKind.Marker
                    : TimelineHitKind.None;
            }
            else
            {
                var block = TimelineMath.BlockRect(i, TimelineMath.BlockStartMs(node), TimelineMath.BlockDurationMs(node), pxPerMs);
                kind = TimelineMath.HitTestBlock(block, node.FadeInMs, node.FadeOutMs, pxPerMs, p, TimelineMath.IsTrimmable(node));

                // Mirror the press-time priority: envelope points (and the add-a-point line) win over
                // edge grips and the body, never over the fade handles.
                if (CanEditEnvelope(node)
                    && kind is not (TimelineHitKind.FadeInHandle or TimelineHitKind.FadeOutHandle))
                {
                    if (TimelineMath.EnvelopePointHit(block, node.VolumeEnvelope, pxPerMs, p) >= 0)
                        kind = TimelineHitKind.EnvelopePoint;
                    else if (kind == TimelineHitKind.Block
                             && TimelineMath.EnvelopeLineHit(block, node.VolumeEnvelope, pxPerMs, p))
                        envelopeLine = true;
                }
            }

            if (kind == TimelineHitKind.None)
                continue;

            Cursor = kind switch
            {
                TimelineHitKind.LeftEdge or TimelineHitKind.RightEdge => new Cursor(StandardCursorType.SizeWestEast),
                TimelineHitKind.Block when envelopeLine => new Cursor(StandardCursorType.Cross),
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
            var start = TimelineMath.BlockStartMs(node);
            edges.Add(start);
            var duration = TimelineMath.IsMarker(node) ? 0 : TimelineMath.BlockDurationMs(node);
            if (duration > 0)
                edges.Add(start + duration);
        }
        return edges;
    }
}

/// <summary>A right-clicked media block on the timeline canvas (see
/// <see cref="TimelineCanvas.BlockContextRequested"/>); <see cref="Position"/> is in canvas
/// coordinates.</summary>
public sealed class TimelineBlockContextEventArgs(CueNodeViewModel node, Point position) : EventArgs
{
    public CueNodeViewModel Node { get; } = node;

    public Point Position { get; } = position;
}
