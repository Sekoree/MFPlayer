using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HaCue2.Controls;

/// <summary>
/// The faint reference grid behind every composition / mapping canvas (the mockup's
/// <c>.vidcanvas .grid</c>, an 8 × 6 division of the frame).
/// </summary>
/// <remarks>
/// A tiled <see cref="ImageBrush"/> would keep the cell size constant while the canvas resized, which
/// is wrong here: the grid means "eighths and sixths of the frame", so it must stay proportional. It
/// draws lines rather than a bitmap so a 1 px rule stays 1 px at any canvas size.
/// </remarks>
public class CanvasGrid : Control
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<CanvasGrid, int>(nameof(Columns), 8);

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<CanvasGrid, int>(nameof(Rows), 6);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<CanvasGrid, IBrush?>(nameof(Stroke));

    static CanvasGrid()
    {
        AffectsRender<CanvasGrid>(ColumnsProperty, RowsProperty, StrokeProperty);
    }

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Stroke is not { } stroke)
            return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        var pen = new Pen(stroke);

        for (var i = 1; i < Columns; i++)
        {
            var x = Math.Round(w * i / Columns) + 0.5;
            context.DrawLine(pen, new Point(x, 0), new Point(x, h));
        }

        for (var i = 1; i < Rows; i++)
        {
            var y = Math.Round(h * i / Rows) + 0.5;
            context.DrawLine(pen, new Point(0, y), new Point(w, y));
        }
    }
}
