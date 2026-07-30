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
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Controls;

/// <summary>
/// Visual editor for a composition's multi-output layout. Draws the composition canvas (aspect-correct) with
/// each bound physical output as a draggable, resizable box positioned from its normalized <em>source
/// slice</em> (the part of the canvas that output displays). Dragging the body moves it; the bottom-right
/// handle resizes it; clicking selects it. Overlapping boxes are blend zones (their translucent fills add up);
/// canvas not covered by any box is a gap (the dark background shows through). All state lives on the bound
/// <see cref="OutputLayoutItemViewModel"/>s - this control only edits their Src rectangle.
/// </summary>
public sealed class OutputLayoutCanvas : Control
{
    private const double Pad = 8;
    private const double HandleSize = 12;

    // Distinct, theme-readable hues keyed by OutputLayoutItemViewModel.ColorIndex (wraps).
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x4F, 0x9C, 0xFF), Color.FromRgb(0x57, 0xC7, 0x85), Color.FromRgb(0xFF, 0xB3, 0x4D),
        Color.FromRgb(0xE0, 0x6C, 0x9C), Color.FromRgb(0x9B, 0x8C, 0xFF), Color.FromRgb(0x52, 0xC4, 0xC9),
        Color.FromRgb(0xD9, 0x6A, 0x6A), Color.FromRgb(0xA9, 0xC4, 0x4F),
    ];

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<OutputLayoutCanvas, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<OutputLayoutItemViewModel?> SelectedItemProperty =
        AvaloniaProperty.Register<OutputLayoutCanvas, OutputLayoutItemViewModel?>(
            nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Canvas aspect ratio (width / height). Defaults to 16:9.</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<OutputLayoutCanvas, double>(nameof(AspectRatio), 16.0 / 9.0);

    /// <summary>Composition canvas pixel size - drives 1-pixel keyboard nudges. 0 = use a small relative step.</summary>
    public static readonly StyledProperty<int> CanvasWidthProperty =
        AvaloniaProperty.Register<OutputLayoutCanvas, int>(nameof(CanvasWidth));

    public static readonly StyledProperty<int> CanvasHeightProperty =
        AvaloniaProperty.Register<OutputLayoutCanvas, int>(nameof(CanvasHeight));

    /// <summary>Pull a dragged section's edges and centre onto the output's edges and centre (see
    /// <see cref="PlacementSnapMath"/>). On by default - centring is how a composition gets letterboxed
    /// inside an output of a different aspect, and "dead centre" is a single exact value that is
    /// impractical to hit by hand. Hold Ctrl to override a guide for one drag.</summary>
    public static readonly StyledProperty<bool> SnapToEdgesProperty =
        AvaloniaProperty.Register<OutputLayoutCanvas, bool>(nameof(SnapToEdges), true);

    public bool SnapToEdges
    {
        get => GetValue(SnapToEdgesProperty);
        set => SetValue(SnapToEdgesProperty, value);
    }

    private readonly List<OutputLayoutItemViewModel> _watched = new();
    private OutputLayoutItemViewModel? _drag;
    private bool _resizing;
    private Point _grabNorm; // pointer offset within the box, normalized, at drag start
    private double _dragAspect = 1; // normalized source-rect aspect for the output's physical pixel ratio

    static OutputLayoutCanvas()
    {
        AffectsRender<OutputLayoutCanvas>(SelectedItemProperty, AspectRatioProperty, SnapToEdgesProperty);
    }

    public OutputLayoutCanvas()
    {
        Focusable = true; // accept keyboard focus so arrow keys nudge the selected output
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public OutputLayoutItemViewModel? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    public int CanvasWidth
    {
        get => GetValue(CanvasWidthProperty);
        set => SetValue(CanvasWidthProperty, value);
    }

    public int CanvasHeight
    {
        get => GetValue(CanvasHeightProperty);
        set => SetValue(CanvasHeightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCol)
                oldCol.CollectionChanged -= OnItemsCollectionChanged;
            if (change.NewValue is INotifyCollectionChanged newCol)
                newCol.CollectionChanged += OnItemsCollectionChanged;
            ResubscribeItems();
            InvalidateVisual();
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResubscribeItems();
        InvalidateVisual();
    }

    private void ResubscribeItems()
    {
        foreach (var i in _watched)
            i.PropertyChanged -= OnItemPropertyChanged;
        _watched.Clear();
        if (Items is not null)
        {
            foreach (var i in Items.OfType<OutputLayoutItemViewModel>())
            {
                i.PropertyChanged += OnItemPropertyChanged;
                _watched.Add(i);
            }
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    /// <summary>Fraction of the control reserved AROUND the canvas on each side, so a section dragged out of
    /// bounds is visible instead of being clipped at the control edge (which reads as a resize, and leaves
    /// nothing to grab). Same rationale as CompositionPlacementCanvas.WorkAreaMargin.</summary>
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

    private static Rect BoxRect(Rect canvas, OutputLayoutItemViewModel i) => new(
        canvas.X + i.SrcX * canvas.Width,
        canvas.Y + i.SrcY * canvas.Height,
        Math.Max(1, i.SrcWidth * canvas.Width),
        Math.Max(1, i.SrcHeight * canvas.Height));

    private static Color ColorFor(OutputLayoutItemViewModel i) =>
        Palette[((i.ColorIndex % Palette.Length) + Palette.Length) % Palette.Length];

    public override void Render(DrawingContext ctx)
    {
        var canvas = CanvasRect();
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 18)), canvas);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80))), canvas);

        if (Items is null) return;

        // A section may now overhang the canvas, so boxes are clipped to this CONTROL - visible in the work
        // area margin, never painting over the surrounding dialog.
        using var clip = ctx.PushClip(new Rect(Bounds.Size));
        foreach (var i in Items.OfType<OutputLayoutItemViewModel>())
        {
            var box = BoxRect(canvas, i);
            var selected = ReferenceEquals(i, SelectedItem);
            var hue = ColorFor(i);
            // Translucent fill so overlapping outputs visibly blend; opaque-ish border for the edge.
            // Two passes so "outside the canvas" is unmistakable: ghosted where it overhangs, full strength
            // inside. The overhang is authored but never rendered, so it must not look identical to the rest.
            var boxFill = new SolidColorBrush(
                Color.FromArgb(selected ? (byte)0x66 : (byte)0x3A, hue.R, hue.G, hue.B));
            var boxPen = new Pen(new SolidColorBrush(hue), selected ? 2 : 1);
            if (!canvas.Contains(box))
            {
                using var ghost = ctx.PushOpacity(0.35);
                ctx.FillRectangle(boxFill, box);
                ctx.DrawRectangle(null, boxPen, box);
            }

            using (ctx.PushClip(canvas))
            {
                ctx.FillRectangle(boxFill, box);
                ctx.DrawRectangle(null, boxPen, box);
            }

            var label = i.DisplayName;
            if (!string.IsNullOrEmpty(label))
            {
                var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 12, Brushes.White);
                if (box.Width > text.Width + 6 && box.Height > text.Height + 4)
                    ctx.DrawText(text, new Point(box.X + 4, box.Y + 3));
            }

            if (selected)
            {
                var handle = new Rect(box.Right - HandleSize, box.Bottom - HandleSize, HandleSize, HandleSize);
                ctx.FillRectangle(new SolidColorBrush(hue), handle);
            }
        }

        // The canvas outline goes on TOP: it is what a section is being placed against (and what letterbox
        // bars are measured from), so an overhanging section must not hide it.
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0xA0, 0xA0, 0xA0)), 1), canvas);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var canvas = CanvasRect();
        if (canvas.Width <= 0 || Items is null) return;
        var pt = e.GetPosition(this);

        // Last item drawn is visually on top; hit-test in reverse so the topmost wins.
        foreach (var i in Items.OfType<OutputLayoutItemViewModel>().Reverse())
        {
            var box = BoxRect(canvas, i);
            var handle = new Rect(box.Right - HandleSize, box.Bottom - HandleSize, HandleSize, HandleSize);
            var onHandle = ReferenceEquals(i, SelectedItem) && handle.Contains(pt);
            if (onHandle || box.Contains(pt))
            {
                SelectedItem = i;
                _drag = i;
                _resizing = onHandle;
                _dragAspect = NormalizedAspectForOutput(i);
                _grabNorm = new Point((pt.X - box.X) / canvas.Width, (pt.Y - box.Y) / canvas.Height);
                e.Pointer.Capture(this);
                Focus(); // take keyboard focus so arrow keys nudge this output
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }
    }

    /// <summary>Cursor feedback before the press - see CompositionPlacementCanvas.UpdateHoverCursor for why
    /// (a full-canvas section's resize handle sits exactly where you reach to push it out to the right).</summary>
    private void UpdateHoverCursor(Point pt)
    {
        var canvas = CanvasRect();
        if (canvas.Width <= 0 || Items is null)
        {
            Cursor = Cursor.Default;
            return;
        }

        foreach (var i in Items.OfType<OutputLayoutItemViewModel>().Reverse())
        {
            var box = BoxRect(canvas, i);
            if (ReferenceEquals(i, SelectedItem)
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

        // Ctrl suppresses snapping for one drag - place it exactly here without toggling the option off.
        var snap = SnapToEdges && !e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (_resizing)
        {
            var w = PlacementSnapMath.SnapResizeAxis(
                _drag.SrcX, nx - _drag.SrcX, PlacementSnapMath.Threshold(canvas.Width), snap);
            var h = PlacementSnapMath.SnapResizeAxis(
                _drag.SrcY, ny - _drag.SrcY, PlacementSnapMath.Threshold(canvas.Height), snap);

            // Aspect-locked by default - keep the output's proportions so the output isn't distorted while
            // fine-tuning. Uncheck the item's aspect lock, or hold Shift, to resize width/height freely.
            if (_drag.AspectLocked && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _dragAspect > 0)
            {
                h = Math.Clamp(w / _dragAspect, PlacementSnapMath.MinSize, PlacementSnapMath.MaxSize);
                w = Math.Clamp(h * _dragAspect, PlacementSnapMath.MinSize, PlacementSnapMath.MaxSize);
            }

            _drag.SetSrcRect(_drag.SrcX, _drag.SrcY, w, h);
        }
        else
        {
            var moved = PlacementSnapMath.SnapAndClampMove(
                nx - _grabNorm.X, ny - _grabNorm.Y, _drag.SrcWidth, _drag.SrcHeight, canvas, snap);
            _drag.SetSrcRect(moved.X, moved.Y, _drag.SrcWidth, _drag.SrcHeight);
        }

        InvalidateVisual(); // guide lines follow the drag
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (SelectedItem is not { } item)
            return;

        // Fine positioning: one canvas pixel per press (×10 with Shift for coarse). Falls back to a small
        // relative step when the canvas pixel size isn't supplied.
        var stepX = CanvasWidth > 0 ? 1.0 / CanvasWidth : 0.002;
        var stepY = CanvasHeight > 0 ? 1.0 / CanvasHeight : 0.002;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { stepX *= 10; stepY *= 10; }

        double dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Left: dx = -stepX; break;
            case Key.Right: dx = stepX; break;
            case Key.Up: dy = -stepY; break;
            case Key.Down: dy = stepY; break;
            default: return;
        }

        item.SetSrcRect(item.SrcX + dx, item.SrcY + dy, item.SrcWidth, item.SrcHeight);
        e.Handled = true;
    }

    private static double NormalizedAspectForOutput(OutputLayoutItemViewModel item)
    {
        var canvasAspect = item.CanvasHeight > 0 ? item.CanvasWidth / (double)item.CanvasHeight : 1.0;
        return canvasAspect > 0 ? item.OutputAspectRatio / canvasAspect : 1.0;
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
