using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TgCheck.Core.Music;

namespace TgCheck.App.Helpers;

/// <summary>
/// Обложки раздела «Музыка»: из памяти (пока картинка где-то на экране), с диска (music\covers) или из сети —
/// не больше 6 загрузок разом; декодируются в фоне сразу под нужный размер, так что списки не тормозят.
/// </summary>
public static class MusicCovers
{
    private static readonly SemaphoreSlim Gate = new(6);
    private static string? _dir;

    public static void Init(MusicLibrary library) => _dir = library.CoversDirectory;

    private static string FileFor(string url) =>
        Path.Combine(_dir!, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20] + ".jpg");

    // Адреса, которые не ответили, — не просим снова 10 минут (иначе каждая прокрутка полки ждала их заново).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Failed = new();

    /// <param name="mirrors">Запасные серверы (Audius): основной не ответил — тот же путь на другом сервере.</param>
    public static async Task<ImageSource?> LoadAsync(string? url, int width, string[]? mirrors = null)
    {
        if (string.IsNullOrEmpty(url) || _dir is null) return null;
        var key = $"music:{width}:{url}";
        if (ImageCache.Get(key) is { } ready) return ready;
        return await Task.Run(async () =>
        {
            var file = url.StartsWith("file:") ? new Uri(url).LocalPath : FileFor(url);
            byte[]? bytes = null;
            if (File.Exists(file))
                try { bytes = await File.ReadAllBytesAsync(file); } catch { }
            if (bytes is null && url.StartsWith("file:")) return null;
            if (bytes is null)
            {
                foreach (var candidate in Candidates(url, mirrors))
                {
                    if (Failed.TryGetValue(candidate, out var at) && Environment.TickCount64 - at < 600_000) continue;
                    await Gate.WaitAsync();
                    try { bytes = await MusicCatalog.GetBytesAsync(candidate); }
                    finally { Gate.Release(); }
                    if (bytes is not null) break;
                    Failed[candidate] = Environment.TickCount64;
                }
                if (bytes is null) return null;
                try { await File.WriteAllBytesAsync(file, bytes); } catch { }
            }
            var image = ImageCache.Put(key, Images.FromBytes(bytes, width));
            if (image is not null && width >= 400) KeepBig(image);
            return image;
        });
    }

    /// <summary>Обложка уже в памяти (без загрузки) — плеер при переключении показывает её сразу.</summary>
    public static ImageSource? Cached(string? url, int width) => string.IsNullOrEmpty(url) ? null : ImageCache.Get($"music:{width}:{url}");

    // Крупные обложки последних треков держим сами (кэш картинок слабый — их собирал сборщик мусора, и плеер
    // при переключении грузил обложку заново).
    private static readonly LinkedList<ImageSource> Big = new();

    private static void KeepBig(ImageSource image)
    {
        lock (Big)
        {
            Big.Remove(image);
            Big.AddFirst(image);
            while (Big.Count > 12) Big.RemoveLast();
        }
    }

    private static IEnumerable<string> Candidates(string url, string[]? mirrors)
    {
        yield return url;
        if (mirrors is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) yield break;
        foreach (var m in mirrors.Take(3))
            if (Uri.TryCreate(m, UriKind.Absolute, out var host) && host.Host != uri.Host)
                yield return new UriBuilder(uri) { Scheme = host.Scheme, Host = host.Host, Port = host.Port }.Uri.AbsoluteUri;
    }

    /// <summary>Обложка, вшитая в песню Telegram (байты jpeg), — на диск; null — обложки нет.</summary>
    public static string? SaveTelegramCoverBytes(string id, byte[]? bytes)
    {
        if (_dir is null || bytes is not { Length: > 0 }) return null;
        var path = Path.Combine(_dir, "tg_" + string.Concat(id.Where(char.IsLetterOrDigit)) + ".jpg");
        try
        {
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return new Uri(path).AbsoluteUri;
        }
        catch { return null; }
    }

    /// <summary>Обложка песни Telegram (превью из сообщения) — на диск, чтобы история показывала её и после перезапуска.</summary>
    public static string? SaveTelegramCover(string id, ImageSource? cover)
    {
        if (_dir is null || cover is not BitmapSource bitmap) return null;
        var path = Path.Combine(_dir, "tg_" + string.Concat(id.Where(char.IsLetterOrDigit)) + ".jpg");
        if (File.Exists(path)) return new Uri(path).AbsoluteUri;
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var fs = File.Create(path);
            encoder.Save(fs);
            return new Uri(path).AbsoluteUri;
        }
        catch { return null; }
    }
}
