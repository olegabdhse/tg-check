using System.Windows;
using System.Windows.Controls;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Controls;

/// <summary>
/// Раскладка альбома как в Telegram: плитки разного размера по пропорциям фото/видео. Ряды одной высоты,
/// ширины в ряду пропорциональны снимкам; разбиение на ряды подбирается так, чтобы ряды были ровными.
/// Для 3–4 медиа с вертикальным первым — большой снимок слева, остальные столбиком справа.
/// </summary>
public sealed class AlbumPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(AlbumPanel),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private const double DefaultWidth = 363, MaxSingleHeight = 420;
    private List<Rect> _rects = [];

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? DefaultWidth : available.Width;
        var ratios = InternalChildren.Cast<UIElement>().Select(AspectOf).ToList();
        _rects = Layout(ratios, width, Spacing);
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(i < _rects.Count ? _rects[i].Size : new Size(0, 0));
        return new Size(width, _rects.Count == 0 ? 0 : _rects.Max(r => r.Bottom));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(i < _rects.Count ? _rects[i] : new Rect());
        return finalSize;
    }

    // Пропорции (ширина/высота) снимка; без размеров — квадрат. Слишком вытянутые ограничены, иначе плитки-«нитки».
    private static double AspectOf(UIElement child)
    {
        if ((child as FrameworkElement)?.DataContext is UploadPart part) return Math.Clamp(part.Aspect, 0.5, 2.2); // отправляемое — та же сетка
        var item = (child as FrameworkElement)?.DataContext is MessageViewModel m ? m.Item : null;
        var ar = item is { MediaWidth: > 0, MediaHeight: > 0 } ? (double)item.MediaWidth / item.MediaHeight : 1;
        return Math.Clamp(ar, 0.5, 2.2);
    }

    private static List<Rect> Layout(IReadOnlyList<double> ar, double w, double s)
    {
        var n = ar.Count;
        var rects = new List<Rect>(n);
        if (n == 0) return rects;
        if (n == 1)
        {
            rects.Add(new Rect(0, 0, w, Math.Clamp(w / ar[0], 120, MaxSingleHeight)));
            return rects;
        }

        // Вертикальный первый снимок и 2–3 остальных — колонка справа.
        if (n is 3 or 4 && ar[0] < 0.8)
        {
            var rest = ar.Skip(1).ToList();
            var gaps = s * (rest.Count - 1);
            // Высота левого = сумма высот правых + промежутки; ширины в сумме — вся ширина.
            var right = (w - s - ar[0] * gaps) / (ar[0] * rest.Sum(r => 1 / r) + 1);
            right = Math.Clamp(right, w * 0.28, w * 0.5);
            var left = w - s - right;
            var height = Math.Min(left / ar[0], MaxSingleHeight * 1.1);
            rects.Add(new Rect(0, 0, left, height));
            var each = (height - gaps) / rest.Count;
            for (var i = 0; i < rest.Count; i++) rects.Add(new Rect(left + s, i * (each + s), right, each));
            return rects;
        }

        // Остальное — ряды. Перебираем, как разбить снимки на ряды по 1–4, и берём самое ровное:
        // высоты рядов близки к «удобной» и альбом не слишком высокий.
        var target = n <= 4 ? w / 1.6 : w / 2.6;
        List<int>? best = null;
        var bestScore = double.MaxValue;
        foreach (var rows in Compositions(n))
        {
            var score = 0d;
            var start = 0;
            var total = 0d;
            foreach (var count in rows)
            {
                var h = RowHeight(ar, start, count, w, s);
                score += Math.Pow(h - target, 2);
                if (count == 1 && n > 2) score += 4000; // одиночные ряды в большом альбоме — некрасиво
                total += h;
                start += count;
            }
            if (total > MaxSingleHeight * 1.5) score += (total - MaxSingleHeight * 1.5) * 200;
            if (score < bestScore) { bestScore = score; best = rows; }
        }

        var y = 0d;
        var index = 0;
        foreach (var count in best!)
        {
            var h = Math.Round(RowHeight(ar, index, count, w, s));
            var x = 0d;
            for (var k = 0; k < count; k++)
            {
                // Последняя в ряду добирает остаток — правый край ровный, без щели от округлений.
                var cw = k == count - 1 ? w - x : Math.Round(ar[index + k] * h);
                rects.Add(new Rect(x, y, Math.Max(1, cw), h));
                x += cw + s;
            }
            y += h + s;
            index += count;
        }
        return rects;
    }

    private static double RowHeight(IReadOnlyList<double> ar, int start, int count, double w, double s)
    {
        var sum = 0d;
        for (var i = start; i < start + count; i++) sum += ar[i];
        return (w - s * (count - 1)) / sum;
    }

    // Все способы разбить n снимков по порядку на ряды от 1 до 4 (для альбома до 10 — сотни вариантов, мгновенно).
    private static IEnumerable<List<int>> Compositions(int n)
    {
        if (n == 0) { yield return []; yield break; }
        for (var first = 1; first <= Math.Min(4, n); first++)
            foreach (var rest in Compositions(n - first))
            {
                rest.Insert(0, first);
                yield return rest;
            }
    }
}
