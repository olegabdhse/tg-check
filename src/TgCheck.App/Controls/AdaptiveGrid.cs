using System.Windows;
using System.Windows.Controls;

namespace TgCheck.App.Controls;

/// <summary>
/// Сетка карточек под ширину окна: в ряду столько колонок, сколько помещается (не уже MinItemWidth),
/// а остаток ширины делится между карточками — справа не остаётся пустого места.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(172d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private (int Columns, double ItemWidth) Layout(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return (1, MinItemWidth);
        var columns = Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));
        return (columns, Math.Floor((width - Spacing * (columns - 1)) / columns));
    }

    protected override Size MeasureOverride(Size available)
    {
        var (columns, itemWidth) = Layout(available.Width);
        double height = 0, rowHeight = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (i % columns == columns - 1 || i == InternalChildren.Count - 1)
            {
                height += rowHeight + Spacing;
                rowHeight = 0;
            }
        }
        var width = double.IsInfinity(available.Width) ? columns * (itemWidth + Spacing) : available.Width;
        return new Size(width, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (columns, itemWidth) = Layout(final.Width);
        double y = 0, rowHeight = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var column = i % columns;
            if (column == 0 && i > 0) { y += rowHeight + Spacing; rowHeight = 0; }
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            child.Arrange(new Rect(column * (itemWidth + Spacing), y, itemWidth, child.DesiredSize.Height));
        }
        return final;
    }
}
