using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;
using TgCheck.Core.Models;

namespace TgCheck.App.Controls;

/// <summary>
/// GIF/анимация из Telegram (это mp4 без звука): превью сразу, затем бесшовный повтор без значка плеера.
/// Играет, только пока видна.
/// </summary>
public sealed class GifView : Grid
{
    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(
        nameof(Media), typeof(MediaHolder), typeof(GifView), new PropertyMetadata(null, (d, _) => ((GifView)d).Reload()));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(GifView), new PropertyMetadata(10d, (d, _) => ((GifView)d).UpdateClip()));

    public MediaHolder? Media { get => (MediaHolder?)GetValue(MediaProperty); set => SetValue(MediaProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
    private readonly MediaElement _player = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true,
        Stretch = Stretch.UniformToFill, Opacity = 0,
    };
    private int _version;
    private bool _hasSource;
    private readonly DispatcherTimer _viewportTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public GifView()
    {
        _viewportTimer.Tick += (_, _) => UpdatePlayback();
        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        Children.Add(_preview);
        Children.Add(_player);
        _player.MediaEnded += (_, _) => { _player.Position = TimeSpan.Zero; _player.Play(); };
        _player.MediaOpened += (_, _) => _player.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        Loaded += (_, _) => { _viewportTimer.Start(); UpdatePlayback(); };
        Unloaded += (_, _) => { _viewportTimer.Stop(); _player.Stop(); };
        IsVisibleChanged += (_, _) => UpdatePlayback();
        SizeChanged += (_, _) => UpdateClip();
    }

    private void UpdateClip() =>
        Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), CornerRadius, CornerRadius);

    private async void Reload()
    {
        var version = ++_version;
        _hasSource = false;
        _fileRequested = false;
        _player.Stop();
        _player.Source = null;
        _player.Opacity = 0;
        if (Media is not { } media || AppServices.Media is not { } service) return;
        _preview.Source = Images.FromBytes(await service.GetMessagePreviewAsync(media));
        UpdatePlayback();
    }

    private bool _fileRequested;

    // Полный файл качается только когда GIF показался на экране, и в фоновой очереди — не мешает открытым видео.
    private async void EnsureFile()
    {
        if (_fileRequested || Media is not { } media || AppServices.Media is not { } service) return;
        _fileRequested = true;
        var version = _version;
        var path = await service.GetMediaFileAsync(media, priority: false);
        if (version != _version || path is null) { _fileRequested = false; return; }
        _player.Source = new Uri(path);
        _hasSource = true;
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        var inView = IsLoaded && IsVisible && Viewport.Contains(this);
        if (inView && !_hasSource) { EnsureFile(); return; }
        if (!_hasSource) return;
        // Играем только то, что реально в видимой части ленты.
        if (IsLoaded && IsVisible && Viewport.Contains(this)) _player.Play();
        else _player.Pause();
    }
}

/// <summary>Видеосообщение-кружок: превью, по клику играет со звуком прямо в чате, кольцо прогресса.</summary>
public sealed class VideoNoteView : Grid
{
    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(
        nameof(Media), typeof(MediaHolder), typeof(VideoNoteView), new PropertyMetadata(null, (d, _) => ((VideoNoteView)d).Reload()));

    public MediaHolder? Media { get => (MediaHolder?)GetValue(MediaProperty); set => SetValue(MediaProperty, value); }

    private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
    private readonly MediaElement _player = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.UniformToFill,
        IsMuted = true, Opacity = 0,
    };
    private readonly Path _ring = new() { Stroke = Brushes.White, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Border _badge;
    private readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 11 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _withSound;
    private int _version;

    public VideoNoteView()
    {
        Cursor = Cursors.Hand;
        var inner = new Grid();
        inner.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) });
        inner.Children.Add(_preview);
        inner.Children.Add(_player);
        inner.SizeChanged += (_, _) => inner.Clip = new EllipseGeometry(new Rect(0, 0, inner.ActualWidth, inner.ActualHeight));
        Children.Add(inner);
        Children.Add(_ring);
        _badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 2, 7, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18, 0, 0, 14), Child = _time,
        };
        Children.Add(_badge);

        _player.MediaEnded += (_, _) =>
        {
            _player.Position = TimeSpan.Zero;
            if (_withSound) StopSound();
            else _player.Play();
        };
        _player.MediaOpened += (_, _) => _player.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        _timer.Tick += (_, _) => UpdateRing();
        MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleSound(); };
        _viewportTimer.Tick += (_, _) => UpdateViewport();
        Loaded += (_, _) => { _viewportTimer.Start(); UpdateViewport(); };
        Unloaded += (_, _) => { _viewportTimer.Stop(); _player.Stop(); _timer.Stop(); };
    }

    private readonly DispatcherTimer _viewportTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _fileRequested;

    // Без звука кружок крутится только на экране; файл качается в фоновой очереди, когда кружок показался.
    private void UpdateViewport()
    {
        if (_withSound) return;
        var inView = IsLoaded && IsVisible && Viewport.Contains(this);
        if (inView && _player.Source is null) { _ = EnsureFileAsync(priority: false); return; }
        if (_player.Source is null) return;
        if (inView) _player.Play();
        else _player.Pause();
    }

    private async Task EnsureFileAsync(bool priority)
    {
        if (_player.Source is not null) return;
        if (_fileRequested && !priority) return;
        if (Media is not { } media || AppServices.Media is not { } service) return;
        _fileRequested = true;
        var version = _version;
        var path = await service.GetMediaFileAsync(media, priority: priority);
        if (version != _version || path is null) { _fileRequested = false; return; }
        if (_player.Source is null) _player.Source = new Uri(path);
    }

    private async void Reload()
    {
        var version = ++_version;
        _player.Source = null;
        _player.Opacity = 0;
        if (Media is not { } media || AppServices.Media is not { } service) return;
        _time.Text = Fmt.Duration(media.DurationSeconds);
        _fileRequested = false;
        _preview.Source = Images.FromBytes(await service.GetMessagePreviewAsync(media));
        UpdateViewport();
    }

    private async void ToggleSound()
    {
        if (_player.Source is null) await EnsureFileAsync(priority: true);
        if (_player.Source is null) return;
        if (_withSound) { StopSound(); return; }
        AudioPlayer.Instance.Stop();
        _withSound = true;
        _player.IsMuted = false;
        _player.Position = TimeSpan.Zero;
        _player.Play();
        _timer.Start();
    }

    private void StopSound()
    {
        _withSound = false;
        _player.IsMuted = true;
        _timer.Stop();
        _ring.Data = null;
        _time.Text = Fmt.Duration(Media?.DurationSeconds ?? 0);
        _player.Play();
    }

    private void UpdateRing()
    {
        if (!_player.NaturalDuration.HasTimeSpan) return;
        var total = _player.NaturalDuration.TimeSpan.TotalSeconds;
        var pos = _player.Position.TotalSeconds;
        _time.Text = Fmt.Duration(pos);
        var frac = total > 0 ? Math.Clamp(pos / total, 0, 0.999) : 0;
        var r = ActualWidth / 2 - 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var angle = frac * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
        _ring.Data = new PathGeometry([fig]);
    }
}

/// <summary>Проверка, что элемент сейчас в видимой области своего ScrollViewer.</summary>
internal static class Viewport
{
    public static bool Contains(FrameworkElement el)
    {
        if (!el.IsVisible || el.ActualWidth == 0) return false;
        DependencyObject? p = el;
        while (p is not null and not ScrollViewer) p = VisualTreeHelper.GetParent(p);
        if (p is not ScrollViewer sv) return true;
        try
        {
            var box = el.TransformToAncestor(sv).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            return box.IntersectsWith(new Rect(0, 0, sv.ViewportWidth, sv.ViewportHeight));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
