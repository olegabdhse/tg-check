using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TgCheck.App.Controls;

/// <summary>
/// Волна голосового в поле ввода. Во время записи (Live) — последние столбики бегут справа налево.
/// После остановки — вся запись целиком: ручки по краям обрезают начало и конец, клик — перемотка прослушивания.
/// </summary>
public sealed class VoiceWave : FrameworkElement
{
    private const double Bar = 3, Gap = 2, HandleWidth = 10;

    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(nameof(Peaks), typeof(float[]), typeof(VoiceWave),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LiveProperty = DependencyProperty.Register(nameof(Live), typeof(bool), typeof(VoiceWave),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrimStartProperty = DependencyProperty.Register(nameof(TrimStart), typeof(double), typeof(VoiceWave),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty TrimEndProperty = DependencyProperty.Register(nameof(TrimEnd), typeof(double), typeof(VoiceWave),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(VoiceWave),
        new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[]? Peaks { get => (float[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public bool Live { get => (bool)GetValue(LiveProperty); set => SetValue(LiveProperty, value); }
    /// <summary>Начало оставляемой части, 0..1 от всей записи.</summary>
    public double TrimStart { get => (double)GetValue(TrimStartProperty); set => SetValue(TrimStartProperty, value); }
    /// <summary>Конец оставляемой части, 0..1.</summary>
    public double TrimEnd { get => (double)GetValue(TrimEndProperty); set => SetValue(TrimEndProperty, value); }
    /// <summary>Где сейчас прослушивание, 0..1 от всей записи (меньше 0 — не играет).</summary>
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    /// <summary>Клик по волне: перемотать прослушивание сюда (0..1 от всей записи).</summary>
    public event Action<double>? SeekRequested;

    private static readonly Brush Kept = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Cut = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Played = Frozen(Color.FromRgb(0x8C, 0xCB, 0xFF));
    private static readonly Brush HandleFill = Frozen(Color.FromRgb(0x3E, 0x9B, 0xF0));
    private static readonly Brush Shade = Frozen(Color.FromArgb(0x30, 0x3E, 0x9B, 0xF0));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public VoiceWave()
    {
        Cursor = Cursors.Arrow;
        Focusable = false;
    }

    // Начало волны и её ширина (с полями под ручки, чтобы их было за что взять у краёв).
    private double Left => Live ? 0 : HandleWidth;
    private double Span => Math.Max(1, ActualWidth - 2 * Left);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // ловить мышь по всей площади
        var peaks = Peaks;
        if (peaks is not { Length: > 0 }) return;
        var h = ActualHeight;
        var slots = Math.Max(1, (int)((Span + Gap) / (Bar + Gap)));
        var max = Math.Max(0.08f, peaks.Max());

        if (Live)
        {
            // Последние столбики — у правого края, как в Telegram.
            var shown = Math.Min(slots, peaks.Length);
            for (var i = 0; i < shown; i++)
            {
                var p = peaks[peaks.Length - shown + i] / max;
                var x = ActualWidth - (shown - i) * (Bar + Gap);
                DrawBar(dc, x, h, p, Kept);
            }
            return;
        }

        // Вся запись: столбик — максимум своего отрезка.
        var bars = Math.Min(slots, Math.Max(1, peaks.Length));
        var step = Span / bars;
        for (var i = 0; i < bars; i++)
        {
            var from = i * peaks.Length / bars;
            var to = Math.Max(from + 1, (i + 1) * peaks.Length / bars);
            var p = 0f;
            for (var j = from; j < to && j < peaks.Length; j++) p = Math.Max(p, peaks[j]);
            var t = (i + 0.5) / bars;
            var brush = t < TrimStart || t > TrimEnd ? Cut : Position >= 0 && t <= Position ? Played : Kept;
            DrawBar(dc, Left + i * step + (step - Bar) / 2, h, p / max, brush);
        }

        // Оставляемая часть подсвечена, по краям — ручки.
        var x0 = Left + TrimStart * Span;
        var x1 = Left + TrimEnd * Span;
        dc.DrawRoundedRectangle(Shade, null, new Rect(x0, 0, Math.Max(0, x1 - x0), h), 6, 6);
        DrawHandle(dc, x0 - HandleWidth, h);
        DrawHandle(dc, x1, h);
    }

    private static void DrawBar(DrawingContext dc, double x, double h, double level, Brush brush)
    {
        var bh = Math.Max(3, Math.Min(1, level) * (h - 8));
        dc.DrawRoundedRectangle(brush, null, new Rect(x, (h - bh) / 2, Bar, bh), 1.5, 1.5);
    }

    private static void DrawHandle(DrawingContext dc, double x, double h)
    {
        dc.DrawRoundedRectangle(HandleFill, null, new Rect(x, 0, HandleWidth, h), 4, 4);
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(x + HandleWidth / 2 - 1, h / 2 - 6, 2, 12), 1, 1);
    }

    // ---- перетаскивание ручек
    private enum Drag { None, Start, End }
    private Drag _drag;

    private double ToFraction(double x) => Math.Clamp((x - Left) / Span, 0, 1);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Live || Peaks is not { Length: > 0 }) return;
        var x = e.GetPosition(this).X;
        var x0 = Left + TrimStart * Span;
        var x1 = Left + TrimEnd * Span;
        // Ручка ближе всего к курсору (в пределах 16 px) — тянем её; иначе клик — перемотка.
        var d0 = Math.Abs(x - (x0 - HandleWidth / 2));
        var d1 = Math.Abs(x - (x1 + HandleWidth / 2));
        _drag = Math.Min(d0, d1) <= 16 ? (d0 <= d1 ? Drag.Start : Drag.End) : Drag.None;
        if (_drag == Drag.None)
        {
            var f = ToFraction(x);
            if (f >= TrimStart && f <= TrimEnd) SeekRequested?.Invoke(f);
            return;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        if (_drag == Drag.None)
        {
            if (Live || Peaks is not { Length: > 0 }) return;
            var x0 = Left + TrimStart * Span;
            var x1 = Left + TrimEnd * Span;
            var near = Math.Min(Math.Abs(x - (x0 - HandleWidth / 2)), Math.Abs(x - (x1 + HandleWidth / 2))) <= 16;
            Cursor = near ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        // Между ручками — не меньше секунды записи.
        var total = Peaks!.Length / (double)Core.Media.VoiceRecorder.PeaksPerSecond;
        var minGap = total > 0 ? Math.Min(1, 1.0 / total) : 0.1;
        var f = ToFraction(_drag == Drag.Start ? x + HandleWidth / 2 : x - HandleWidth / 2);
        if (_drag == Drag.Start) TrimStart = Math.Min(f, TrimEnd - minGap);
        else TrimEnd = Math.Max(f, TrimStart + minGap);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }
}
