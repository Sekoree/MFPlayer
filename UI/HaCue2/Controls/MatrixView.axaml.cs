using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Controls;

/// <summary>Renders a gain matrix: logical outputs across, sources or device channels down.</summary>
public partial class MatrixView : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<MatrixColumn>> ColumnsProperty =
        AvaloniaProperty.Register<MatrixView, IReadOnlyList<MatrixColumn>>(nameof(Columns), []);

    public static readonly StyledProperty<IReadOnlyList<MatrixRow>> RowsProperty =
        AvaloniaProperty.Register<MatrixView, IReadOnlyList<MatrixRow>>(nameof(Rows), []);

    /// <summary>
    /// Width of the row-label column. It differs between the two uses on purpose: a cue's sends are
    /// labelled "Src L", the project patch is labelled "18i20 · Out 3" — the plan requires the patch
    /// label to carry BOTH the stable line alias and the real channel number, which needs the room.
    /// </summary>
    public static readonly StyledProperty<double> RowHeaderWidthProperty =
        AvaloniaProperty.Register<MatrixView, double>(nameof(RowHeaderWidth), 96d);

    public MatrixView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

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
}
