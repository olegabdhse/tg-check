using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Controls;

/// <summary>
/// Закреплённые подарки вокруг аватарки, как в Telegram: до шести штук слева и справа, тихо покачиваются.
/// Кладётся поверх аватарки (нажатия проходят к ней); анимация идёт, только пока кольцо видно и окно не свёрнуто.
/// </summary>
public sealed class PinnedGiftsRing : Canvas
{
    // Места вокруг центра в долях радиуса аватарки: вразброс, но с запасом до края панели (иначе подарки обрезаются).
    private static readonly (double X, double Y, double Tilt, double Scale)[] Slots =
    [
        (-1.62, -0.72, -16, 0.92), (1.38, -0.80, 13, 1.0), (-1.86, 0.10, -7, 0.8),
        (1.84, -0.06, 10, 0.88), (-1.40, 0.84, -11, 1.0), (1.62, 0.76, 6, 0.84),
    ];

    public static readonly DependencyProperty GiftsProperty = DependencyProperty.Register(nameof(Gifts), typeof(IEnumerable), typeof(PinnedGiftsRing),
        new PropertyMetadata(null, (d, e) => ((PinnedGiftsRing)d).OnGiftsChanged(e)));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius), typeof(double), typeof(PinnedGiftsRing), new PropertyMetadata(66.0));
    public static readonly DependencyProperty GiftSizeProperty = DependencyProperty.Register(nameof(GiftSize), typeof(double), typeof(PinnedGiftsRing), new PropertyMetadata(36.0));

    public IEnumerable? Gifts { get => (IEnumerable?)GetValue(GiftsProperty); set => SetValue(GiftsProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public double GiftSize { get => (double)GetValue(GiftSizeProperty); set => SetValue(GiftSizeProperty, value); }

    private readonly List<TranslateTransform> _floats = [];
    private Window? _window;
    private bool _running;

    public PinnedGiftsRing()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.StateChanged += OnWindowState;
            Update();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.StateChanged -= OnWindowState;
            _window = null;
            Update();
        };
        IsVisibleChanged += (_, _) => Update();
    }

    private void OnWindowState(object? sender, EventArgs e) => Update();

    private void OnGiftsChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged now) now.CollectionChanged += OnCollectionChanged;
        Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        StopAll();
        Children.Clear();
        _floats.Clear();
        var i = 0;
        foreach (var gift in Gifts?.OfType<GiftViewModel>() ?? [])
        {
            if (i >= Slots.Length) break;
            var (x, y, tilt, scale) = Slots[i++];
            var size = GiftSize * scale;
            var item = new Grid { Width = size, Height = size, DataContext = gift, CacheMode = new BitmapCache { RenderAtScale = 2 } };
            // Размытое пятно цвета фона подарка, как в Telegram; у подарка без своего фона — без пятна.
            if (gift.Item.CenterColor is { } center)
            {
                var color = Images.Rgb(center);
                var edge = gift.Item.EdgeColor is { } e ? Images.Rgb(e) : color;
                var spot = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(235, color.R, color.G, color.B), 0),
                        new GradientStop(Color.FromArgb(150, edge.R, edge.G, edge.B), 0.5),
                        new GradientStop(Color.FromArgb(0, edge.R, edge.G, edge.B), 1),
                    },
                };
                spot.Freeze();
                item.Children.Add(new Ellipse { Fill = spot, Margin = new Thickness(-size * 0.38) });
            }
            var image = new Image { Stretch = Stretch.Uniform };
            image.SetBinding(Image.SourceProperty, new Binding(nameof(GiftViewModel.ModelImage)));
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            item.Children.Add(image);
            var shift = new TranslateTransform();
            _floats.Add(shift);
            item.RenderTransformOrigin = new Point(0.5, 0.5);
            item.RenderTransform = new TransformGroup { Children = { new RotateTransform(tilt), shift } };
            SetLeft(item, x * Radius - size / 2);
            SetTop(item, y * Radius - size / 2);
            Children.Add(item);
        }
        _running = false;
        Update();
    }

    private void Update()
    {
        var run = _floats.Count > 0 && IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized;
        if (run == _running) return;
        if (!run) { StopAll(); return; }
        _running = true;
        for (var i = 0; i < _floats.Count; i++)
        {
            // Каждый со своим периодом — без общего ритма.
            var sway = new DoubleAnimation(-3, 3, TimeSpan.FromSeconds(2.6 + i * 0.37))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(sway, 20); // медленное движение: чаще незаметно, а процессор тратит
            _floats[i].BeginAnimation(TranslateTransform.YProperty, sway);
        }
    }

    private void StopAll()
    {
        _running = false;
        foreach (var shift in _floats) shift.BeginAnimation(TranslateTransform.YProperty, null);
    }
}
