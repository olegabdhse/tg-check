using System.Windows;
using System.Windows.Controls;

namespace TgCheck.App.Controls;

/// <summary>
/// Сетка из квадратов в три колонки. С <see cref="Big"/> первая плитка занимает четыре клетки, две следующие стоят рядом с ней
/// (<see cref="Mirror"/> — большая справа). Неполная последняя строка растягивается на всю ширину: дырок не остаётся.
/// Показывается не больше <see cref="Limit"/> плиток.
/// </summary>
public sealed class MosaicPanel : Panel
{
    private const int Columns = 3;
    private const double Gap = 6;

    public static readonly DependencyProperty BigProperty = DependencyProperty.Register(nameof(Big), typeof(bool), typeof(MosaicPanel),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MirrorProperty = DependencyProperty.Register(nameof(Mirror), typeof(bool), typeof(MosaicPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty LimitProperty = DependencyProperty.Register(nameof(Limit), typeof(int), typeof(MosaicPanel),
        new FrameworkPropertyMetadata(9, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool Big { get => (bool)GetValue(BigProperty); set => SetValue(BigProperty, value); }
    public bool Mirror { get => (bool)GetValue(MirrorProperty); set => SetValue(MirrorProperty, value); }
    public int Limit { get => (int)GetValue(LimitProperty); set => SetValue(LimitProperty, value); }

    private static double CellSize(double width) => Math.Max(0, (width - Gap * (Columns - 1)) / Columns);

    // Место плитки при count видимых плитках в панели шириной width.
    private Rect Slot(int index, int count, double width)
    {
        var cell = CellSize(width);
        var step = cell + Gap;
        var bigSide = cell * 2 + Gap;
        if (Big && count >= 3 && index < 3)
        {
            if (index == 0) return new Rect(Mirror ? step : 0, 0, bigSide, bigSide);
            return new Rect(Mirror ? 0 : step * 2, (index - 1) * step, cell, cell);
        }
        // Обычные строки: после мозаики (две строки заняты большой плиткой) или с самого верха.
        var head = Big && count >= 3 ? 3 : 0;
        var firstRow = head > 0 ? 2 : 0;
        var position = index - head;
        var row = position / Columns;
        var inRow = Math.Min(Columns, count - head - row * Columns);
        var tile = (width - Gap * (inRow - 1)) / inRow;
        return new Rect(position % Columns * (tile + Gap), (firstRow + row) * step, tile, cell);
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 300 : available.Width;
        var count = Math.Min(InternalChildren.Count, Limit);
        double bottom = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (i >= count)
            {
                InternalChildren[i].Measure(new Size(0, 0));
                continue;
            }
            var slot = Slot(i, count, width);
            InternalChildren[i].Measure(slot.Size);
            bottom = Math.Max(bottom, slot.Bottom);
        }
        return new Size(width, bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Math.Min(InternalChildren.Count, Limit);
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(i < count ? Slot(i, count, finalSize.Width) : new Rect(0, 0, 0, 0));
        return finalSize;
    }
}
