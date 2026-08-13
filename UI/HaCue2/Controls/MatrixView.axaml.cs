using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using HaCue2.ViewModels;

namespace HaCue2.Controls;

/// <summary>What the operator did to a cell.</summary>
public enum MatrixGestureKind
{
    /// <summary>A click with no drag: route the cell at unity, or un-route it if it was routed.</summary>
    Toggle,

    /// <summary>A vertical drag: move the gain by <see cref="MatrixGesture.DeltaDb"/>.</summary>
    Adjust,

    /// <summary>A right-click: mute or unmute, keeping the routing.</summary>
    Mute,
}

/// <summary>One edit to one cell, addressed by its position in the matrix.</summary>
/// <remarks>
/// By INDEX rather than by id: the two matrices this control serves address their cells differently
/// (the patch by line and channel, a cue's sends by source channel), and a control that knew which was
/// which would have to know what a patch is. The view-model maps indices back to the document, where
/// that knowledge belongs.
/// </remarks>
public sealed record MatrixGesture(int Row, int Column, MatrixGestureKind Kind, double DeltaDb = 0);

/// <summary>Renders a gain matrix and turns pointer gestures on it into edits.</summary>
public partial class MatrixView : UserControl
{
    /// <summary>
    /// How far a drag moves the gain. 0.25 dB per pixel puts the whole −60…+12 range in ~290 px of
    /// travel: fine enough to land on a value, coarse enough to cross the range without a second grab.
    /// </summary>
    private const double DbPerPixel = 0.25;

    /// <summary>Movement under this is a click, not a drag - hands are not steady on a trackpad.</summary>
    private const double DragThreshold = 3;

    /// <summary>
    /// The cell being dragged, as COORDINATES rather than as the Border that was under the pointer.
    /// </summary>
    /// <remarks>
    /// Every adjustment refreshes the pane, which rebuilds the matrix and lets the items control
    /// recycle its containers. The Border captured at press then holds a different item's DataContext,
    /// or none - so a drag emitted its first step and went dead, which reads as one big jump per grab.
    /// A row and a column cannot go stale.
    /// </remarks>
    private (int Row, int Column)? _pressed;

    private Point _pressedAt;
    private double _appliedDb;
    private bool _dragging;

    public static readonly StyledProperty<IReadOnlyList<MatrixColumn>> ColumnsProperty =
        AvaloniaProperty.Register<MatrixView, IReadOnlyList<MatrixColumn>>(nameof(Columns), []);

    public static readonly StyledProperty<IReadOnlyList<MatrixRow>> RowsProperty =
        AvaloniaProperty.Register<MatrixView, IReadOnlyList<MatrixRow>>(nameof(Rows), []);

    /// <summary>
    /// Width of the row-label column. It differs between the two uses on purpose: a cue's sends are
    /// labelled "Src L", the project patch is labelled "18i20 · Out 3" - the plan requires the patch
    /// label to carry BOTH the stable line alias and the real channel number, which needs the room.
    /// </summary>
    /// <summary>The resource the row templates read the header width from.</summary>
    /// <remarks>
    /// A resource rather than a <c>RelativeSource AncestorType</c> binding from inside the item
    /// template. An ancestor lookup runs while a container is still detached - during realisation and
    /// again while recycling - and Avalonia reports "Ancestor not found" every time, which fills the
    /// debug output with errors for something that is merely not-yet-attached. A DynamicResource
    /// resolves through the same tree but DEFERS instead of failing.
    /// </remarks>
    public const string RowHeaderWidthKey = "MatrixRowHeaderWidth";

    public static readonly StyledProperty<double> RowHeaderWidthProperty =
        AvaloniaProperty.Register<MatrixView, double>(nameof(RowHeaderWidth), 96d);

    /// <summary>Whether pointer gestures edit the matrix. Off for a read-only view.</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<MatrixView, bool>(nameof(IsEditable), true);

    public MatrixView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        Resources[RowHeaderWidthKey] = RowHeaderWidth;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RowHeaderWidthProperty || change.Property == RowsProperty)
            UpdateRowHeaderWidth();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Rows are often assigned before the control can see the theme's font resources; re-measure
        // once they are reachable so the first layout already has the right width.
        UpdateRowHeaderWidth();
    }

    /// <summary>
    /// Publishes the row-label column's width: at least <see cref="RowHeaderWidth"/>, widened to fit
    /// the longest header.
    /// </summary>
    /// <remarks>
    /// The styled property is a MINIMUM, not the answer. The patch matrix labels rows
    /// "&lt;line name&gt; · Out &lt;n&gt;", and a real interface's name does not fit any fixed
    /// width - at 118 px "Ryzen HD Audio Controller Analog Stereo · Out 1" clipped to its first few
    /// glyphs, which is exactly the row the operator reads to tell one output from another. The
    /// corner spacer and every row share this resource, so the whole column widens together and the
    /// pane's horizontal ScrollViewer absorbs the extra.
    /// </remarks>
    private void UpdateRowHeaderWidth() =>
        Resources[RowHeaderWidthKey] = Math.Max(RowHeaderWidth, MeasuredRowHeaderWidth());

    private double MeasuredRowHeaderWidth()
    {
        var rows = Rows;
        if (rows is not { Count: > 0 })
            return 0;

        // The same face the `mxhead rowhead` style renders with (Chrome.axaml); the fallbacks only
        // matter before the control is attached, and attachment re-measures.
        var family = this.TryFindResource("MonoFont", out var f) && f is FontFamily mono
            ? mono
            : FontFamily.Default;
        var size = this.TryFindResource("FontSizeMono", out var s) && s is double points ? points : 10d;
        var typeface = new Typeface(family);

        var widest = 0d;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Header))
                continue;

            var formatted = new FormattedText(
                row.Header, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, size, Brushes.Black);
            // FormattedText does not apply the style's LetterSpacing (0.55/char); add it by hand.
            widest = Math.Max(widest, formatted.Width + row.Header.Length * 0.55);
        }

        // The header Border's own chrome around the text: 5+5 padding, the 1 px right rule, and two
        // spare so the last glyph never touches it.
        return widest == 0 ? 0 : Math.Ceiling(widest + 5 + 5 + 1 + 2);
    }

    /// <summary>Raised for every cell edit; the view-model decides what it means.</summary>
    public event EventHandler<MatrixGesture>? Gesture;

    public IReadOnlyList<MatrixColumn> Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public IReadOnlyList<MatrixRow> Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public double RowHeaderWidth
    {
        get => GetValue(RowHeaderWidthProperty);
        set => SetValue(RowHeaderWidthProperty, value);
    }

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEditable || CellUnder(e) is not { } cell)
            return;

        var point = e.GetCurrentPoint(this);

        // Right-click mutes immediately: it is a discrete state, so there is nothing to drag and
        // waiting for a release would only make it feel unresponsive.
        if (cell.DataContext is not MatrixCell data)
            return;

        if (point.Properties.IsRightButtonPressed)
        {
            Raise(data.Row, data.Column, MatrixGestureKind.Mute);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        _pressed = (data.Row, data.Column);
        _pressedAt = point.Position;
        _appliedDb = 0;
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } cell)
            return;

        var moved = _pressedAt.Y - e.GetPosition(this).Y;
        if (!_dragging && Math.Abs(moved) < DragThreshold)
            return;

        _dragging = true;

        // Emit the CHANGE since the last move, not the total: each step is a small coalescing edit, so
        // the whole drag collapses to one undo entry that reverts to where the grab started.
        var target = moved * DbPerPixel;
        var step = target - _appliedDb;
        _appliedDb = target;

        if (step != 0)
            Raise(cell.Row, cell.Column, MatrixGestureKind.Adjust, step);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is { } cell && !_dragging)
            Raise(cell.Row, cell.Column, MatrixGestureKind.Toggle);

        _pressed = null;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private Border? CellUnder(PointerEventArgs e) =>
        (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("mxcell"));

    /// <summary>Raises the gesture for the cell that was hit, using the coordinates it carries.</summary>
    private void Raise(int row, int column, MatrixGestureKind kind, double deltaDb = 0) =>
        Gesture?.Invoke(this, new MatrixGesture(row, column, kind, deltaDb));
}
