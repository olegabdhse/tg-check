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

    /// <summary>Играть только уже скачанное (видео в слайд-шоу): само не качает.</summary>
    public bool OnlyIfCached { get; set; }

    private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
    private MediaElement? _player; // создаётся, только когда есть что играть: каждый живой плеер — отдельный движок WMP вне .NET
    private int _version;
    private bool _hasSource;
    private readonly DispatcherTimer _viewportTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _firstFrame = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private int _firstFrameTicks;

    public GifView()
    {
        _viewportTimer.Tick += (_, _) => UpdatePlayback();
        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        Children.Add(_preview);
        _firstFrame.Tick += (_, _) =>
        {
            if (_player is not { } player) { _firstFrame.Stop(); return; }
            if (player.Position < TimeSpan.FromMilliseconds(80) && ++_firstFrameTicks < 40) return;
            _firstFrame.Stop();
            _firstFrameTicks = 0;
            player.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        };
        Loaded += (_, _) => { _viewportTimer.Start(); UpdatePlayback(); };
        Unloaded += (_, _) => { _viewportTimer.Stop(); ReleasePlayer(); };
        IsVisibleChanged += (_, _) => UpdatePlayback();
        SizeChanged += (_, _) => UpdateClip();
    }

    private void UpdateClip() =>
        Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), CornerRadius, CornerRadius);

    private MediaElement CreatePlayer()
    {
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true,
            Stretch = Stretch.UniformToFill, Opacity = 0,
        };
        // По кругу без чёрной вспышки: перемотка на самое начало (ровно 0) показывала пустой кадр.
        player.MediaEnded += (_, _) => { player.Position = TimeSpan.FromMilliseconds(1); player.Play(); };
        // Видео проявляется только когда реально пошли кадры: сразу после открытия плеер ещё чёрный — отсюда мерцание.
        player.MediaOpened += (_, _) => _firstFrame.Start();
        Children.Add(player);
        return _player = player;
    }

    private async void Reload()
    {
        var version = ++_version;
        ReleasePlayer();
        _preview.Source = null; // превью прошлого видео (слайда) не должно закрывать новое
        if (Media is not { } media || AppServices.Media is not { } service) return;
        var bytes = await service.GetMessagePreviewAsync(media);
        var image = bytes is null ? null : await Task.Run(() => Images.FromBytesMax(bytes, 1280)); // декодируем не в потоке интерфейса
        if (version != _version) return;
        _preview.Source = image;
        UpdatePlayback();
    }

    private bool _fileRequested;

    // Полный файл качается только когда GIF показался на экране, и в фоновой очереди — не мешает открытым видео.
    private async void EnsureFile()
    {
        if (_fileRequested || Media is not { } media || AppServices.Media is not { } service) return;
        _fileRequested = true;
        var version = _version;
        var path = await service.GetMediaFileAsync(media, priority: true);
        if (version != _version || path is null) { _fileRequested = false; return; }
        (_player ?? CreatePlayer()).Source = new Uri(path);
        _hasSource = true;
        UpdatePlayback();
    }

    // Плеер держит движок, декодер и кадры видео вне .NET, в том числе на паузе. Долго не на экране — убираем его совсем, остаётся превью;
    // вернётся на экран — файл откроется заново из кэша.
    private void ReleasePlayer()
    {
        _hiddenSince = 0;
        _hasSource = false;
        _fileRequested = false;
        _firstFrame.Stop();
        _firstFrameTicks = 0;
        if (_player is not { } player) return;
        _player = null;
        player.Close();
        Children.Remove(player);
        Viewport.PlayerReleased();
    }

    private long _hiddenSince;

    private void UpdatePlayback()
    {
        var inView = IsLoaded && IsVisible && Viewport.Contains(this);
        if (inView) _hiddenSince = 0;
        else if (_hasSource)
        {
            if (_hiddenSince == 0) _hiddenSince = Environment.TickCount64;
            else if (Environment.TickCount64 - _hiddenSince > Viewport.ReleaseAfterMs) { ReleasePlayer(); return; }
        }
        if (inView && !_hasSource)
        {
            if (!OnlyIfCached || (Media is { } m && AppServices.Media?.IsMediaFileCached(m) == true)) EnsureFile();
            return;
        }
        if (!_hasSource || _player is not { } player) return;
        // Играем только то, что реально в видимой части ленты.
        if (inView) player.Play();
        else player.Pause();
    }
}

/// <summary>Видеосообщение-кружок: превью, по клику играет со звуком прямо в чате, кольцо прогресса.</summary>
public sealed class VideoNoteView : Grid
{
    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(
        nameof(Media), typeof(MediaHolder), typeof(VideoNoteView), new PropertyMetadata(null, (d, _) => ((VideoNoteView)d).Reload()));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(VideoNoteView));

    /// <summary>Не прослушано — точка рядом со временем (как в Telegram).</summary>
    public static readonly DependencyProperty IsUnlistenedProperty = DependencyProperty.Register(nameof(IsUnlistened), typeof(bool), typeof(VideoNoteView),
        new PropertyMetadata(false, (d, e) => ((VideoNoteView)d)._dot.Visibility = e.NewValue is true ? Visibility.Visible : Visibility.Collapsed));

    public bool IsUnlistened
    {
        get => (bool)GetValue(IsUnlistenedProperty);
        set => SetValue(IsUnlistenedProperty, value);
    }

    private readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 6, Height = 6, Fill = Brushes.White, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    /// <summary>Имя отправителя — для панели плеера.</summary>
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public MediaHolder? Media { get => (MediaHolder?)GetValue(MediaProperty); set => SetValue(MediaProperty, value); }

    private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
    private MediaElement? _player; // как у GifView: создаётся, только когда есть что играть
    private readonly Grid _inner = new();
    private readonly Path _ring = new() { Stroke = Brushes.White, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Border _badge;
    private readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 11 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _withSound;
    private int _version;

    public VideoNoteView()
    {
        Cursor = Cursors.Hand;
        _inner.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) });
        _inner.Children.Add(_preview);
        _inner.SizeChanged += (_, _) => _inner.Clip = new EllipseGeometry(new Rect(0, 0, _inner.ActualWidth, _inner.ActualHeight));
        Children.Add(_inner);
        Children.Add(_ring);
        _badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 2, 7, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18, 0, 0, 14), Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _time, _dot } },
        };
        Children.Add(_badge);

        _timer.Tick += (_, _) => UpdateRing();
        MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleSound(); };
        _viewportTimer.Tick += (_, _) => UpdateViewport();
        Loaded += (_, _) => { _viewportTimer.Start(); UpdateViewport(); };
        Unloaded += (_, _) =>
        {
            _viewportTimer.Stop();
            _timer.Stop();
            if (_withSound) _player?.Stop();
            else ReleasePlayer();
        };
    }

    private MediaElement CreatePlayer()
    {
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.UniformToFill,
            IsMuted = true, Opacity = 0,
        };
        player.MediaEnded += (_, _) =>
        {
            player.Position = TimeSpan.Zero;
            if (_withSound) StopSound();
            else player.Play();
        };
        player.MediaOpened += (_, _) => player.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        _inner.Children.Add(player);
        return _player = player;
    }

    private readonly DispatcherTimer _viewportTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _fileRequested;
    private long _hiddenSince;

    // Как у GifView: долго не на экране — плеер закрывается и не держит декодер, остаётся превью.
    private void ReleasePlayer()
    {
        _hiddenSince = 0;
        _fileRequested = false;
        if (_player is not { } player) return;
        _player = null;
        _roundBrush = null;
        player.Close();
        _inner.Children.Remove(player);
        Viewport.PlayerReleased();
    }

    // Без звука кружок крутится только на экране; файл качается в фоновой очереди, когда кружок показался.
    private void UpdateViewport()
    {
        if (_withSound)
        {
            ReportRound();
            return;
        }
        var inView = IsLoaded && IsVisible && Viewport.Contains(this);
        if (inView) _hiddenSince = 0;
        else if (_player is not null)
        {
            if (_hiddenSince == 0) _hiddenSince = Environment.TickCount64;
            else if (Environment.TickCount64 - _hiddenSince > Viewport.ReleaseAfterMs) { ReleasePlayer(); return; }
        }
        if (_player is not { Source: not null } player)
        {
            if (inView) _ = EnsureFileAsync(priority: false);
            return;
        }
        if (inView) player.Play();
        else player.Pause();
    }

    private async Task EnsureFileAsync(bool priority)
    {
        if (_player?.Source is not null) return;
        if (_fileRequested && !priority) return;
        if (Media is not { } media || AppServices.Media is not { } service) return;
        _fileRequested = true;
        var version = _version;
        var path = await service.GetMediaFileAsync(media, priority: priority);
        if (version != _version || path is null) { _fileRequested = false; return; }
        if (_player?.Source is null) (_player ?? CreatePlayer()).Source = new Uri(path);
    }

    private async void Reload()
    {
        var version = ++_version;
        ReleasePlayer();
        if (Media is not { } media || AppServices.Media is not { } service) return;
        _time.Text = Fmt.Duration(media.DurationSeconds);
        _fileRequested = false;
        var bytes = await service.GetMessagePreviewAsync(media);
        var image = bytes is null ? null : await Task.Run(() => Images.FromBytesMax(bytes, 480)); // декодируем не в потоке интерфейса
        if (version != _version) return;
        _preview.Source = image;
        UpdateViewport();
    }

    private async void ToggleSound()
    {
        if (_player?.Source is null) await EnsureFileAsync(priority: true);
        if (_player is not { Source: not null } player) return;
        if (_withSound) { StopSound(); return; }
        AudioPlayer.Instance.Stop();
        _withSound = true;
        player.IsMuted = false;
        player.Position = TimeSpan.Zero;
        player.Play();
        _timer.Start();
        _paused = false;
        _roundBrush ??= new VisualBrush(player) { Stretch = Stretch.UniformToFill };
        ViewModels.NowPlaying.Instance.StartRound(Title ?? "Видеосообщение", _roundBrush, TogglePause, StopSound);
        RaiseEvent(new RoutedEventArgs(SoundStartedEvent, this));
        _viewportTimer.Interval = TimeSpan.FromMilliseconds(150);
    }

    /// <summary>Кружок начали слушать со звуком — лента отмечает его прослушанным.</summary>
    public static readonly RoutedEvent SoundStartedEvent = EventManager.RegisterRoutedEvent(
        "SoundStarted", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(VideoNoteView));

    private void StopSound()
    {
        _withSound = false;
        _paused = false;
        _viewportTimer.Interval = TimeSpan.FromMilliseconds(450);
        if (_roundBrush is not null) ViewModels.NowPlaying.Instance.EndRound(_roundBrush);
        _timer.Stop();
        _ring.Data = null;
        _time.Text = Fmt.Duration(Media?.DurationSeconds ?? 0);
        if (_player is not { } player) return;
        player.IsMuted = true;
        player.Play();
    }

    // Кружок со звуком: пауза с панели плеера.
    private bool _paused;
    private VisualBrush? _roundBrush;

    private void TogglePause()
    {
        if (!_withSound || _player is not { } player) return;
        _paused = !_paused;
        if (_paused) player.Pause(); else player.Play();
        ReportRound();
    }

    private void ReportRound()
    {
        if (!_withSound || _player is not { NaturalDuration.HasTimeSpan: true } player) return;
        ViewModels.NowPlaying.Instance.UpdateRound(player.Position.TotalSeconds, player.NaturalDuration.TimeSpan.TotalSeconds,
            !_paused, !Viewport.Contains(this));
    }

    private void UpdateRing()
    {
        if (_player is not { NaturalDuration.HasTimeSpan: true } player) return;
        var total = player.NaturalDuration.TimeSpan.TotalSeconds;
        var pos = player.Position.TotalSeconds;
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
    /// <summary>Через столько миллисекунд вне экрана плеер закрывается (короткая прокрутка туда-обратно его не трогает).</summary>
    public const int ReleaseAfterMs = 10_000;

    private static DispatcherTimer? _collect;

    /// <summary>
    /// Плеер убран: его движок вне .NET освобождается только при сборке мусора — запускаем её, когда освобождения затихли.
    /// Только младшие поколения: полная сборка заодно очищает слабый кэш картинок, и аватарки с превью декодировались бы заново.
    /// </summary>
    public static void PlayerReleased()
    {
        if (_collect is null)
        {
            _collect = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _collect.Tick += (_, _) =>
            {
                _collect.Stop();
                GC.Collect(1, GCCollectionMode.Forced, blocking: false);
            };
        }
        _collect.Stop();
        _collect.Start();
    }

    public static bool Contains(FrameworkElement el)
    {
        if (!el.IsVisible || el.ActualWidth == 0) return false;
        // У свёрнутого окна IsVisible остаётся true — проверяем отдельно.
        if (Window.GetWindow(el) is { WindowState: WindowState.Minimized }) return false;
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
