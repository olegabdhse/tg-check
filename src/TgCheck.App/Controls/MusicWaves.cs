using System.Windows;
using System.Windows.Media;

namespace TgCheck.App.Controls;

/// <summary>
/// Обложка играющей песни с «волнами»: вокруг круга расходятся лучи, длина — по громкости музыки прямо сейчас
/// (сглажено, у каждого луча свой ритм), по краю обложки — тонкое кольцо: сколько песни проиграно.
/// Анимация идёт, только пока музыка играет и контрол на экране.
/// </summary>
public sealed class MusicWaves : FrameworkElement
{
    public static readonly DependencyProperty CoverProperty = DependencyProperty.Register(nameof(Cover), typeof(ImageSource), typeof(MusicWaves),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CoverBrushProperty = DependencyProperty.Register(nameof(CoverBrush), typeof(Brush), typeof(MusicWaves),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(MusicWaves),
        new FrameworkPropertyMetadata(0d));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(MusicWaves),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(MusicWaves),
        new FrameworkPropertyMetadata(false, (d, _) => ((MusicWaves)d).UpdateHook()));

    public ImageSource? Cover { get => (ImageSource?)GetValue(CoverProperty); set => SetValue(CoverProperty, value); }
    public Brush? CoverBrush { get => (Brush?)GetValue(CoverBrushProperty); set => SetValue(CoverBrushProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

    /// <summary>Доля размера под обложку (остальное — место для волн).</summary>
    public double CoverRatio { get; set; } = 0.62;

    /// <summary>Обложка слегка увеличивается на басах (плеер под списком чатов).</summary>
    public bool BassPulse { get; set; }
    private double _bass, _bassPeak = 0.2;

    private const int Rays = 40;
    private double _amp;
    private bool _hooked;
    private TimeSpan _last;
    private static readonly Pen Ring = Freeze(new Pen(Brushes.White, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen RingTrack = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)), 2));
    private static readonly Brush RayBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public MusicWaves()
    {
        Loaded += (_, _) => UpdateHook();
        Unloaded += (_, _) => Unhook();
        IsVisibleChanged += (_, _) => UpdateHook();
    }

    private void UpdateHook()
    {
        if (IsPlaying && IsLoaded && IsVisible) { if (!_hooked) { _hooked = true; CompositionTarget.Rendering += OnFrame; } }
        else Unhook();
        InvalidateVisual();
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    // История громкости: каждый кадр — новый замер; лучи показывают последние замеры, и волна «бежит» вокруг обложки в такт музыке.
    private readonly double[] _history = new double[Rays / 2];
    private int _head;
    private double _peak = 0.3;

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if ((now - _last).TotalMilliseconds < 1000.0 / 40) return; // ~40 кадров/с
        _last = now;
        // Громкость прямо из плеера (не через привязку — без задержки), нормированная по недавнему пику: тихие песни тоже «качают».
        var level = IsPlaying ? Core.Media.AudioPlayer.Instance.Level : 0;
        _peak = Math.Max(level, _peak * 0.995);
        var norm = _peak > 0.02 ? Math.Min(1, level / _peak) : 0;
        // Быстро растут, плавно спадают — как у эквалайзера.
        _amp += (norm - _amp) * (norm > _amp ? 0.65 : 0.18);
        _head = (_head + 1) % _history.Length;
        _history[_head] = _amp;
        if (BassPulse)
        {
            var bass = IsPlaying ? Core.Media.AudioPlayer.Instance.Bass : 0;
            _bassPeak = Math.Max(bass, _bassPeak * 0.995);
            var b = _bassPeak > 0.02 ? Math.Min(1, bass / _bassPeak) : 0;
            b = b > 0.55 ? (b - 0.55) / 0.45 : 0; // только удары, а не фон
            _bass += (b - _bass) * (b > _bass ? 0.7 : 0.15);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size * CoverRatio / 2;
        if (BassPulse) r *= 1 + 0.07 * _bass; // «толчок» на басах

        // Лучи вокруг обложки.
        var t = Environment.TickCount64 / 1000.0;
        var maxLen = size / 2 - r - 3;
        if (maxLen > 1 && (IsPlaying || _amp > 0.01))
        {
            for (var i = 0; i < Rays; i++)
            {
                var a = 2 * Math.PI * i / Rays;
                // Луч i — замер i кадров назад (симметрично по двум половинам круга): удар в музыке расходится волной.
                var k = i < Rays / 2 ? i : Rays - 1 - i;
                var v = _history[(_head - k + _history.Length) % _history.Length];
                var len = 1.5 + maxLen * v * (0.85 + 0.15 * Math.Sin(t * 3 + i));
                var from = new Point(c.X + Math.Cos(a) * (r + 3), c.Y + Math.Sin(a) * (r + 3));
                var to = new Point(c.X + Math.Cos(a) * (r + 3 + len), c.Y + Math.Sin(a) * (r + 3 + len));
                dc.DrawLine(RayPen(size), from, to);
            }
        }

        // Обложка (или градиент песни) кругом.
        var fill = CoverFill();
        dc.DrawEllipse(fill, null, c, r, r);

        // Кольцо прогресса по краю обложки.
        dc.DrawEllipse(null, RingTrack, c, r - 1, r - 1);
        var p = Math.Clamp(Progress, 0, 1);
        if (p <= 0.001) return;
        if (p >= 0.999) { dc.DrawEllipse(null, Ring, c, r - 1, r - 1); return; }
        var end = 2 * Math.PI * p;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - (r - 1)), false, false);
            ctx.ArcTo(new Point(c.X + (r - 1) * Math.Sin(end), c.Y - (r - 1) * Math.Cos(end)), new Size(r - 1, r - 1), 0, p > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, Ring, g);
    }

    // Кисть обложки — одна на картинку (раньше создавалась заново на каждом кадре).
    private ImageSource? _fillFor;
    private Brush? _fill;

    private Brush CoverFill()
    {
        if (Cover is not { } img) return CoverBrush ?? Brushes.SteelBlue;
        if (!ReferenceEquals(img, _fillFor) || _fill is null)
        {
            _fillFor = img;
            _fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            _fill.Freeze();
        }
        return _fill;
    }

    private Pen? _rayPen;
    private double _rayPenSize;

    private Pen RayPen(double size)
    {
        if (_rayPen is not null && Math.Abs(_rayPenSize - size) < 0.5) return _rayPen;
        _rayPenSize = size;
        _rayPen = Freeze(new Pen(RayBrush, Math.Max(1.5, size / 40)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        return _rayPen;
    }
}
