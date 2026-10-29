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
/// Стикер Telegram: анимированные (.tgs/Lottie) проигрываются через Skottie прямо в WriteableBitmap,
/// статичные и видео-стикеры показываются картинкой. Анимация идёт только пока контрол на экране.
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

    /// <summary>Проиграть анимацию один раз и скрыться (эффект при постановке реакции).</summary>
    public bool PlayOnce { get; set; }

    /// <summary>Однократная анимация доиграла.</summary>
    public event EventHandler? Finished;

    private Animation? _animation;
    private WriteableBitmap? _bitmap;
    private TimeSpan _start;
    private int _version;

    public StickerView()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => UpdateActive();
        Unloaded += (_, _) => { Active.Remove(this); };
        IsVisibleChanged += (_, _) => UpdateActive();
    }

    private async void Reload()
    {
        var version = ++_version;
        Active.Remove(this);
        _animation?.Dispose();
        _animation = null;
        Source = null;
        var media = Media;
        if (media is null || AppServices.Media is not { } service) return;

        // Сначала статичный кадр — чтобы не было пустого места, пока грузится анимация.
        var preview = await service.GetMessagePreviewAsync(media);
        if (version != _version) return;
        if (!PlayOnce) Source = Images.FromBytes(preview);

        if (media.StickerFormat != StickerFormat.Animated) return;
        // Без анимации и без превью (иконки реакций Telegram — только анимация): рисуем один первый кадр.
        if (!Animate && Source is not null) return;
        var json = await LoadJsonAsync(media, service);
        if (version != _version || json is null) return;
        if (!Animation.TryParse(json, out var anim) || anim is null) return;
        _animation = anim;
        var px = (int)Math.Max(64, Math.Min(256, (double.IsNaN(Width) ? 180 : Width) * 1.5));
        _bitmap = new WriteableBitmap(px, px, 96, 96, PixelFormats.Pbgra32, null);
        Source = _bitmap;
        _start = TimeSpan.Zero;
        RenderFrame(TimeSpan.Zero);
        if (Animate) UpdateActive();
        else { _animation.Dispose(); _animation = null; } // кадр нарисован — анимация больше не нужна
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
        if (_animation is not null && IsLoaded && IsVisible) Active.Add(this);
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
        var t = duration > 0 ? elapsed % duration : 0;
        _animation.SeekFrameTime(t, null);

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
