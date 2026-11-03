using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace TgCheck.App.Controls;

/// <summary>
/// Волны вдоль узкой полосы плеера: столбик коротких полосок, которые дышат под громкость песни.
/// Рисуется, только пока песня играет и полоса на экране. Движение идёт по настоящему времени: задержка кадра не превращается в рывок.
/// </summary>
public sealed class StripWaves : FrameworkElement
{
    private const double BarHeight = 3, BarGap = 4;
    private const int FadeSteps = 8;
    // За это время (с) сглаженная громкость проходит большую часть пути к новой: громкость приходит лишь 4 раза в секунду.
    private const double LevelSmoothing = 0.22;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(StripWaves));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(StripWaves),
        new PropertyMetadata(false, (d, _) => ((StripWaves)d).UpdateTimer()));
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(StripWaves),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((StripWaves)d)._fades = null));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    // Приоритет отрисовки: обычный таймер WPF уступает всей остальной работе окна, и волны шли рывками.
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastTick;
    private double _time;
    private double _smooth;
    private Brush[]? _fades;

    public StripWaves()
    {
        _timer.Tick += (_, _) =>
        {
            var now = _clock.Elapsed;
            var delta = Math.Min((now - _lastTick).TotalSeconds, 0.25); // после долгой паузы не прыгаем вперёд
            _lastTick = now;
            if (Window.GetWindow(this) is { WindowState: WindowState.Minimized }) return;
            _time += delta;
            _smooth += (Math.Clamp(Level, 0, 1) - _smooth) * (1 - Math.Exp(-delta / LevelSmoothing));
            InvalidateVisual();
        };
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => UpdateTimer();
    }

    private void UpdateTimer()
    {
        var run = IsPlaying && IsVisible && IsLoaded;
        if (run == _timer.IsEnabled) return;
        _timer.IsEnabled = run;
        if (run)
        {
            _clock.Restart();
            _lastTick = TimeSpan.Zero;
        }
        else
        {
            _smooth = 0;
            InvalidateVisual();
        }
    }

    // Прозрачность — готовыми кистями: отдельный слой на каждую из десятков полосок перерисовывался слишком долго.
    private Brush[] Fades()
    {
        if (_fades is not null) return _fades;
        var color = (BarBrush as SolidColorBrush)?.Color ?? Colors.White;
        var fades = new Brush[FadeSteps + 1];
        for (var i = 0; i <= FadeSteps; i++)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(color.A * (0.35 + 0.65 * i / FadeSteps)), color.R, color.G, color.B));
            brush.Freeze();
            fades[i] = brush;
        }
        return _fades = fades;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var count = (int)((ActualHeight + BarGap) / (BarHeight + BarGap));
        if (width <= 0 || count <= 0) return;
        var fades = Fades();
        var top = (ActualHeight - (count * (BarHeight + BarGap) - BarGap)) / 2;
        var strength = 0.25 + _smooth * 0.75;
        for (var i = 0; i < count; i++)
        {
            // Несколько наложенных волн, чтобы полоски не двигались одинаково; к концам столбика они затухают.
            var edge = Math.Sin(Math.PI * (i + 0.5) / count);
            var wave = 0.5 + 0.3 * Math.Sin(_time * 3.1 + i * 0.55) + 0.2 * Math.Sin(_time * 5.3 - i * 0.31);
            var length = IsPlaying ? Math.Max(4, width * wave * strength * (0.35 + 0.65 * edge)) : 4;
            dc.DrawRoundedRectangle(fades[(int)(edge * FadeSteps)], null, new Rect((width - length) / 2, top + i * (BarHeight + BarGap), length, BarHeight), BarHeight / 2, BarHeight / 2);
        }
    }
}
