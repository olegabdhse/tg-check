using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TgCheck.App.Controls;

/// <summary>
/// Индикатор отправки как в Telegram: тёмный круг с крестиком, белый ободок заполняется по кругу по проценту загрузки
/// (плавно, без скачков) и медленно вращается. Элемент живёт всё время отправки — меняется только дуга.
/// </summary>
public sealed class ProgressArc : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(ProgressArc),
        new PropertyMetadata(0d, (d, e) => ((ProgressArc)d).AnimateTo((double)e.NewValue)));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(ProgressArc),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(ProgressArc),
        new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0))), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Заливка круга под ободком.</summary>
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private static readonly Pen Arc = FreezePen(new Pen(Brushes.White, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen Cross = FreezePen(new Pen(Brushes.White, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private readonly RotateTransform _spin = new();

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    public ProgressArc()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => _spin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever });
        Unloaded += (_, _) => _spin.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void AnimateTo(double value) =>
        BeginAnimation(ShownProperty, new DoubleAnimation(Math.Clamp(value, 0, 1), TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuadraticEase() },
            HandoffBehavior.SnapshotAndReplace);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2;
        dc.DrawEllipse(Fill, null, c, r, r);

        // Крестик отмены.
        var k = size * 0.16;
        dc.DrawLine(Cross, new Point(c.X - k, c.Y - k), new Point(c.X + k, c.Y + k));
        dc.DrawLine(Cross, new Point(c.X + k, c.Y - k), new Point(c.X - k, c.Y + k));

        // Ободок: минимум маленькая дуга, чтобы с самого начала было видно, что идёт отправка.
        var shown = Math.Max(0.04, (double)GetValue(ShownProperty));
        var ar = r - 4;
        _spin.CenterX = c.X;
        _spin.CenterY = c.Y;
        dc.PushTransform(_spin);
        if (shown >= 0.999) dc.DrawEllipse(null, Arc, c, ar, ar);
        else
        {
            var angle = shown * 2 * Math.PI;
            var start = new Point(c.X, c.Y - ar);
            var end = new Point(c.X + ar * Math.Sin(angle), c.Y - ar * Math.Cos(angle));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(ar, ar), 0, shown > 0.5, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, Arc, g);
        }
        dc.Pop();
    }
}
