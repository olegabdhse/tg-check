using System.Collections.Concurrent;
using System.IO.Compression;
using SkiaSharp;
using SkiaSharp.Skottie;
using TgCheck.Core.Models;
using TgCheck.Core.Services;
using TL;
using WTelegram;

namespace TgCheck.Core.Media;

/// <summary>
/// Скачивает стикеры подарков и рендерит их в PNG: анимированные .tgs (Lottie) — первым кадром,
/// уникальные подарки — вместе с фоном (backdrop) и узором (pattern), как в Telegram.
/// Результат кэшируется на диске.
/// </summary>
public sealed class MediaService
{
    private readonly Func<Client> _client;
    private readonly string _cacheDir;
    private readonly LifoGate _downloads = new(8); // превью фото/GIF/документов: 4 было мало для экрана картинок
    // Аватарки маленькие и нужны сразу (первый вход — десятки строк) — своя, более широкая очередь, не за картинками сообщений.
    private readonly LifoGate _avatarDownloads = new(8);
    // Загрузчики запускаются в пуле потоков: даже чтение с диска не должно идти в UI-потоке (в группах это десятки файлов разом).
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();

    // Общий запрос на время загрузки: одновременные просьбы одной картинки ждут одну загрузку. Готовые картинки
    // держим в памяти в ограниченном кэше (последние, до 256 МБ): повторный показ мгновенный, как раньше, но память
    // не растёт без конца (раньше хранилось всё за сессию — сотни МБ).
    private Task<byte[]?> Shared(string key, Func<Task<byte[]?>> start)
    {
        if (_recent.Get(key) is { } ready) return Task.FromResult<byte[]?>(ready);
        var task = _inflight.GetOrAdd(key, _ => start());
        if (task.IsCompleted) Done(task);
        else _ = task.ContinueWith(Done, TaskScheduler.Default);
        return task;

        void Done(Task<byte[]?> t)
        {
            _inflight.TryRemove(new KeyValuePair<string, Task<byte[]?>>(key, t));
            // Запоминаем только настоящие картинки. «Нет картинки» не запоминаем: сбой при запуске (ещё нет связи) мог
            // выглядеть так же — и превью оставалось серым до перезапуска.
            if (t.IsCompletedSuccessfully && t.Result is { Length: > 0 } bytes) _recent.Put(key, bytes);
        }
    }

    private readonly BytesCache _recent = new(256L * 1024 * 1024);

    internal MediaService(Func<Client> client, string cacheDir)
    {
        _client = client;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    /// <summary>PNG-картинка подарка размером size×size (null — если не удалось получить).</summary>
    public Task<byte[]?> GetGiftImageAsync(GiftItem gift, int size)
    {
        var key = gift.ImageKey(size);
        return Shared(key, () => Task.Run(() => RenderCachedAsync(gift, size, key)));
    }

    private async Task<byte[]?> RenderCachedAsync(GiftItem gift, int size, string key)
    {
        var path = Path.Combine(_cacheDir, $"gift_{key}.png");
        try
        {
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path);

            var png = await RenderGiftAsync(gift, size);
            if (png is not null) await File.WriteAllBytesAsync(path, png);
            return png;
        }
        catch (Exception ex)
        {
            Logging.Write($"render gift {gift.GiftId}: {ex.Message}");
            _inflight.TryRemove(key, out _);
            return null;
        }
    }

    private async Task<byte[]?> RenderGiftAsync(GiftItem gift, int size)
    {
        var stickerTask = gift.Sticker is Document d ? LoadStickerAsync(d, size) : Task.FromResult<SKImage?>(null);
        var patternSize = (int)(size * 0.14);
        var patternTask = gift.PatternDocument is Document p ? LoadStickerAsync(p, patternSize * 2) : Task.FromResult<SKImage?>(null);

        using var sticker = await stickerTask;
        using var pattern = await patternTask;
        if (sticker is null && gift.CenterColor is null) return null;

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        if (gift.CenterColor is { } center && gift.EdgeColor is { } edge)
        {
            using var shader = SKShader.CreateRadialGradient(
                new SKPoint(size / 2f, size * 0.45f), size * 0.75f,
                new[] { Rgb(center), Rgb(edge) }, (float[]?)null, SKShaderTileMode.Clamp);
            using var bg = new SKPaint { Shader = shader, IsAntialias = true };
            canvas.DrawRect(0, 0, size, size, bg);

            if (pattern is not null)
                DrawPatternRing(canvas, pattern, size, gift.PatternColor is { } pc ? Rgb(pc) : SKColors.Black, sampling);
        }

        if (sticker is not null)
        {
            var inset = gift.CenterColor is null ? 0.04f : 0.2f;
            var rect = SKRect.Create(size * inset, size * inset, size * (1 - 2 * inset), size * (1 - 2 * inset));
            canvas.DrawImage(sticker, rect, sampling);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Фон уникального подарка для карточки в чате: градиент его цветов и кольца узора, без самой модели
    /// (модель поверх — живым стикером). PNG size×size, кэш на диске.
    /// </summary>
    public Task<byte[]?> GetGiftBackdropAsync(GiftCard card, int size)
    {
        if (card.CenterColor is not { } center || card.EdgeColor is not { } edge) return Task.FromResult<byte[]?>(null);
        var key = $"giftbg_{center:x6}_{edge:x6}_{card.PatternColor ?? 0:x6}_{card.Pattern?.id ?? 0}_{size}";
        return Shared(key, () => Task.Run(async () =>
        {
            var path = Path.Combine(_cacheDir, key + ".png");
            try
            {
                if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
                using var pattern = card.Pattern is { } p ? await LoadStickerAsync(p, (int)(size * 0.14) * 2) : null;
                var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                var canvas = surface.Canvas;
                using (var shader = SKShader.CreateRadialGradient(new SKPoint(size / 2f, size / 2f), size * 0.75f,
                           new[] { Rgb(center), Rgb(edge) }, (float[]?)null, SKShaderTileMode.Clamp))
                using (var bg = new SKPaint { Shader = shader, IsAntialias = true })
                    canvas.DrawRect(0, 0, size, size, bg);
                if (pattern is not null)
                    DrawPatternRing(canvas, pattern, size, card.PatternColor is { } pc ? Rgb(pc) : SKColors.Black,
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                var png = data.ToArray();
                await File.WriteAllBytesAsync(path, png);
                return png;
            }
            catch (Exception ex)
            {
                Logging.Write($"gift backdrop: {ex.Message}");
                _inflight.TryRemove(key, out var _gone);
                return null;
            }
        }));
    }

    // Узор вокруг модели: два кольца символов с затуханием к краю, как на карточках подарков в Telegram.
    private static void DrawPatternRing(SKCanvas canvas, SKImage pattern, int size, SKColor color, SKSamplingOptions sampling)
    {
        var c = new SKPoint(size / 2f, size / 2f);
        (float radius, int count, float scale, byte alpha, float phase)[] rings =
        [
            (0.31f, 6, 0.13f, 0x66, 0f),
            (0.44f, 8, 0.10f, 0x40, 22.5f),
        ];

        foreach (var ring in rings)
        {
            using var filter = SKColorFilter.CreateBlendMode(color.WithAlpha(ring.alpha), SKBlendMode.SrcIn);
            using var paint = new SKPaint { ColorFilter = filter, IsAntialias = true };
            var s = size * ring.scale;
            for (var i = 0; i < ring.count; i++)
            {
                var angle = (ring.phase + 360f / ring.count * i - 90f) * MathF.PI / 180f;
                var x = c.X + MathF.Cos(angle) * size * ring.radius;
                var y = c.Y + MathF.Sin(angle) * size * ring.radius;
                canvas.DrawImage(pattern, SKRect.Create(x - s / 2, y - s / 2, s, s), sampling, paint);
            }
        }
    }

    private async Task<SKImage?> LoadStickerAsync(Document doc, int size)
    {
        var (bytes, mime) = await DownloadDocumentAsync(doc, _stickerDownloads);
        if (bytes is null) return null;

        if (mime == "application/x-tgsticker" || IsGzip(bytes))
        {
            var image = RenderLottie(bytes, size);
            if (image is not null) return image;
        }
        else if (SKImage.FromEncodedData(bytes) is { } decoded)
        {
            return decoded;
        }

        // Видео-стикеры (webm) и прочее — берём превью.
        var thumb = doc.thumbs?.OfType<PhotoSize>().OrderByDescending(t => t.w).FirstOrDefault();
        if (thumb is null) return null;
        await _downloads.WaitAsync();
        try
        {
            using var ms = new MemoryStream();
            await Timed(_client().DownloadFileAsync(doc, ms, thumb), Small);
            await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
            return SKImage.FromEncodedData(ms.ToArray());
        }
        finally { _downloads.Release(); }
    }

    // Эмодзи — маленькие и нужны сразу десятками: своя широкая очередь и отрисовка сразу в нужном размере.
    private readonly LifoGate _emojiDownloads = new(4); // эмодзи не занимают все места загрузок — чаты и открытое пользователем идут вперёд
    private readonly LifoGate _emojiPrefetch = new(2);

    /// <summary>Картинка эмодзи Telegram (PNG нужного размера) с кэшем на диске.</summary>
    /// <param name="background">Фоновая предзагрузка набора: своя узкая очередь, не обгоняет эмодзи на экране.</param>
    public Task<byte[]?> GetEmojiImageAsync(MediaHolder emoji, int px, bool background = false)
    {
        if (emoji.Document is not { } doc) return Task.FromResult<byte[]?>(null);
        var key = $"emo_{doc.id}_{px}";
        return Shared(key, () => Task.Run(async () =>
        {
            var path = Path.Combine(_cacheDir, key + ".png");
            try
            {
                if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
                if (background) await WhilePriorityAsync();
                var (bytes, mime) = await DownloadDocumentAsync(doc, background ? _emojiPrefetch : _emojiDownloads);
                if (bytes is null) return null;
                using var image = mime == "application/x-tgsticker" || IsGzip(bytes) ? RenderLottie(bytes, px) : SKImage.FromEncodedData(bytes);
                if (image is null) return null;
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                var png = data.ToArray();
                await File.WriteAllBytesAsync(path, png);
                return png;
            }
            catch (Exception ex) { Logging.Write($"emoji {doc.id}: {ex.Message}"); _inflight.TryRemove(key, out var _); return null; }
        }));
    }

    private static SKImage? RenderLottie(byte[] tgs, int size)
    {
        string json;
        using (var gz = new GZipStream(new MemoryStream(tgs), CompressionMode.Decompress))
        using (var reader = new StreamReader(gz))
            json = reader.ReadToEnd();

        if (!Animation.TryParse(json, out var animation) || animation is null) return null;
        using (animation)
        {
            animation.Seek(0, null);
            var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(SKColors.Transparent);
            animation.Render(surface.Canvas, SKRect.Create(0, 0, size, size));
            return surface.Snapshot();
        }
    }

    // Стикеры — своя очередь: раньше стояли за фото и превью видео (общие 8 мест) и появлялись с опозданием.
    private readonly LifoGate _stickerDownloads = new(8);
    // Один и тот же документ (превью и анимация стикера) качается один раз, даже если его просят одновременно.
    private readonly ConcurrentDictionary<long, Task<(byte[]? Bytes, string? Mime)>> _docInflight = new();

    private Task<(byte[]? Bytes, string? Mime)> DownloadDocumentAsync(Document doc, LifoGate? gate = null)
    {
        var task = _docInflight.GetOrAdd(doc.id, _ => DownloadDocumentCoreAsync(doc, gate));
        _ = task.ContinueWith(_ => _docInflight.TryRemove(doc.id, out var _gone), TaskScheduler.Default);
        return task;
    }

    // Исходные файлы документов тоже кэшируются: узоры и модели повторяются у многих подарков.
    private async Task<(byte[]? Bytes, string? Mime)> DownloadDocumentCoreAsync(Document doc, LifoGate? gate)
    {
        var path = Path.Combine(_cacheDir, $"doc_{doc.id}.bin");
        if (File.Exists(path)) return (await File.ReadAllBytesAsync(path), doc.mime_type);

        gate ??= _downloads;
        await gate.WaitAsync();
        try
        {
            if (File.Exists(path)) return (await File.ReadAllBytesAsync(path), doc.mime_type);
            return await DownloadToCacheAsync(doc, path);
        }
        catch (Exception ex)
        {
            // Сбой или тайм-аут — не ошибка для вызывающих (стикер, эмодзи, подарок останутся картинкой и запросятся снова);
            // раньше исключение доходило до интерфейса и роняло приложение.
            Logging.Write($"document {doc.id}: {ex.Message}");
            return (null, null);
        }
        finally { gate.Release(); }
    }

    private async Task<(byte[]? Bytes, string? Mime)> DownloadToCacheAsync(Document doc, string path)
    {
        using var ms = new MemoryStream();
        var mime = await Timed(_client().DownloadFileAsync(doc, ms), Small);
        await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
        var bytes = ms.ToArray();
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return (bytes, mime ?? doc.mime_type);
    }

    // Мелкие загрузки (превью, стикеры, аватары, эмодзи) не ждут вечно: после обрыва связи зависшая загрузка держала
    // место в очереди навсегда, и новые стикеры и картинки переставали грузиться. Ошибка — повтор при следующем показе.
    // Мелкие файлы приходят за секунды: нет ответа 20 с — соединение с медиасервером оборвалось, быстрее повторить.
    private const int Small = 20;

    private static async Task<T> Timed<T>(Task<T> download, int seconds = 60)
    {
        if (await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(seconds))) != download)
        {
            _ = download.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException("загрузка не ответила за " + seconds + " с");
        }
        return await download;
    }

    /// <summary>Связь переподключена: незавершённые загрузки старого соединения не ждём — следующий показ скачает заново.</summary>
    public void ForgetPending()
    {
        foreach (var (key, task) in _inflight)
            if (!task.IsCompleted) _inflight.TryRemove(key, out _);
    }

    private static bool IsGzip(byte[] b) => b.Length > 2 && b[0] == 0x1F && b[1] == 0x8B;

    private static SKColor Rgb(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ===================================================================== Аватары

    /// <summary>Маленький аватар отправителя подарка (jpeg) или null, если фото нет.</summary>
    public Task<byte[]?> GetSenderAvatarAsync(GiftItem gift)
    {
        if (!gift.HasSenderPhoto || gift.FromPeer is not { } peer) return Task.FromResult<byte[]?>(null);
        var photoId = peer switch
        {
            User u => u.photo?.photo_id ?? 0,
            Channel c => (c.photo as ChatPhoto)?.photo_id ?? 0,
            Chat c => (c.photo as ChatPhoto)?.photo_id ?? 0,
            _ => 0,
        };
        var key = $"ava_{peer.ID}_{photoId}";
        return Shared(key, () => Task.Run(() => LoadAvatarAsync(peer, key)));
    }

    private async Task<byte[]?> LoadAvatarAsync(IPeerInfo peer, string key, bool big = false)
    {
        var path = Path.Combine(_cacheDir, key + ".jpg");
        try
        {
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
            await _avatarDownloads.WaitAsync();
            try
            {
                using var ms = new MemoryStream();
                await Timed(_client().DownloadProfilePhotoAsync(peer, ms, big: big), Small);
                await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
                if (ms.Length == 0) return null;
                var bytes = ms.ToArray();
                await File.WriteAllBytesAsync(path, bytes);
                return bytes;
            }
            finally { _avatarDownloads.Release(); }
        }
        catch (Exception ex)
        {
            Logging.Write($"avatar {peer.ID}: {ex.Message}");
            _inflight.TryRemove(key, out _);
            return null;
        }
    }

    /// <summary>Большое фото профиля (для страницы профиля) — с кэшем на диске: повторное открытие профиля мгновенное.</summary>
    public Task<byte[]?> GetPeerAvatarBigAsync(PeerRef peer)
    {
        if (!peer.HasPhoto) return Task.FromResult<byte[]?>(null);
        var photoId = peer.Info switch
        {
            User u => u.photo?.photo_id ?? 0,
            Channel c => (c.photo as ChatPhoto)?.photo_id ?? 0,
            Chat c => (c.photo as ChatPhoto)?.photo_id ?? 0,
            _ => 0,
        };
        var key = $"avabig_{peer.Info.ID}_{photoId}";
        return Shared(key, () => Task.Run(() => LoadAvatarAsync(peer.Info, key, big: true)));
    }

    /// <summary>Маленький аватар пользователя/группы/канала (jpeg) или null.</summary>
    public Task<byte[]?> GetPeerAvatarAsync(PeerRef peer)
    {
        if (!peer.HasPhoto) return Task.FromResult<byte[]?>(null);
        var photoId = peer.Info switch
        {
            User u => u.photo?.photo_id ?? 0,
            Channel c => (c.photo as ChatPhoto)?.photo_id ?? 0,
            Chat c => (c.photo as ChatPhoto)?.photo_id ?? 0,
            _ => 0,
        };
        var key = $"ava_{peer.Info.ID}_{photoId}";
        return Shared(key, () => Task.Run(() => LoadAvatarAsync(peer.Info, key)));
    }

    /// <summary>
    /// Картинка для пузыря сообщения: фото (~800px), превью видео/GIF, стикер (PNG с прозрачностью).
    /// </summary>
    /// <summary>Превью документа уже скачано (лежит в кэше на диске).</summary>
    public bool HasCachedDocumentPreview(long documentId) => File.Exists(Path.Combine(_cacheDir, $"msgdoc_{documentId}.img"));

    public Task<byte[]?> GetMessagePreviewAsync(MediaHolder message)
    {
        var key = message.Photo is { } p ? $"msgphoto_{p.id}" : message.Document is { } d ? $"msgdoc_{d.id}" : null;
        if (key is null) return Task.FromResult<byte[]?>(null);
        return Shared(key, () => Task.Run(() => LoadMessagePreviewAsync(message, key)));
    }

    private async Task<byte[]?> LoadMessagePreviewAsync(MediaHolder message, string key)
    {
        var path = Path.Combine(_cacheDir, key + ".img");
        try
        {
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
            byte[]? bytes = null;
            if (message.Photo is { } photo)
            {
                bytes = await DownloadPhotoAsync(message, photo, p => p.sizes.OfType<PhotoSize>().Where(s => s.w <= 1280).MaxBy(s => s.w)
                                                                     ?? p.LargestPhotoSize, _downloads);
            }
            else if (message.Document is { } doc)
            {
                if (message is StickerItem || message is MessageItem { Media: MediaKind.Sticker })
                {
                    using var img = await LoadStickerAsync(doc, 256);
                    if (img is not null)
                    {
                        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                        bytes = data.ToArray();
                    }
                }
                else if (doc.thumbs?.OfType<PhotoSize>().Any() == true)
                {
                    bytes = await DownloadDocThumbAsync(message, doc);
                }
                else if (doc.thumbs?.OfType<PhotoCachedSize>().FirstOrDefault() is { } cached)
                {
                    bytes = cached.bytes;
                }
                else if (doc.thumbs?.OfType<PhotoStrippedSize>().FirstOrDefault() is { } stripped)
                {
                    bytes = ExpandStripped(stripped.bytes);
                }
            }
            if (bytes is { Length: > 0 }) await File.WriteAllBytesAsync(path, bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            Logging.Write($"message preview {key}: {ex.Message}");
            _inflight.TryRemove(key, out _);
            return null;
        }
    }

    // ===================================================================== Обложка песни

    /// <summary>
    /// Обложка альбома для песни без встроенной картинки — из открытых каталогов без ключей: сначала iTunes,
    /// не нашлось — Deezer (в нём много русской музыки). Ищем по «исполнитель + название», затем по одному названию;
    /// мусор вроде «(lmusic.kz)» из названия убирается. Ответ — и «не найдено» тоже — кэшируется на диске.
    /// </summary>
    public Task<byte[]?> GetSongCoverAsync(string? performer, string title)
    {
        var song = CleanSongTitle(title);
        var artist = string.IsNullOrWhiteSpace(performer) ? null : CleanSongTitle(performer);
        var full = string.Join(' ', new[] { artist, song }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (full.Length < 3) return Task.FromResult<byte[]?>(null);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..16];
        var key = "cover2_" + hash;
        return Shared(key, () => Task.Run(async () =>
        {
            var path = Path.Combine(_cacheDir, key + ".jpg");
            var miss = Path.Combine(_cacheDir, key + ".none");
            try
            {
                if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
                if (File.Exists(miss)) return null;
                var terms = artist is null || song.Length < 3 ? [full] : new[] { full, song };
                string? artUrl = null;
                foreach (var term in terms)
                {
                    artUrl = await FindITunesCoverAsync(term) ?? await FindDeezerCoverAsync(term);
                    if (artUrl is not null) break;
                }
                if (artUrl is null)
                {
                    await File.WriteAllTextAsync(miss, "");
                    return null;
                }
                var bytes = await MapHttp.GetByteArrayAsync(artUrl);
                await File.WriteAllBytesAsync(path, bytes);
                return bytes;
            }
            catch (Exception ex)
            {
                Logging.Write($"song cover: {ex.Message}");
                _inflight.TryRemove(key, out var _gone);
                return null;
            }
        }));
    }

    private static async Task<string?> FindITunesCoverAsync(string term)
    {
        try
        {
            var url = "https://itunes.apple.com/search?media=music&entity=song&limit=1&term=" + Uri.EscapeDataString(term);
            using var json = System.Text.Json.JsonDocument.Parse(await MapHttp.GetStringAsync(url));
            var results = json.RootElement.GetProperty("results");
            return results.GetArrayLength() > 0 && results[0].TryGetProperty("artworkUrl100", out var art) && art.GetString() is { } a
                ? a.Replace("100x100", "600x600") : null;
        }
        catch (Exception ex) { Logging.Write($"song cover itunes: {ex.Message}"); return null; }
    }

    private static async Task<string?> FindDeezerCoverAsync(string term)
    {
        try
        {
            var url = "https://api.deezer.com/search?limit=1&q=" + Uri.EscapeDataString(term);
            using var json = System.Text.Json.JsonDocument.Parse(await MapHttp.GetStringAsync(url));
            if (!json.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return null;
            return data[0].TryGetProperty("album", out var album) && album.TryGetProperty("cover_big", out var cover) && cover.GetString() is { Length: > 0 } c
                ? c : null;
        }
        catch (Exception ex) { Logging.Write($"song cover deezer: {ex.Message}"); return null; }
    }

    // «без тебя мир некрасивый (lmusic.kz)» → «без тебя мир некрасивый»: сайты, «official audio» и т.п. мешают поиску.
    private static string CleanSongTitle(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\(\[][^\)\]]*(\.\w{2,4}|official|audio|video|lyrics|remaster|prod\.?)[^\)\]]*[\)\]]", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b[\w-]+\.(kz|ru|com|net|org|me|fm)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '_', '.');
    }

    // ===================================================================== Карта для геопозиции

    private static readonly HttpClient MapHttp = CreateMapHttp();

    private static HttpClient CreateMapHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TgCheck/1.0 (desktop Telegram client)");
        return http;
    }

    /// <summary>
    /// Картинка карты для геопозиции: как официальные клиенты — через сервер Telegram (upload.getWebFile),
    /// при неудаче — тайл OpenStreetMap. Кэшируется на диске.
    /// </summary>
    public Task<byte[]?> GetMapAsync(MessageItem item, int w = 300, int h = 170, int zoom = 15)
    {
        if (item.Latitude is not { } lat || item.Longitude is not { } lon) return Task.FromResult<byte[]?>(null);
        var key = $"map_{lat:F5}_{lon:F5}_{w}x{h}_{zoom}".Replace(',', '.');
        return Shared(key, () => Task.Run(() => LoadMapAsync(item, lat, lon, w, h, zoom, key)));
    }

    private async Task<byte[]?> LoadMapAsync(MessageItem item, double lat, double lon, int w, int h, int zoom, string key)
    {
        var path = Path.Combine(_cacheDir, key + ".img");
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
        byte[]? bytes = null;
        try
        {
            var client = _client();
            var dc = client.TLConfig?.webfile_dc_id ?? 0;
            var target = dc > 0 ? await client.GetClientForDC(dc, true) : client;
            var file = await target.Upload_GetWebFile(new InputWebFileGeoPointLocation
            {
                geo_point = new InputGeoPoint { lat = lat, lon = lon },
                access_hash = item.GeoAccessHash, w = w, h = h, zoom = zoom, scale = 2,
            }, 0, 512 * 1024);
            if (file.bytes is { Length: > 0 }) bytes = file.bytes;
        }
        catch (Exception ex) { Logging.Write("tg map: " + ex.Message); }

        if (bytes is null)
        {
            try
            {
                // Тайл OSM, в центре которого точка (упрощённо — без склейки соседних тайлов).
                var n = Math.Pow(2, zoom);
                var x = (int)((lon + 180) / 360 * n);
                var latRad = lat * Math.PI / 180;
                var y = (int)((1 - Math.Log(Math.Tan(latRad) + 1 / Math.Cos(latRad)) / Math.PI) / 2 * n);
                bytes = await MapHttp.GetByteArrayAsync($"https://tile.openstreetmap.org/{zoom}/{x}/{y}.png");
            }
            catch (Exception ex) { Logging.Write("osm map: " + ex.Message); }
        }
        if (bytes is { Length: > 0 }) await File.WriteAllBytesAsync(path, bytes);
        else _inflight.TryRemove(key, out _);
        return bytes;
    }

    // ===================================================================== Полные файлы для воспроизведения

    private readonly ConcurrentDictionary<long, Task<string?>> _files = new();

    /// <summary>
    /// Скачивает документ (видео, GIF, голосовое, аудио) во внутренний кэш и возвращает путь —
    /// для воспроизведения без «Сохранить как». Повторные вызовы отдают тот же файл.
    /// </summary>
    /// <param name="priority">true — открыто пользователем (видео, история, голосовое): без очереди. false — фоновая загрузка (GIF, кружки).</param>
    /// <summary>Файл (видео и т.п.) уже скачан целиком — можно играть сразу, не загружая.</summary>
    public bool IsMediaFileCached(MediaHolder media)
    {
        if (media.Document is not { } doc) return false;
        var dir = Path.Combine(_cacheDir, "media");
        if (!Directory.Exists(dir)) return false;
        return Directory.EnumerateFiles(dir, $"{doc.id}.*").Any(f => !f.EndsWith(".part") && new FileInfo(f).Length == doc.size);
    }

    private readonly ConcurrentDictionary<long, bool> _cancelledFiles = new();
    // Открытое пользователем (песня, видео) качается — фоновые загрузки (голосовые на экране, набор эмодзи) ждут.
    private int _priorityActive;

    // Качается видео истории (открыта в просмотрщике): ему — вся скорость, загрузка песни ждёт. Превью историй
    // (сетка в профиле, полоса историй) сюда не относятся — иначе песня ждала, пока грузится любая картинка истории.
    private int _storyActive;

    private async Task<T> AsStory<T>(MediaHolder media, Func<Task<T>> load)
    {
        if (media is not StoryInfo) return await load();
        Interlocked.Increment(ref _storyActive);
        try { return await load(); }
        finally { Interlocked.Decrement(ref _storyActive); }
    }

    private async Task WhilePriorityAsync()
    {
        while (Volatile.Read(ref _priorityActive) > 0) await Task.Delay(250);
    }
    // Файлы, которые качаются прямо сейчас (путь к .part) — их можно читать на ходу.
    private readonly ConcurrentDictionary<long, string> _partPaths = new();

    /// <summary>
    /// Песня для воспроизведения сразу, не дожидаясь полной загрузки: поток читает файл по мере скачивания
    /// (если плеер догнал загрузку — ждёт). Уже скачанное — обычный файл.
    /// </summary>
    public async Task<Stream?> OpenMediaStreamAsync(MediaHolder media)
    {
        if (media.Document is not { } doc) return null;
        if (doc.mime_type is { } mime && (mime.StartsWith("audio/") || mime.StartsWith("video/")) && doc.size > 0 && OpenStreaming(media, doc) is { } streaming)
            return streaming;
        var done = GetMediaFileAsync(media);
        // Ждём начала загрузки и первых ~64 КБ (заголовок файла), либо конца загрузки.
        for (var i = 0; i < 400 && !done.IsCompleted; i++)
        {
            if (_partPaths.TryGetValue(doc.id, out var part) && File.Exists(part) && new FileInfo(part).Length >= Math.Min(65536, doc.size))
            {
                try { return new GrowingFileStream(part, doc.size, done); }
                catch (IOException) { break; } // файл успели переименовать — значит, докачан
            }
            await Task.Delay(50);
        }
        var path = await done;
        return path is null ? null : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    // Песни и видео (истории) — потоком по требованию, как в Telegram Desktop: проигрыватель сразу получает поток,
    // нужные ему места качаются первыми, и воспроизведение начинается, не дожидаясь файла целиком.
    private readonly ConcurrentDictionary<long, StreamingDownload> _streams = new();

    // Песни уступают только истории, которую смотрят прямо сейчас (брошенная при листании стоит на паузе и не в счёт).
    private readonly ConcurrentDictionary<long, StreamingDownload> _storyStreams = new();
    private bool StoryIsLoading() => Volatile.Read(ref _storyActive) > 0 || _storyStreams.Values.Any(s => s.IsReading);

    private Stream? OpenStreaming(MediaHolder media, Document doc)
    {
        var path = MediaFilePath(doc);
        if (File.Exists(path) && new FileInfo(path).Length == doc.size)
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return StartStreaming(media, doc) is { } download ? new StreamingReadStream(download) : null;
    }

    /// <summary>
    /// Тихо подкачать начало песни (следующей в очереди), чтобы переход на неё был почти мгновенным: только первые
    /// <paramref name="bytes"/> байт и только пока их никто не слушает — дальше загрузка встаёт на паузу до воспроизведения.
    /// </summary>
    public void Prefetch(MediaHolder media, long bytes)
    {
        if (media.Document is not { size: > 0 } doc) return;
        var path = MediaFilePath(doc);
        if (File.Exists(path) && new FileInfo(path).Length == doc.size) return;
        StartStreaming(media, doc)?.Prefetch(bytes);
    }

    // Загрузка по требованию для файла: уже идущая или новая (null — файл качается обычным способом).
    private StreamingDownload? StartStreaming(MediaHolder media, Document doc)
    {
        var path = MediaFilePath(doc);
        if (_streams.TryGetValue(doc.id, out var running) && !running.Done.IsCompleted) return running;
        if (_partPaths.ContainsKey(doc.id)) return null; // уже качается обычным способом — читаем его, как раньше
        _cancelledFiles.TryRemove(doc.id, out _);
        var part = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        var download = new StreamingDownload(_client, doc, part, path,
            () => RefreshDocument?.Invoke(media) ?? Task.FromResult<Document?>(null),
            // Видео истории сам никому не уступает; песни и прочее уступают ему.
            media is StoryInfo ? () => false : StoryIsLoading,
            () => _cancelledFiles.ContainsKey(doc.id));
        try { download.Start(); }
        catch (Exception ex)
        {
            Logging.Write($"stream {doc.id}: {ex.Message}");
            return null;
        }
        _streams[doc.id] = download;
        if (media is StoryInfo) _storyStreams[doc.id] = download;
        _files[doc.id] = download.Done; // другие запросы этого файла ждут эту же загрузку, а не качают заново
        _ = download.Done.ContinueWith(t =>
        {
            _streams.TryRemove(doc.id, out _);
            _storyStreams.TryRemove(doc.id, out _);
            if (t.Result is null) _files.TryRemove(doc.id, out _);
        }, TaskScheduler.Default);
        return download;
    }

    /// <summary>Остановить загрузку файла (крестик на ободке). Следующий запрос начнёт заново.</summary>
    public void CancelMediaFile(MediaHolder media)
    {
        if (media.Document is { } doc) _cancelledFiles[doc.id] = true;
    }

    /// <summary>Взять свежую ссылку на файл (сообщение или профиль запросить заново). Задаёт TelegramService.</summary>
    internal Func<MediaHolder, Task<Document?>>? RefreshDocument { get; set; }

    // Скачать; если ссылка на файл устарела (кэш, давно открытый чат) — один раз берём свежую и повторяем.
    private async Task<string?> DownloadFreshAsync(MediaHolder media, Document doc, IProgress<double>? progress, bool priority)
    {
        try { return await DownloadMediaFileAsync(doc, progress, priority); }
        catch (FileReferenceExpiredException)
        {
            if (RefreshDocument is null) return null;
            Document? fresh = null;
            try { fresh = await RefreshDocument(media); }
            catch (Exception ex) { Logging.Write($"refresh file reference: {ex.Message}"); }
            if (fresh is null) return null;
            Logging.Write($"media file {doc.id}: ссылка обновлена, повторяем");
            try { return await DownloadMediaFileAsync(fresh, progress, priority); }
            catch (FileReferenceExpiredException) { return null; }
        }
    }

    public Task<string?> GetMediaFileAsync(MediaHolder media, IProgress<double>? progress = null, bool priority = true)
    {
        if (media.Document is not { } doc) return Task.FromResult<string?>(null);
        // Уже идёт потоковая загрузка (песня, история) — нужен файл целиком: пусть докачивается, ждём её.
        if (_streams.TryGetValue(doc.id, out var streaming) && !streaming.Done.IsCompleted)
        {
            streaming.Keep();
            return streaming.Done;
        }
        var task = _files.GetOrAdd(doc.id, _ => Task.Run(() => AsStory(media, () => DownloadFreshAsync(media, doc, progress, priority))));
        if (task.IsFaulted || (task.IsCompleted && task.Result is null)) _files.TryRemove(doc.id, out _);
        return task;
    }

    private readonly ConcurrentDictionary<long, Task<byte[]?>> _fullPhotos = new();

    /// <summary>
    /// Фото в полном размере для просмотрщика: кэш на диске + общий запрос, так что соседние фото можно
    /// подгрузить заранее (листание стрелками без ожидания).
    /// </summary>
    public Task<byte[]?> GetFullPhotoAsync(MediaHolder media, bool priority = true)
    {
        if (media.Photo is not { } photo) return Task.FromResult<byte[]?>(null);
        var task = _fullPhotos.GetOrAdd(photo.id, _ => Task.Run(() => DownloadFullPhotoAsync(media, photo, priority)));
        if (task.IsFaulted || (task.IsCompleted && task.Result is null)) _fullPhotos.TryRemove(photo.id, out _);
        // В памяти держим немного — остальное читается с диска.
        if (_fullPhotos.Count > 12)
            foreach (var key in _fullPhotos.Where(p => p.Value.IsCompleted && p.Key != photo.id).Select(p => p.Key).Take(_fullPhotos.Count - 12).ToList())
                _fullPhotos.TryRemove(key, out _);
        return task;
    }

    // ---- устаревшие ссылки на фото и превью: берём сообщение заново и повторяем; свежую ссылку запоминаем
    //      (превью и полное фото того же сообщения следом качаются уже по ней).

    /// <summary>Свежая ссылка на фото сообщения. Задаёт TelegramService.</summary>
    internal Func<MediaHolder, Task<Photo?>>? RefreshPhoto { get; set; }
    private readonly ConcurrentDictionary<long, Photo> _freshPhotos = new();
    private readonly ConcurrentDictionary<long, Document> _freshDocs = new();

    private static bool IsReferenceExpired(Exception ex) =>
        ex.Message.Contains("FILE_REFERENCE_") || ex.InnerException?.Message.Contains("FILE_REFERENCE_") == true;

    // gate — очередь загрузок (превью); на время повторного запроса сообщения слот не занимаем — видимое не ждёт.
    private async Task<byte[]> DownloadPhotoAsync(MediaHolder holder, Photo photo, Func<Photo, PhotoSizeBase> size, LifoGate? gate = null)
    {
        photo = _freshPhotos.GetValueOrDefault(photo.id) ?? photo;
        try { return await LoadPhotoAsync(photo, size(photo), gate); }
        catch (Exception ex) when (IsReferenceExpired(ex) && RefreshPhoto is not null)
        {
            if (await RefreshPhoto(holder) is not { } fresh) throw;
            _freshPhotos[fresh.id] = fresh;
            return await LoadPhotoAsync(fresh, size(fresh), gate);
        }
    }

    private async Task<byte[]> LoadPhotoAsync(Photo photo, PhotoSizeBase size, LifoGate? gate)
    {
        if (gate is not null) await gate.WaitAsync();
        try
        {
            using var ms = new MemoryStream();
            await Timed(_client().DownloadFileAsync(photo, ms, size), 30);
            await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
            return ms.ToArray();
        }
        finally { gate?.Release(); }
    }

    private async Task<byte[]?> DownloadDocThumbAsync(MediaHolder holder, Document doc)
    {
        doc = _freshDocs.GetValueOrDefault(doc.id) ?? doc;
        try { return await LoadDocThumbAsync(doc); }
        catch (Exception ex) when (IsReferenceExpired(ex) && RefreshDocument is not null)
        {
            if (await RefreshDocument(holder) is not { } fresh) throw;
            _freshDocs[fresh.id] = fresh;
            return await LoadDocThumbAsync(fresh);
        }
    }

    private async Task<byte[]?> LoadDocThumbAsync(Document doc)
    {
        if (doc.thumbs?.OfType<PhotoSize>().MaxBy(t => t.w) is not { } thumb) return null;
        await _downloads.WaitAsync();
        try
        {
            using var ms = new MemoryStream();
            await Timed(_client().DownloadFileAsync(doc, ms, thumb), Small);
            await Task.Yield();
            return ms.ToArray();
        }
        finally { _downloads.Release(); }
    }

    private async Task<byte[]?> DownloadFullPhotoAsync(MediaHolder media, Photo photo, bool priority)
    {
        var dir = Path.Combine(_cacheDir, "media");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"p{photo.id}.jpg");
        try
        {
            if (File.Exists(path))
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return await File.ReadAllBytesAsync(path);
            }
            if (!priority) await _backgroundFiles.WaitAsync();
            try
            {
                var bytes = await DownloadPhotoAsync(media, photo, p => p.LargestPhotoSize);
                await File.WriteAllBytesAsync(path, bytes);
                return bytes;
            }
            finally
            {
                if (!priority) _backgroundFiles.Release();
            }
        }
        catch (Exception ex)
        {
            Logging.Write("full photo: " + ex.Message);
            return null;
        }
    }

    private readonly SemaphoreSlim _backgroundFiles = new(2, 2);

    // Путь готового файла в кэше: {id}.{расширение по типу}.
    private string MediaFilePath(Document doc)
    {
        var dir = Path.Combine(_cacheDir, "media");
        Directory.CreateDirectory(dir);
        var ext = doc.mime_type switch
        {
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/webm" => ".webm",
            "audio/ogg" => ".ogg",
            "audio/mpeg" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "application/x-tgsticker" => ".tgs",
            _ => Path.GetExtension(doc.Filename ?? "") is { Length: > 0 } e ? e : ".bin",
        };
        return Path.Combine(dir, $"{doc.id}{ext}");
    }

    private async Task<string?> DownloadMediaFileAsync(Document doc, IProgress<double>? progress, bool priority)
    {
        var isAudio = doc.mime_type?.StartsWith("audio/") == true;
        var path = MediaFilePath(doc);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == doc.size)
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return path;
            }
            _cancelledFiles.TryRemove(doc.id, out _);
            var part = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            if (!priority) { await WhilePriorityAsync(); await _backgroundFiles.WaitAsync(); }
            else Interlocked.Increment(ref _priorityActive);
            try
            {
                // Файл открыт для чтения другими (песня играет, пока качается); запись строго по порядку — без «дыр».
                await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                await using (var ordered = new OrderedFileStream(fs))
                {
                    _partPaths[doc.id] = part;
                    if (isAudio) await DownloadAudioAsync(doc, ordered, progress);
                    else await _client().DownloadFileAsync(doc, ordered, (PhotoSizeBase?)null,
                        (done, total) =>
                        {
                            // Крестик на ободке загрузки — остановить (исключение из обратного вызова прерывает загрузку).
                            if (_cancelledFiles.ContainsKey(doc.id)) throw new OperationCanceledException();
                            progress?.Report(total > 0 ? (double)done / total : 0);
                        });
                }
                await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
            }
            finally
            {
                if (!priority) _backgroundFiles.Release();
                else Interlocked.Decrement(ref _priorityActive);
                _partPaths.TryRemove(doc.id, out _);
            }
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            Logging.Write($"media file {doc.id}: {ex.Message}");
            _files.TryRemove(doc.id, out _);
            foreach (var leftover in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, $"{doc.id}*.part")) try { File.Delete(leftover); } catch { }
            // Ссылка на файл устарела (Telegram выдаёт их на время) — пусть вызывающий возьмёт свежую и повторит.
            if (ex is RpcException { Message: "FILE_REFERENCE_EXPIRED" }) throw new FileReferenceExpiredException();
            return null;
        }
    }

    // Песни качаем своими частями по 1 МБ (до 12 одновременно): перед каждой частью — уступить видео истории.
    // Иначе песня занимала все параллельные загрузки, и истории ждали её целиком. Ожидание — асинхронное,
    // сетевые потоки библиотеки не блокируются.
    private async Task DownloadAudioAsync(Document doc, Stream output, IProgress<double>? progress)
    {
        // Части по 1 МБ (максимум, что отдаёт Telegram за запрос): вдвое меньше запросов и вдвое больше данных в пути,
        // 12 частей одновременно — как у библиотеки.
        const int Chunk = 1024 * 1024;
        const int ParallelParts = 12;
        long done = 0;
        try
        {
            var dc = await _client().GetClientForDC(-doc.dc_id, true);
            var location = new InputDocumentFileLocation { id = doc.id, access_hash = doc.access_hash, file_reference = doc.file_reference, thumb_size = "" };
            var running = new List<Task>();
            // Первый мегабайт — частями по 128 КБ: начало песни приходит быстро, и она начинает играть сразу.
            const int Head = 128 * 1024;
            for (long offset = 0; offset < doc.size;)
            {
                var limit = offset < Chunk ? Head : Chunk;
                while (running.Count >= ParallelParts)
                {
                    var finished = await Task.WhenAny(running);
                    running.Remove(finished);
                    await finished;
                }
                running.Add(Part(dc, location, offset, limit));
                offset += limit;
            }
            await Task.WhenAll(running);
        }
        catch (Exception ex) when (Interlocked.Read(ref done) == 0 && ex is not OperationCanceledException && !ex.Message.Contains("FILE_REFERENCE"))
        {
            // Свой загрузчик не завёлся (ничего ещё не записано) — качаем как раньше, средствами библиотеки.
            Logging.Write($"audio {doc.id}: {ex.Message}, качаем обычным способом");
            await _client().DownloadFileAsync(doc, output, (PhotoSizeBase?)null);
        }

        async Task Part(WTelegram.Client dc, InputDocumentFileLocation location, long offset, int limit)
        {
            while (StoryIsLoading() && !_cancelledFiles.ContainsKey(doc.id)) await Task.Delay(100);
            if (_cancelledFiles.ContainsKey(doc.id)) throw new OperationCanceledException();
            if (await dc.Upload_GetFile(location, offset, limit) is not Upload_File file) throw new IOException("файл песни отдаётся через CDN");
            lock (output)
            {
                output.Position = offset;
                output.Write(file.bytes);
            }
            progress?.Report(doc.size > 0 ? (double)Interlocked.Add(ref done, file.bytes.Length) / doc.size : 0);
        }
    }

    /// <summary>Исходные байты документа (например, .tgs для анимации стикера).</summary>
    // Видео-стикеры и видео-эмодзи (webm): с низким приоритетом — пока качается открытое (фото, голосовое, видео, история),
    // они ждут, и одновременно их качается не больше двух.
    private readonly LifoGate _videoStickerDownloads = new(2);

    public async Task<byte[]?> GetVideoStickerBytesAsync(MediaHolder media)
    {
        if (media.Document is not { } doc) return null;
        var (bytes, _) = await Task.Run(async () =>
        {
            await WhilePriorityAsync();
            return await DownloadDocumentAsync(doc, _videoStickerDownloads);
        });
        return bytes;
    }

    public async Task<byte[]?> GetDocumentBytesAsync(MediaHolder media)
    {
        if (media.Document is not { } doc) return null;
        var (bytes, _) = await Task.Run(() => DownloadDocumentAsync(doc, _stickerDownloads));
        return bytes;
    }

    /// <summary>Ограничивает размер кэша медиа (самые давно открытые файлы удаляются первыми).</summary>
    public void TrimMediaCache(long maxBytes = 2L * 1024 * 1024 * 1024)
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(_cacheDir, "media"));
            if (!dir.Exists) return;
            var files = dir.GetFiles().OrderByDescending(f => f.LastAccessTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > maxBytes || f.Name.EndsWith(".part")) f.Delete();
            }
        }
        catch (Exception ex) { Logging.Write("trim cache: " + ex.Message); }
    }

    /// <summary>Полноразмерное фото профиля.</summary>
    public async Task<byte[]?> GetProfilePhotoAsync(ProfilePhoto photo)
    {
        var path = Path.Combine(_cacheDir, $"photo_{photo.Photo.id}.jpg");
        try
        {
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
            await _downloads.WaitAsync();
            try
            {
                using var ms = new MemoryStream();
                await Timed(_client().DownloadFileAsync(photo.Photo, ms, photo.Photo.LargestPhotoSize));
                await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
                var bytes = ms.ToArray();
                await File.WriteAllBytesAsync(path, bytes);
                return bytes;
            }
            finally { _downloads.Release(); }
        }
        catch (Exception ex)
        {
            Logging.Write($"profile photo {photo.Photo.id}: {ex.Message}");
            return null;
        }
    }

    // Telegram «stripped»-миниатюра: JPEG без стандартных заголовков (алгоритм из официальных клиентов).
    private static byte[]? ExpandStripped(byte[] s)
    {
        if (s.Length < 3 || s[0] != 1) return null;
        const string header = "ffd8ffe000104a46494600010100000100010000ffdb004300281c1e231e19282321232d2b28303c64413c37373c7b585d4964918099968f808c8aa0b4e6c3a0aadaad8a8cc8ffcbdaeef5ffffff9bc1fffffffaffe6fdfff8ffdb0043012b2d2d3c353c76414176f8a58ca5f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8f8ffc0001108001e002803012200021101031101ffc4001f0000010501010101010100000000000000000102030405060708090a0bffc400b5100002010303020403050504040000017d01020300041105122131410613516107227114328191a1082342b1c11552d1f02433627282090a161718191a25262728292a3435363738393a434445464748494a535455565758595a636465666768696a737475767778797a838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9faffc4001f0100030101010101010101010000000000000102030405060708090a0bffc400b51100020102040403040705040400010277000102031104052131061241510761711322328108144291a1b1c109233352f0156272d10a162434e125f11718191a262728292a35363738393a434445464748494a535455565758595a636465666768696a737475767778797a82838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae2e3e4e5e6e7e8e9eaf2f3f4f5f6f7f8f9faffda000c03010002110311003f00";
        var head = Convert.FromHexString(header);
        head[164] = s[1];
        head[166] = s[2];
        var result = new byte[head.Length + s.Length - 3 + 2];
        head.CopyTo(result, 0);
        Array.Copy(s, 3, result, head.Length, s.Length - 3);
        result[^2] = 0xFF;
        result[^1] = 0xD9;
        return result;
    }

    /// <summary>
    /// Очистка кэша с сохранением нужного: картинки эмодзи (emo_) — всегда; документы и превью — из keepDocs
    /// (стикеры, эмодзи, медиа сохраняемых чатов); фото — из keepPhotos; аватары — пиров из keepPeers. Возвращает освобождённые байты.
    /// </summary>
    public long ClearCacheExcept(IReadOnlySet<long> keepDocs, IReadOnlySet<long> keepPhotos, IReadOnlySet<long> keepPeers, IProgress<double>? progress = null)
    {
        _recent.Clear();
        // Забываем и то, что держим в памяти о файлах на диске (готовые пути, полные фото, завершённые загрузки) —
        // иначе после очистки отдавались бы пути к удалённым файлам. Идущие сейчас загрузки не трогаем.
        static void DropDone<TKey, TValue>(ConcurrentDictionary<TKey, Task<TValue>> map) where TKey : notnull
        {
            foreach (var (key, task) in map)
                if (task.IsCompleted) map.TryRemove(key, out _);
        }
        DropDone(_inflight);
        DropDone(_docInflight);
        DropDone(_files);
        DropDone(_fullPhotos);
        long freed = 0;
        var media = Path.Combine(_cacheDir, "media");
        var files = Directory.EnumerateFiles(_cacheDir).ToList();
        var mediaFiles = Directory.Exists(media) ? Directory.EnumerateFiles(media).ToList() : [];
        var total = Math.Max(1, files.Count + mediaFiles.Count);
        var done = 0;
        void Step() { if (++done % 40 == 0) progress?.Report((double)done / total); }
        static long IdAt(string name, int index) => name.Split('_', '.') is var parts && parts.Length > index && long.TryParse(parts[index], out var id) ? id : 0;
        foreach (var path in files)
        {
            Step();
            var name = Path.GetFileName(path);
            var keep = name.StartsWith("emo_")
                || (name.StartsWith("doc_") && keepDocs.Contains(IdAt(name, 1)))
                || (name.StartsWith("msgdoc_") && keepDocs.Contains(IdAt(name, 1)))
                || ((name.StartsWith("msgphoto_") || name.StartsWith("photo_")) && keepPhotos.Contains(IdAt(name, 1)))
                || ((name.StartsWith("ava_") || name.StartsWith("avabig_")) && keepPeers.Contains(IdAt(name, 1)));
            if (!keep) freed += TryDelete(path);
        }
        foreach (var path in mediaFiles)
        {
            Step();
            var name = Path.GetFileName(path);
            // Полные фото лежат как p{id}.jpg, файлы — как {id}.ext.
            var keep = name.StartsWith('p') ? keepPhotos.Contains(IdAt(name[1..], 0)) : keepDocs.Contains(IdAt(name, 0));
            if (!keep) freed += TryDelete(path);
        }
        foreach (var (id, part) in _partPaths)
            if (!File.Exists(part)) _partPaths.TryRemove(id, out _);
        progress?.Report(1);
        return freed;
    }

    private static long TryDelete(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            File.Delete(path);
            return size;
        }
        catch { return 0; } // файл сейчас открыт (играет видео, качается) — останется
    }

    /// <summary>Очищает кэш картинок.</summary>
    public void ClearCache()
    {
        _inflight.Clear();
        foreach (var f in Directory.EnumerateFiles(_cacheDir))
            try { File.Delete(f); } catch { /* файл может быть занят */ }
    }
}
