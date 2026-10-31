using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SkiaSharp.Skottie;
using TgCheck.App.Helpers;
using TgCheck.Core.Models;

namespace TgCheck.App.Controls;

/// <summary>
/// Стикер Telegram: анимированные (.tgs/Lottie) проигрываются через Skottie прямо в WriteableBitmap, видео-стикеры (webm VP9
/// с прозрачностью) — своим декодером (Helpers/Vp9), статичные показываются картинкой. Анимация идёт только пока контрол на экране.
/// </summary>
public sealed class StickerView : Image
{
    private static readonly HashSet<StickerView> Active = [];
    private static bool _hooked;
    private static readonly Dictionary<long, string> JsonCache = [];

    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(
        nameof(Media), typeof(MediaHolder), typeof(StickerView), new PropertyMetadata(null, (d, _) => ((StickerView)d).Reload()));

    /// <summary>false — показывать только первый кадр (для сеток выбора).</summary>
    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(StickerView), new PropertyMetadata(true));

    /// <summary>Id кастомного (премиум) эмодзи — документ подгружается сам.</summary>
    public static readonly DependencyProperty CustomEmojiIdProperty = DependencyProperty.Register(
        nameof(CustomEmojiId), typeof(long), typeof(StickerView), new PropertyMetadata(0L, (d, e) => ((StickerView)d).LoadCustom((long)e.NewValue)));

    public long CustomEmojiId
    {
        get => (long)GetValue(CustomEmojiIdProperty);
        set => SetValue(CustomEmojiIdProperty, value);
    }

    private async void LoadCustom(long id)
    {
        if (id == 0 || AppServices.Tg is not { } tg) return;
        var sticker = await tg.GetCustomEmojiAsync(id);
        if (sticker is not null && CustomEmojiId == id) Media = sticker;
    }

    public MediaHolder? Media
    {
        get => (MediaHolder?)GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    /// <summary>Эмодзи в тексте: при появлении анимация проигрывается один раз и замирает (как в Telegram), а не крутится.
    /// Задавать до Media; уже показанная статичная картинка (Source) остаётся, пока грузится анимация.</summary>
    public bool Once { get; set; }
    private bool _onceDone;
    private long _shownId;

    /// <summary>Проиграть анимацию один раз и скрыться (эффект при постановке реакции).</summary>
    public bool PlayOnce { get; set; }

    /// <summary>Однократная анимация доиграла.</summary>
    public event EventHandler? Finished;

    private Animation? _animation;
    private Vp9.Player? _video;
    private byte[]? _videoBuffer;
    private bool _videoBusy;
    private bool _videoReleased;
    private WriteableBitmap? _bitmap;
    private TimeSpan _start;
    private int _version;

    public StickerView()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) =>
        {
            // Снова на экране (меню реакций открыли ещё раз) — однократная анимация играет заново.
            if (_videoReleased) { _videoReleased = false; Reload(); }
            else if (Once && _onceDone && _animation is null) { _onceDone = false; Reload(); }
            else UpdateActive();
        };
        Unloaded += (_, _) =>
        {
            Active.Remove(this);
            // Декодер видео-стикера держит память вне .NET — освобождаем, при возвращении на экран запустится заново.
            if (_video is null) return;
            _video.Dispose();
            _video = null;
            _videoReleased = true;
        };
        IsVisibleChanged += (_, _) => UpdateActive();
    }

    // Ошибка загрузки или разбора стикера не должна ронять приложение — стикер просто останется картинкой.
    private async void Reload()
    {
        try { await ReloadAsync(); }
        catch (Exception ex) { TgCheck.Core.Services.Logging.Write("sticker: " + ex.Message); }
    }

    private async Task ReloadAsync()
    {
        var version = ++_version;
        Active.Remove(this);
        _animation?.Dispose();
        _animation = null;
        _video?.Dispose();
        _video = null;
        var media = Media;
        // Уже показанная статичная картинка того же стикера (общая картинка эмодзи, последний кадр) остаётся — без мигания;
        // у переиспользованной ячейки сетки картинка чужая — её убираем.
        var still = Once && (_shownId == 0 || _shownId == media?.MediaId) ? Source : null;
        _shownId = media?.MediaId ?? 0;
        Source = still;
        if (media is null || AppServices.Media is not { } service) return;

        // Сначала статичный кадр — чтобы не было пустого места, пока грузится анимация.
        // Готовые картинки общие для всех копий (строки списка, сетки) — при прокрутке не декодируем заново.
        var px = (int)Math.Max(64, Math.Min(256, (double.IsNaN(Width) ? 180 : Width) * 1.5));
        if (still is not null) goto animation;
        if (!Animate && FrameCache.Get((media.MediaId, px)) is { } ready)
        {
            Source = ready;
            return;
        }
        var preview = await service.GetMessagePreviewAsync(media);
        if (version != _version) return;
        // Декодирование — вне потока интерфейса и сразу в нужном размере (превью стикера ~512px, а показываем 16–180).
        var decodeWidth = double.IsNaN(Width) ? (int?)null : Math.Max(32, (int)(Width * 2));
        var image = preview is null ? null : await Task.Run(() => Images.FromBytes(preview, decodeWidth));
        if (version != _version) return;
        if (!PlayOnce) Source = image;

        if (media.StickerFormat == StickerFormat.Static || (media.StickerFormat == StickerFormat.Video && !Animate))
        {
            if (!Animate && image is not null) FrameCache.Put((media.MediaId, px), image);
            return;
        }
        // Без анимации и без превью (иконки реакций Telegram — только анимация): рисуем один первый кадр.
        if (!Animate && image is not null)
        {
            FrameCache.Put((media.MediaId, px), image);
            return;
        }
    animation:
        if (media.StickerFormat == StickerFormat.Video)
        {
            if (Animate) await StartVideoAsync(media, service, px, version);
            return;
        }
        if (media.StickerFormat != StickerFormat.Animated) return;
        var json = await LoadJsonAsync(media, service);
        if (version != _version || json is null) return;
        var anim = await Task.Run(() => Animation.TryParse(json, out var a) ? a : null); // разбор Lottie — тоже не в потоке интерфейса
        if (version != _version) { anim?.Dispose(); return; }
        if (anim is null) return;
        _animation = anim;
        _bitmap = new WriteableBitmap(px, px, 96, 96, PixelFormats.Pbgra32, null);
        Source = _bitmap;
        _start = TimeSpan.Zero;
        RenderFrame(TimeSpan.Zero);
        if (Animate) UpdateActive();
        else
        {
            // Кадр нарисован — анимация больше не нужна; замороженную копию отдаём остальным копиям этого эмодзи.
            _animation.Dispose();
            _animation = null;
            var frozen = _bitmap.Clone();
            frozen.Freeze();
            FrameCache.Put((media.MediaId, px), frozen);
        }
    }

    // Видео-стикер: файл (webm) из кэша или с сервера, разбор и первый кадр — в фоне; дальше кадры рисуются по времени.
    private async Task StartVideoAsync(MediaHolder media, Core.Media.MediaService service, int px, int version)
    {
        // Файл — с низким приоритетом: фото, голосовые, видео и истории качаются раньше видео-стикеров и эмодзи.
        var bytes = await service.GetVideoStickerBytesAsync(media);
        if (version != _version || bytes is null) return;
        var buffer = new byte[px * px * 4];
        var player = await Task.Run(() =>
        {
            var p = Vp9.Player.Create(bytes);
            p?.DecodeInto(0, buffer, px);
            return p;
        });
        if (version != _version) { player?.Dispose(); return; }
        if (player is null) return; // декодер недоступен — остаётся статичная картинка
        _video = player;
        _videoBuffer = buffer;
        _bitmap = new WriteableBitmap(px, px, 96, 96, PixelFormats.Pbgra32, null);
        _bitmap.WritePixels(new Int32Rect(0, 0, px, px), buffer, px * 4, 0);
        Source = _bitmap;
        _start = TimeSpan.Zero;
        UpdateActive();
    }

    /// <summary>Готовые статичные кадры стикеров/эмодзи (общие для всех контролов), не больше Capacity.</summary>
    private static class FrameCache
    {
        private const int Capacity = 400;
        private static readonly Dictionary<(long, int), ImageSource> Items = [];
        private static readonly Queue<(long, int)> Order = new();

        public static ImageSource? Get((long, int) key) => Items.GetValueOrDefault(key);

        public static void Put((long, int) key, ImageSource image)
        {
            if (Items.ContainsKey(key)) return;
            Items[key] = image;
            Order.Enqueue(key);
            while (Order.Count > Capacity) Items.Remove(Order.Dequeue());
        }
    }

    private static async Task<string?> LoadJsonAsync(MediaHolder media, Core.Media.MediaService service)
    {
        lock (JsonCache)
            if (JsonCache.TryGetValue(media.MediaId, out var cached)) return cached;
        var bytes = await service.GetDocumentBytesAsync(media);
        if (bytes is null) return null;
        var json = await Task.Run(() =>
        {
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            return reader.ReadToEnd();
        });
        lock (JsonCache)
        {
            if (JsonCache.Count > 150) JsonCache.Clear();
            JsonCache[media.MediaId] = json;
        }
        return json;
    }

    private void UpdateActive()
    {
        if ((_animation is not null || _video is not null) && IsLoaded && IsVisible) Active.Add(this);
        else Active.Remove(this);
        if (!_hooked && Active.Count > 0)
        {
            _hooked = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static TimeSpan _lastTick;

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if ((now - _lastTick).TotalMilliseconds < 1000.0 / 30) return; // ~30 кадров/с достаточно
        _lastTick = now;
        foreach (var view in Active.ToArray()) view.RenderFrame(now);
        if (Active.Count == 0)
        {
            // Нечего анимировать — отписываемся, иначе WPF перерисовывает окно каждый кадр.
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    private void RenderFrame(TimeSpan now)
    {
        if (_video is not null) { RenderVideo(now); return; }
        if (_animation is null || _bitmap is null) return;
        if (_start == TimeSpan.Zero) _start = now;
        var duration = _animation.Duration.TotalSeconds;
        var elapsed = (now - _start).TotalSeconds;
        if (PlayOnce && elapsed >= duration)
        {
            Active.Remove(this);
            Visibility = Visibility.Collapsed;
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (Once && elapsed >= duration)
        {
            // Доиграла один раз — последний кадр остаётся, анимация больше не нужна.
            _onceDone = true;
            _animation.SeekFrameTime(Math.Max(0, duration - 0.001), null);
            Draw();
            Active.Remove(this);
            _animation.Dispose();
            _animation = null;
            return;
        }
        var t = duration > 0 ? elapsed % duration : 0;
        _animation.SeekFrameTime(t, null);
        Draw();
    }

    private void RenderVideo(TimeSpan now)
    {
        if (_video is null || _bitmap is null) return;
        if (_start == TimeSpan.Zero) _start = now;
        var elapsed = (now - _start).TotalSeconds;
        if ((PlayOnce || Once) && elapsed >= _video.Duration)
        {
            Active.Remove(this);
            _video.Dispose();
            _video = null;
            if (PlayOnce) { Visibility = Visibility.Collapsed; Finished?.Invoke(this, EventArgs.Empty); }
            else _onceDone = true; // последний кадр остаётся
            return;
        }
        // Кадр декодируется в фоне (окно не ждёт), сюда — только копирование готовой картинки. Один кадр за раз.
        if (_videoBusy || _videoBuffer is null) return;
        _videoBusy = true;
        var player = _video;
        var buffer = _videoBuffer;
        var bitmap = _bitmap;
        var t = elapsed % player.Duration;
        var size = bitmap.PixelWidth;
        Task.Run(() => player.DecodeInto(t, buffer, size)).ContinueWith(task =>
        {
            _videoBusy = false;
            if (!task.IsCompletedSuccessfully || !task.Result || _video != player || _bitmap != bitmap) return;
            bitmap.WritePixels(new Int32Rect(0, 0, size, size), buffer, size * 4, 0);
            if (!ReferenceEquals(Source, bitmap)) Source = bitmap;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Draw()
    {
        if (_animation is null || _bitmap is null) return;
        _bitmap.Lock();
        try
        {
            var info = new SKImageInfo(_bitmap.PixelWidth, _bitmap.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, _bitmap.BackBuffer, _bitmap.BackBufferStride);
            surface.Canvas.Clear(SKColors.Transparent);
            _animation.Render(surface.Canvas, SKRect.Create(0, 0, info.Width, info.Height));
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, info.Width, info.Height));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }
}

/// <summary>Сервисы, доступные контролам (задаются после входа).</summary>
public static class AppServices
{
    public static Core.Media.MediaService? Media { get; set; }
    public static Core.Services.TelegramService? Tg { get; set; }
}
