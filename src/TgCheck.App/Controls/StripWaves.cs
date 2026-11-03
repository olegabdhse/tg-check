using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace TgCheck.App.Controls;

/// <summary>
/// Волны вдоль узкой полосы плеера: столбик коротких полосок, которые дышат под громкость песни.
/// Рисуется, только пока песня играет и полоса на экране.
/// </summary>
public sealed class StripWaves : FrameworkElement
{
    private const double BarHeight = 3, BarGap = 4;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(StripWaves));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(StripWaves),
        new PropertyMetadata(false, (d, _) => ((StripWaves)d).UpdateTimer()));
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(StripWaves),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _time;
    private double _smooth;

    public StripWaves()
    {
        _timer.Tick += (_, _) =>
        {
            if (Window.GetWindow(this) is { WindowState: WindowState.Minimized }) return;
            _time += 0.04;
            _smooth += (Math.Clamp(Level, 0, 1) - _smooth) * 0.25;
            InvalidateVisual();
        };
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => UpdateTimer();
    }

    private void UpdateTimer()
    {
        _timer.IsEnabled = IsPlaying && IsVisible && IsLoaded;
        if (!_timer.IsEnabled)
        {
            _smooth = 0;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var count = (int)((ActualHeight + BarGap) / (BarHeight + BarGap));
        if (width <= 0 || count <= 0) return;
        var top = (ActualHeight - (count * (BarHeight + BarGap) - BarGap)) / 2;
        var strength = 0.25 + _smooth * 0.75;
        for (var i = 0; i < count; i++)
        {
            // Несколько наложенных волн, чтобы полоски не двигались одинаково; к концам столбика они затухают.
            var edge = Math.Sin(Math.PI * (i + 0.5) / count);
            var wave = 0.5 + 0.3 * Math.Sin(_time * 3.1 + i * 0.55) + 0.2 * Math.Sin(_time * 5.3 - i * 0.31);
            var length = IsPlaying ? Math.Max(4, width * wave * strength * (0.35 + 0.65 * edge)) : 4;
            dc.PushOpacity(0.35 + 0.65 * edge);
            dc.DrawRoundedRectangle(BarBrush, null, new Rect((width - length) / 2, top + i * (BarHeight + BarGap), length, BarHeight), BarHeight / 2, BarHeight / 2);
            dc.Pop();
        }
    }
}
