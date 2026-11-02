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

    private static readonly (Color Top, Color Bottom)[] MixColors =
    [
        (Color.FromRgb(0x5B, 0x2E, 0xE0), Color.FromRgb(0x10, 0x8D, 0xD6)),
        (Color.FromRgb(0xE0, 0x3E, 0x7A), Color.FromRgb(0xF0, 0x8A, 0x3C)),
        (Color.FromRgb(0x0F, 0x9D, 0x7A), Color.FromRgb(0x2E, 0x5F, 0xE0)),
    ];

    private static (Color Top, Color Bottom) MoodColors(string? caption) => caption?.Split(' ')[0] switch
    {
        "Утро" => (Color.FromRgb(0xF5, 0xB2, 0x3C), Color.FromRgb(0xEF, 0x5D, 0x8A)),
        "День" => (Color.FromRgb(0x25, 0xB5, 0xE8), Color.FromRgb(0x3C, 0xD0, 0x9A)),
        "Вечер" => (Color.FromRgb(0xE8, 0x4A, 0x5F), Color.FromRgb(0x5B, 0x2E, 0xA0)),
        _ => (Color.FromRgb(0x1B, 0x2A, 0x6B), Color.FromRgb(0x6A, 0x2E, 0xB5)),
    };

    /// <summary>Обложка своего плейлиста: четыре обложки песен под цветным слоем и название поверх.</summary>
    public static async Task<ImageSource?> ComposeAsync(MusicPlaylist playlist)
    {
        var urls = playlist.Tracks?.Select(t => t.CoverUrl ?? t.CoverBigUrl).Where(u => !string.IsNullOrEmpty(u)).Distinct().Take(4).ToList() ?? [];
        var covers = (await Task.WhenAll(urls.Select(u => LoadAsync(u, 220)))).OfType<ImageSource>().ToList();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null ? null : await dispatcher.InvokeAsync(() => Render(playlist, covers));
    }

    private static ImageSource Render(MusicPlaylist playlist, List<ImageSource> covers)
    {
        const int size = 320;
        var (top, bottom) = playlist.Kind == MusicPlaylist.KindMood ? MoodColors(playlist.Caption)
            : MixColors[(int)(Math.Abs(playlist.Id) % MixColors.Length)];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var all = new System.Windows.Rect(0, 0, size, size);
            dc.DrawRectangle(new LinearGradientBrush(top, bottom, 60), null, all);
            for (var i = 0; covers.Count == 4 && i < 4; i++)
                dc.DrawImage(covers[i], new System.Windows.Rect(i % 2 * size / 2, i / 2 * size / 2, size / 2, size / 2));
            if (covers.Count is > 0 and < 4) dc.DrawImage(covers[0], all);
            var shade = new LinearGradientBrush(Color.FromArgb(150, top.R, top.G, top.B), Color.FromArgb(235, bottom.R, bottom.G, bottom.B), 90);
            if (covers.Count > 0) dc.DrawRectangle(shade, null, all);
            var title = playlist.Kind == MusicPlaylist.KindMood ? "Настроение\nдня" : playlist.Title.Replace("Микс ", "Микс\n");
            var text = new FormattedText(title, System.Globalization.CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal),
                playlist.Kind switch { MusicPlaylist.KindMood => 46, MusicPlaylist.KindMix => 58, _ => 38 }, Brushes.White, 1.0)
            { MaxTextWidth = size - 40, MaxTextHeight = size - 40, Trimming = System.Windows.TextTrimming.CharacterEllipsis };
            dc.DrawText(text, new System.Windows.Point(20, size - text.Height - 18));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
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
