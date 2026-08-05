using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using S.Media.Compositor;
using S.Media.Core.Video;

namespace HaCue2.Controls;

public sealed record WarpPointGesture(int Index, double OffsetX, double OffsetY);

/// <summary>
/// Direct mesh editor: the exact interpolated surface the GL compositor uses, with draggable handles.
/// Numeric fields remain beside it for precision and keyboard-only operation.
/// </summary>
public sealed class WarpMeshCanvas : Control
{
    private const double Padding = 16;
    private const double HandleRadius = 5.5;
    private int _dragged = -1;
    private IReadOnlyList<double>? _previewOffsets;

    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<WarpMeshCanvas, int>(nameof(Columns));
    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<WarpMeshCanvas, int>(nameof(Rows));
    public static readonly StyledProperty<IReadOnlyList<double>> OffsetsProperty =
        AvaloniaProperty.Register<WarpMeshCanvas, IReadOnlyList<double>>(nameof(Offsets), []);
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<WarpMeshCanvas, int>(nameof(SelectedIndex));

    static WarpMeshCanvas() =>
        AffectsRender<WarpMeshCanvas>(ColumnsProperty, RowsProperty, OffsetsProperty, SelectedIndexProperty);

    public WarpMeshCanvas() => Focusable = true;

    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public int Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public IReadOnlyList<double> Offsets { get => GetValue(OffsetsProperty); set => SetValue(OffsetsProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    public event EventHandler<WarpPointGesture>? PointMoved;
    public event EventHandler<int>? PointSelected;
    public event EventHandler? GestureCompleted;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 7, 12, 18)),
            new Pen(new SolidColorBrush(Color.FromArgb(130, 80, 100, 120))), Bounds);

        var mesh = BuildMesh(CurrentOffsets());
        if (mesh is null)
            return;

        var linePen = new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 157, 40)), 1);
        for (var row = 0; row < Rows; row++)
            DrawCurve(context, mesh, linePen, horizontal: true, row / (double)(Rows - 1));
        for (var column = 0; column < Columns; column++)
            DrawCurve(context, mesh, linePen, horizontal: false, column / (double)(Columns - 1));

        for (var index = 0; index < mesh.Points.Length; index++)
        {
            var point = mesh.Points[index];
            var centre = new Point(point.X, point.Y);
            var selected = index == SelectedIndex;
            context.DrawEllipse(
                selected ? Brushes.White : Brushes.Orange,
                new Pen(selected ? Brushes.Orange : Brushes.Black, selected ? 2 : 1),
                centre, selected ? 7 : HandleRadius, selected ? 7 : HandleRadius);
        }
    }

    private static void DrawCurve(
        DrawingContext context, WarpMesh mesh, Pen pen, bool horizontal, double fixedAxis)
    {
        Point? previous = null;
        var segments = Math.Max(8, (horizontal ? mesh.Columns - 1 : mesh.Rows - 1) * 8);
        for (var index = 0; index <= segments; index++)
        {
            var moving = index / (float)segments;
            var point = horizontal
                ? WarpMeshTessellator.Evaluate(mesh, moving, (float)fixedAxis)
                : WarpMeshTessellator.Evaluate(mesh, (float)fixedAxis, moving);
            var current = new Point(point.X, point.Y);
            if (previous is { } from)
                context.DrawLine(pen, from, current);
            previous = current;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (BuildMesh(CurrentOffsets()) is not { } mesh)
            return;

        var at = e.GetPosition(this);
        var closest = Enumerable.Range(0, mesh.Points.Length)
            .Select(index => (index, distance: Distance(mesh.Points[index], at)))
            .OrderBy(item => item.distance)
            .First();
        if (closest.distance > 13)
            return;

        _dragged = closest.index;
        _previewOffsets = CurrentOffsets().ToArray();
        SelectedIndex = _dragged;
        PointSelected?.Invoke(this, _dragged);
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragged < 0 || !ReferenceEquals(e.Pointer.Captured, this))
            return;
        MoveTo(_dragged, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragged < 0)
            return;
        e.Pointer.Capture(null);
        _dragged = -1;
        _previewOffsets = null;
        GestureCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var offsets = CurrentOffsets();
        if (Columns < 2 || Rows < 2 || offsets.Count != Columns * Rows * 2
            || SelectedIndex < 0 || SelectedIndex >= Columns * Rows
            || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? .05 : .005;
        var x = offsets[SelectedIndex * 2];
        var y = offsets[SelectedIndex * 2 + 1];
        x += e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 };
        y += e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 };
        PointMoved?.Invoke(this, new WarpPointGesture(SelectedIndex, Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1)));
        GestureCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void MoveTo(int index, Point point)
    {
        var width = Math.Max(1, Bounds.Width - Padding * 2);
        var height = Math.Max(1, Bounds.Height - Padding * 2);
        var column = index % Columns;
        var row = index / Columns;
        var baseX = column / (double)(Columns - 1);
        var baseY = row / (double)(Rows - 1);
        var offsetX = Math.Clamp((point.X - Padding) / width - baseX, -1, 1);
        var offsetY = Math.Clamp((point.Y - Padding) / height - baseY, -1, 1);

        if (_previewOffsets is double[] preview)
        {
            preview[index * 2] = offsetX;
            preview[index * 2 + 1] = offsetY;
            InvalidateVisual();
        }
        PointMoved?.Invoke(this, new WarpPointGesture(index, offsetX, offsetY));
    }

    private WarpMesh? BuildMesh(IReadOnlyList<double> offsets)
    {
        if (Columns < 2 || Rows < 2 || offsets.Count != Columns * Rows * 2
            || Bounds.Width <= Padding * 2 || Bounds.Height <= Padding * 2)
            return null;
        var width = Bounds.Width - Padding * 2;
        var height = Bounds.Height - Padding * 2;
        var points = new Vector2[Columns * Rows];
        for (var row = 0; row < Rows; row++)
        for (var column = 0; column < Columns; column++)
        {
            var index = row * Columns + column;
            points[index] = new Vector2(
                (float)(Padding + width * (column / (double)(Columns - 1) + offsets[index * 2])),
                (float)(Padding + height * (row / (double)(Rows - 1) + offsets[index * 2 + 1])));
        }
        return new WarpMesh(Columns, Rows, points);
    }

    private IReadOnlyList<double> CurrentOffsets() => _previewOffsets ?? Offsets;
    private static double Distance(Vector2 point, Point at) =>
        Math.Sqrt(Math.Pow(point.X - at.X, 2) + Math.Pow(point.Y - at.Y, 2));
}
