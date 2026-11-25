using System.Windows;
using System.Windows.Controls;

namespace BAM.Desktop.Controls;

internal sealed class ResponsivePanel : Panel
{
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsivePanel), new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => (int)value > 0);
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(ResponsivePanel), new FrameworkPropertyMetadata(260d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => (double)value > 0 && double.IsFinite((double)value));

    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    private double Gap => (double)FindResource("Bam.ColumnGap");

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MaxColumns * (ItemWidth + Gap) - Gap;
        var columns = Columns(width);
        var cellWidth = CellWidth(width, columns);
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(cellWidth, double.PositiveInfinity));

        var height = 0d;
        for (var start = 0; start < InternalChildren.Count; start += columns)
            height += RowHeight(start, columns) + (start > 0 ? Gap : 0);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var cellWidth = CellWidth(finalSize.Width, columns);
        var offset = 0d;
        for (var start = 0; start < InternalChildren.Count; start += columns)
        {
            var rowHeight = RowHeight(start, columns);
            for (var column = 0; column < columns && start + column < InternalChildren.Count; column++)
                InternalChildren[start + column].Arrange(new Rect(column * (cellWidth + Gap), offset, cellWidth, rowHeight));
            offset += rowHeight + Gap;
        }
        return finalSize;
    }

    private int Columns(double width) => Math.Clamp((int)Math.Floor((width + Gap) / (ItemWidth + Gap)), 1, MaxColumns);
    private double CellWidth(double width, int columns) => Math.Max(0, (width - (columns - 1) * Gap) / columns);

    private double RowHeight(int start, int columns)
    {
        var height = 0d;
        for (var index = start; index < InternalChildren.Count && index < start + columns; index++)
            height = Math.Max(height, InternalChildren[index].DesiredSize.Height);
        return height;
    }
}
