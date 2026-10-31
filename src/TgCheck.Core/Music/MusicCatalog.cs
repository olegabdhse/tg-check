using System.Net;
using System.Net.Http;
using System.Text.Json;
using TgCheck.Core.Services;

namespace TgCheck.Core.Music;

/// <summary>
/// Deezer без ключей: огромный каталог — обложки и «похожие исполнители» (без входа даёт только 30 с превью).
/// Все запросы — в фоне, с ограничением по времени; ответ разбирается вне потока интерфейса.
/// </summary>
public static class MusicCatalog
{
    private const string Deezer = "https://api.deezer.com";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 8,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    static MusicCatalog() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("TgCheck/1.0");

    /// <summary>Скачать байты (обложки).</summary>
    public static async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct = default)
    {
        // Обложка — не дольше 8 с: не ответил сервер — пробуем запасной, а не ждём 15 с.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { return await Http.GetByteArrayAsync(url, timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logging.Write($"music cover: {ex.Message}");
            return null;
        }
    }

    private static async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logging.Write($"music api: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<JsonElement> Data(JsonElement? root) =>
        root is { } r && r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    // ===================================================================== Deezer

    public static async Task<IReadOnlyList<MusicTrack>> DeezerSearchAsync(string query, int limit = 20, CancellationToken ct = default) =>
        DeezerTracks(await GetJsonAsync($"{Deezer}/search?q={Uri.EscapeDataString(query)}&limit={limit}", ct));

    /// <summary>Мировой чарт Deezer.</summary>
    public static async Task<IReadOnlyList<MusicTrack>> DeezerChartAsync(int limit = 25, CancellationToken ct = default) =>
        DeezerTracks(await GetJsonAsync($"{Deezer}/chart/0/tracks?limit={limit}", ct));

    public static async Task<IReadOnlyList<MusicTrack>> DeezerArtistTopAsync(long artistId, int limit = 5, CancellationToken ct = default) =>
        DeezerTracks(await GetJsonAsync($"{Deezer}/artist/{artistId}/top?limit={limit}", ct));

    /// <summary>Исполнитель по имени (лучшее совпадение).</summary>
    public static async Task<(long Id, string Name)?> DeezerFindArtistAsync(string name, CancellationToken ct = default)
    {
        var a = Data(await GetJsonAsync($"{Deezer}/search/artist?q={Uri.EscapeDataString(name)}&limit=1", ct)).FirstOrDefault();
        return a.ValueKind == JsonValueKind.Object && Num(a, "id") is > 0 and var id ? ((long)id, Str(a, "name") ?? name) : null;
    }

    /// <summary>«Радио» исполнителя: треки в похожем звучании (жанр, настроение) — от него и похожих на него.</summary>
    public static async Task<IReadOnlyList<MusicTrack>> DeezerArtistRadioAsync(long artistId, int limit = 25, CancellationToken ct = default) =>
        DeezerTracks(await GetJsonAsync($"{Deezer}/artist/{artistId}/radio?limit={limit}", ct));

    /// <summary>Похожие исполнители.</summary>
    public static async Task<IReadOnlyList<(long Id, string Name)>> DeezerRelatedAsync(long artistId, int limit = 6, CancellationToken ct = default) =>
        Data(await GetJsonAsync($"{Deezer}/artist/{artistId}/related?limit={limit}", ct))
            .Select(a => ((long)Num(a, "id"), Str(a, "name") ?? "")).Where(a => a.Item1 > 0).ToList();

    /// <summary>Трек заново — ссылка на превью у Deezer живёт ограниченное время.</summary>
    public static async Task<string?> DeezerPreviewAsync(string id, CancellationToken ct = default) =>
        await GetJsonAsync($"{Deezer}/track/{id}", ct) is { } t ? Str(t, "preview") : null;

    /// <summary>Обложка альбома для песни без своей обложки (песни Telegram): (маленькая, крупная).</summary>
    public static async Task<(string Small, string Big)?> FindCoverAsync(string artist, string title, CancellationToken ct = default)
    {
        var query = string.IsNullOrWhiteSpace(artist) ? title : $"artist:\"{artist}\" track:\"{title}\"";
        var found = DeezerTracks(await GetJsonAsync($"{Deezer}/search?q={Uri.EscapeDataString(query)}&limit=5", ct));
        if (found.Count == 0 && !string.IsNullOrWhiteSpace(artist)) // точный поиск не нашёл (теги записаны иначе) — свободный
            found = DeezerTracks(await GetJsonAsync($"{Deezer}/search?q={Uri.EscapeDataString(artist + " " + title)}&limit=5", ct));
        var want = MusicTrack.Normalize(title);
        // Та же песня; нет — хотя бы тот же исполнитель (чужая обложка хуже, чем никакой).
        var artistKey = MusicTrack.Normalize(artist);
        var best = found.FirstOrDefault(t => MusicTrack.Normalize(t.Title) == want)
            ?? found.FirstOrDefault(t => artistKey.Length > 0 && MusicTrack.Normalize(t.Artist) == artistKey);
        return best is { CoverUrl: { } s, CoverBigUrl: { } b } ? (s, b) : null;
    }

    private static List<MusicTrack> DeezerTracks(JsonElement? root)
    {
        var list = new List<MusicTrack>();
        foreach (var t in Data(root))
        {
            if (t.TryGetProperty("readable", out var r) && r.ValueKind == JsonValueKind.False) continue;
            var id = Num(t, "id");
            if (id <= 0 || Str(t, "preview") is not { Length: > 0 }) continue;
            string? artist = null, small = null, big = null, album = null;
            long artistId = 0;
            if (t.TryGetProperty("artist", out var a)) { artist = Str(a, "name"); artistId = (long)Num(a, "id"); }
            if (t.TryGetProperty("album", out var al))
            {
                album = Str(al, "title");
                small = Str(al, "cover_medium");
                big = Str(al, "cover_xl") ?? Str(al, "cover_big") ?? small;
            }
            list.Add(new MusicTrack
            {
                Source = MusicSource.Deezer, Id = ((long)id).ToString(), Title = Str(t, "title_short") ?? Str(t, "title") ?? "",
                Artist = artist ?? "", ArtistId = artistId, Album = album, CoverUrl = small, CoverBigUrl = big, Duration = Num(t, "duration"),
            });
        }
        return list;
    }
}
