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
    private readonly SemaphoreSlim _downloads = new(4, 4);
    // Аватарки маленькие и нужны сразу (первый вход — десятки строк) — своя, более широкая очередь, не за картинками сообщений.
    private readonly SemaphoreSlim _avatarDownloads = new(8, 8);
    // Загрузчики запускаются в пуле потоков: даже чтение с диска не должно идти в UI-потоке (в группах это десятки файлов разом).
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();

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
        return _inflight.GetOrAdd(key, _ => Task.Run(() => RenderCachedAsync(gift, size, key)));
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
        var (bytes, mime) = await DownloadDocumentAsync(doc);
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
            await _client().DownloadFileAsync(doc, ms, thumb);
            await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
            return SKImage.FromEncodedData(ms.ToArray());
        }
        finally { _downloads.Release(); }
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

    // Исходные файлы документов тоже кэшируются: узоры и модели повторяются у многих подарков.
    private async Task<(byte[]? Bytes, string? Mime)> DownloadDocumentAsync(Document doc)
    {
        var path = Path.Combine(_cacheDir, $"doc_{doc.id}.bin");
        if (File.Exists(path)) return (await File.ReadAllBytesAsync(path), doc.mime_type);

        await _downloads.WaitAsync();
        try
        {
            if (File.Exists(path)) return (await File.ReadAllBytesAsync(path), doc.mime_type);
            using var ms = new MemoryStream();
            var mime = await _client().DownloadFileAsync(doc, ms);
            await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
            var bytes = ms.ToArray();
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return (bytes, mime ?? doc.mime_type);
        }
        finally { _downloads.Release(); }
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
        return _inflight.GetOrAdd(key, _ => Task.Run(() => LoadAvatarAsync(peer, key)));
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
                await _client().DownloadProfilePhotoAsync(peer, ms, big: big);
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
        return _inflight.GetOrAdd(key, _ => Task.Run(() => LoadAvatarAsync(peer.Info, key, big: true)));
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
        return _inflight.GetOrAdd(key, _ => Task.Run(() => LoadAvatarAsync(peer.Info, key)));
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
        return _inflight.GetOrAdd(key, _ => Task.Run(() => LoadMessagePreviewAsync(message, key)));
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
                var size = photo.sizes.OfType<PhotoSize>().Where(s => s.w <= 1280).MaxBy(s => s.w)
                           ?? photo.LargestPhotoSize;
                await _downloads.WaitAsync();
                try
                {
                    using var ms = new MemoryStream();
                    await _client().DownloadFileAsync(photo, ms, size);
                    await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
                    bytes = ms.ToArray();
                }
                finally { _downloads.Release(); }
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
                else if (doc.thumbs?.OfType<PhotoSize>().MaxBy(t => t.w) is { } thumb)
                {
                    await _downloads.WaitAsync();
                    try
                    {
                        using var ms = new MemoryStream();
                        await _client().DownloadFileAsync(doc, ms, thumb);
                        await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
                        bytes = ms.ToArray();
                    }
                    finally { _downloads.Release(); }
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
        return _inflight.GetOrAdd(key, _ => Task.Run(() => LoadMapAsync(item, lat, lon, w, h, zoom, key)));
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
    public Task<string?> GetMediaFileAsync(MediaHolder media, IProgress<double>? progress = null, bool priority = true)
    {
        if (media.Document is not { } doc) return Task.FromResult<string?>(null);
        var task = _files.GetOrAdd(doc.id, _ => Task.Run(() => DownloadMediaFileAsync(doc, progress, priority)));
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
        var task = _fullPhotos.GetOrAdd(photo.id, _ => Task.Run(() => DownloadFullPhotoAsync(photo, priority)));
        if (task.IsFaulted || (task.IsCompleted && task.Result is null)) _fullPhotos.TryRemove(photo.id, out _);
        // В памяти держим немного — остальное читается с диска.
        if (_fullPhotos.Count > 12)
            foreach (var key in _fullPhotos.Where(p => p.Value.IsCompleted && p.Key != photo.id).Select(p => p.Key).Take(_fullPhotos.Count - 12).ToList())
                _fullPhotos.TryRemove(key, out _);
        return task;
    }

    private async Task<byte[]?> DownloadFullPhotoAsync(Photo photo, bool priority)
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
                using var ms = new MemoryStream();
                await _client().DownloadFileAsync(photo, ms, photo.LargestPhotoSize);
                await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
                var bytes = ms.ToArray();
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

    private async Task<string?> DownloadMediaFileAsync(Document doc, IProgress<double>? progress, bool priority)
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
        var path = Path.Combine(dir, $"{doc.id}{ext}");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == doc.size)
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return path;
            }
            var part = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            if (!priority) await _backgroundFiles.WaitAsync();
            try
            {
                await using (var fs = File.Create(part))
                    await _client().DownloadFileAsync(doc, fs, (PhotoSizeBase?)null,
                        (done, total) => progress?.Report(total > 0 ? (double)done / total : 0));
                await Task.Yield(); // дальше — в пуле потоков, не в сетевом потоке (иначе ждут обновления)
            }
            finally { if (!priority) _backgroundFiles.Release(); }
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            Logging.Write($"media file {doc.id}: {ex.Message}");
            _files.TryRemove(doc.id, out _);
            return null;
        }
    }

    /// <summary>Исходные байты документа (например, .tgs для анимации стикера).</summary>
    public async Task<byte[]?> GetDocumentBytesAsync(MediaHolder media)
    {
        if (media.Document is not { } doc) return null;
        var (bytes, _) = await Task.Run(() => DownloadDocumentAsync(doc));
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
                await _client().DownloadFileAsync(photo.Photo, ms, photo.Photo.LargestPhotoSize);
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

    /// <summary>Очищает кэш картинок.</summary>
    public void ClearCache()
    {
        _inflight.Clear();
        foreach (var f in Directory.EnumerateFiles(_cacheDir))
            try { File.Delete(f); } catch { /* файл может быть занят */ }
    }
}
