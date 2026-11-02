using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TgCheck.Core.Services;

namespace TgCheck.Core.Music;

/// <summary>
/// Плейлисты с других площадок: по ссылке, которой там делятся (без входа в аккаунт), или списком песен текстом.
/// От площадки нужны только названия, исполнители и обложки: сами файлы, как и у песен каталога, ищутся в Telegram при запуске песни.
/// </summary>
public static class PlaylistImport
{
    private const int MaxTracks = 500;

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        // Страницы площадок отдают данные обычному браузеру.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        return http;
    }

    private static readonly Regex Spotify = new(@"open\.spotify\.com/(?:[a-z-]+/)?(?:embed/)?(playlist|album|track)/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
    private static readonly Regex YandexPlaylist = new(@"music\.yandex\.[a-z]+/users/([^/?#]+)/playlists/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex YandexUuid = new(@"music\.yandex\.[a-z]+/playlists?/([0-9a-z.-]{8,})", RegexOptions.IgnoreCase);
    private static readonly Regex YandexAlbum = new(@"music\.yandex\.[a-z]+/album/(\d+)(?:/track/(\d+))?", RegexOptions.IgnoreCase);
    private static readonly Regex Apple = new(@"music\.apple\.com/[^\s]+", RegexOptions.IgnoreCase);
    private static readonly Regex Deezer = new(@"deezer\.com/(?:[a-z]{2}/)?(playlist|album|track)/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex DeezerShort = new(@"(?:deezer\.page\.link|link\.deezer\.com)/\S+", RegexOptions.IgnoreCase);
    private static readonly Regex YouTube = new(@"(?:youtube\.com|youtu\.be)/\S*[?&]list=([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
    private static readonly Regex Closed = new(@"(?:vk\.com|vk\.ru|soundcloud\.com|boom\.ru|tidal\.com|music\.amazon)\S*", RegexOptions.IgnoreCase);
    private static readonly Regex AnyUrl = new(@"https?://\S+", RegexOptions.IgnoreCase);

    /// <summary>Похоже на ссылку на музыку (поиск раздела и буфер обмена предлагают добавить её как плейлист).</summary>
    public static bool IsMusicLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return !t.Contains('\n') && (Spotify.IsMatch(t) || YandexPlaylist.IsMatch(t) || YandexUuid.IsMatch(t) || YandexAlbum.IsMatch(t) || Apple.IsMatch(t)
            || Deezer.IsMatch(t) || DeezerShort.IsMatch(t) || YouTube.IsMatch(t) || Closed.IsMatch(t));
    }

    /// <summary>Плейлист по ссылке или из списка песен. Ошибка с понятным текстом — <see cref="InvalidOperationException"/>.</summary>
    public static async Task<MusicPlaylist> ImportAsync(string input, CancellationToken ct = default)
    {
        var text = input.Trim();
        if (text.Length == 0) throw new InvalidOperationException("Вставьте ссылку или список песен.");
        if (text.Contains('\n') || !AnyUrl.IsMatch(text) && !IsMusicLink(text)) return FromText(text);

        if (DeezerShort.IsMatch(text)) text = await FollowAsync(AnyUrl.Match(text).Value, ct).ConfigureAwait(false);
        Parsed? parsed;
        try
        {
            parsed = text switch
            {
                _ when Spotify.Match(text) is { Success: true } m => await SpotifyAsync(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value, ct).ConfigureAwait(false),
                _ when YandexPlaylist.Match(text) is { Success: true } m => await YandexPlaylistAsync($"users/{m.Groups[1].Value}/playlists/{m.Groups[2].Value}", ct).ConfigureAwait(false),
                _ when YandexAlbum.Match(text) is { Success: true } m => await YandexAlbumAsync(m.Groups[1].Value, m.Groups[2].Value, ct).ConfigureAwait(false),
                _ when YandexUuid.Match(text) is { Success: true } m => await YandexPlaylistAsync($"playlist/{m.Groups[1].Value}", ct).ConfigureAwait(false),
                _ when Deezer.Match(text) is { Success: true } m => await DeezerAsync(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value, ct).ConfigureAwait(false),
                _ when Apple.Match(text) is { Success: true } m => await AppleAsync("https://" + m.Value, ct).ConfigureAwait(false),
                _ when YouTube.Match(text) is { Success: true } m => await YouTubeAsync(m.Groups[1].Value, ct).ConfigureAwait(false),
                _ when Closed.IsMatch(text) => throw new InvalidOperationException("Эта площадка не показывает плейлисты без входа. Скопируйте песни списком: «Исполнитель — Название», по одной в строке."),
                _ => throw new InvalidOperationException("Такие ссылки пока не поддерживаются: Spotify, Яндекс Музыка, Apple Music, Deezer и YouTube."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            Logging.Write($"music import: {ex.Message}");
            throw new InvalidOperationException("Площадка не ответила. Проверьте ссылку: плейлист должен быть открытым.");
        }
        if (parsed is null || parsed.Tracks.Count == 0)
            throw new InvalidOperationException("В плейлисте не нашлось песен. Он должен быть открытым.");
        return Build(parsed.Title, parsed.Source, parsed.Cover, parsed.Tracks, text);
    }

    private sealed record Parsed(string Title, string Source, string? Cover, List<(string Artist, string Title, double Duration, string? Cover)> Tracks);

    private static MusicPlaylist Build(string title, string source, string? cover, List<(string Artist, string Title, double Duration, string? Cover)> tracks, string? link)
    {
        var list = MusicTrack.Distinct(tracks.Where(t => t.Title.Length > 0).Take(MaxTracks).Select(t => new MusicTrack
        {
            Source = MusicSource.Imported,
            Id = Hash(t.Artist + "|" + t.Title),
            Title = t.Title,
            Artist = t.Artist,
            Duration = t.Duration,
            CoverUrl = t.Cover,
            CoverBigUrl = t.Cover,
        }));
        return new MusicPlaylist(-Math.Abs(long.Parse(Hash(link ?? title + list.Count)[..15], System.Globalization.NumberStyles.HexNumber)) - 1, title, list.Count, cover, cover)
        {
            Kind = MusicPlaylist.KindImported,
            Caption = source,
            Link = link,
            Tracks = list,
        };
    }

    private static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(MusicTrack.Normalize(s)))).ToLowerInvariant()[..24];

    // ---- список песен текстом

    private static readonly Regex Numbering = new(@"^\s*(?:\d+[.)]\s*|[-•*]\s+)");
    private static readonly Regex Separator = new(@"\s+[-–—]\s+");

    private static MusicPlaylist FromText(string text)
    {
        var tracks = new List<(string, string, double, string?)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Numbering.Replace(raw.Trim(), "").Trim();
            if (line.Length == 0 || AnyUrl.IsMatch(line)) continue;
            var parts = Separator.Split(line, 2);
            tracks.Add(parts.Length == 2 ? (parts[0].Trim(), parts[1].Trim(), 0, null) : ("", line, 0, null));
        }
        if (tracks.Count == 0) throw new InvalidOperationException("Не нашлось ни одной песни: нужна ссылка или строки «Исполнитель — Название».");
        return Build($"Мой плейлист · {DateTime.Now:d MMMM}", "Из списка", null, tracks, null);
    }

    // ---- площадки

    private static async Task<string> FollowAsync(string url, CancellationToken ct)
    {
        using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return res.RequestMessage?.RequestUri?.ToString() ?? url;
    }

    private static readonly Regex NextData = new("""<script id="__NEXT_DATA__"[^>]*>(.+?)</script>""", RegexOptions.Singleline);

    private static async Task<Parsed?> SpotifyAsync(string kind, string id, CancellationToken ct)
    {
        var html = await Http.GetStringAsync($"https://open.spotify.com/embed/{kind}/{id}", ct).ConfigureAwait(false);
        if (NextData.Match(html) is not { Success: true } m) return null;
        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        if (!doc.RootElement.TryGetProperty("props", out var props) || !props.TryGetProperty("pageProps", out var page)
            || !page.TryGetProperty("state", out var state) || !state.TryGetProperty("data", out var data) || !data.TryGetProperty("entity", out var entity))
            return null;
        var title = Str(entity, "title") ?? Str(entity, "name") ?? "Плейлист";
        string? cover = null;
        if (entity.TryGetProperty("coverArt", out var art) && art.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            cover = sources.EnumerateArray().Select(s => Str(s, "url")).FirstOrDefault(u => u is not null);
        var tracks = new List<(string, string, double, string?)>();
        if (entity.TryGetProperty("trackList", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var t in list.EnumerateArray())
                tracks.Add((Str(t, "subtitle")?.Replace(" ", " ") ?? "", Str(t, "title") ?? "", Num(t, "duration") / 1000, kind == "playlist" ? null : cover));
        else if (kind == "track")
            tracks.Add((Str(entity, "subtitle") ?? "", title, Num(entity, "duration") / 1000, cover));
        return new Parsed(title, "Spotify", cover, tracks);
    }

    private static string? YandexCover(JsonElement e, string name)
    {
        var uri = Str(e, name);
        return string.IsNullOrEmpty(uri) ? null : "https://" + uri.Replace("%%", "400x400");
    }

    private static (string, string, double, string?) YandexTrack(JsonElement t)
    {
        var artists = t.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
            ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name")).Where(n => !string.IsNullOrEmpty(n)))
            : "";
        var title = Str(t, "title") ?? "";
        if (Str(t, "version") is { Length: > 0 } version) title += $" ({version})";
        return (artists, title, Num(t, "durationMs") / 1000, YandexCover(t, "coverUri"));
    }

    private static async Task<JsonElement?> YandexAsync(string path, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.music.yandex.net/" + path, ct).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("result", out var r) ? r.Clone() : null;
    }

    private static async Task<Parsed?> YandexPlaylistAsync(string path, CancellationToken ct)
    {
        if (await YandexAsync(path, ct).ConfigureAwait(false) is not { } r) return null;
        var tracks = new List<(string, string, double, string?)>();
        if (r.TryGetProperty("tracks", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (item.TryGetProperty("track", out var t)) tracks.Add(YandexTrack(t));
        return new Parsed(Str(r, "title") ?? "Плейлист", "Яндекс Музыка", YandexCover(r, "ogImage"), tracks);
    }

    private static async Task<Parsed?> YandexAlbumAsync(string album, string track, CancellationToken ct)
    {
        if (track.Length > 0)
        {
            if (await YandexAsync($"tracks/{track}", ct).ConfigureAwait(false) is not { ValueKind: JsonValueKind.Array } one) return null;
            var songs = one.EnumerateArray().Select(YandexTrack).ToList();
            return songs.Count == 0 ? null : new Parsed(songs[0].Item2, "Яндекс Музыка", songs[0].Item4, songs);
        }
        if (await YandexAsync($"albums/{album}/with-tracks", ct).ConfigureAwait(false) is not { } r) return null;
        var tracks = new List<(string, string, double, string?)>();
        if (r.TryGetProperty("volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Array)
            foreach (var volume in volumes.EnumerateArray())
                tracks.AddRange(volume.EnumerateArray().Select(YandexTrack));
        return new Parsed(Str(r, "title") ?? "Альбом", "Яндекс Музыка", YandexCover(r, "coverUri"), tracks);
    }

    private static async Task<Parsed?> DeezerAsync(string kind, string id, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.deezer.com/{kind}/{id}" + (kind == "track" ? "" : "?limit=" + MaxTracks), ct).ConfigureAwait(false));
        var r = doc.RootElement;
        if (r.TryGetProperty("error", out _)) return null;
        static (string, string, double, string?) Song(JsonElement t, string? fallback)
        {
            var artist = t.TryGetProperty("artist", out var a) ? Str(a, "name") ?? "" : "";
            var cover = t.TryGetProperty("album", out var al) ? Str(al, "cover_medium") ?? fallback : fallback;
            return (artist, Str(t, "title_short") ?? Str(t, "title") ?? "", Num(t, "duration"), cover);
        }
        if (kind == "track")
        {
            var one = Song(r, null);
            return new Parsed(one.Item2, "Deezer", one.Item4, [one]);
        }
        var big = Str(r, "picture_big") ?? Str(r, "cover_big");
        var tracks = new List<(string, string, double, string?)>();
        if (r.TryGetProperty("tracks", out var list) && list.TryGetProperty("data", out var data))
            tracks.AddRange(data.EnumerateArray().Select(t => Song(t, big)));
        return new Parsed(Str(r, "title") ?? "Плейлист", "Deezer", big, tracks);
    }

    private static readonly Regex JsonLd = new("""<script[^>]*application/ld\+json[^>]*>(.+?)</script>""", RegexOptions.Singleline);
    private static readonly Regex OgImage = new("""<meta[^>]+property="?og:image"?[^>]+content="([^"]+)""");
    // В данных страницы у каждой песни рядом исполнитель и название: в JSON-LD плейлиста исполнителей нет.
    private static readonly Regex AppleSong = new("""
        "artistName":"((?:[^"\\]|\\.)*)".{0,400}?"name":"((?:[^"\\]|\\.)*)","impressionType"
        """.Trim(), RegexOptions.Singleline);

    private static async Task<Parsed?> AppleAsync(string url, CancellationToken ct)
    {
        var html = await Http.GetStringAsync(url.TrimEnd('.', ',', ')'), ct).ConfigureAwait(false);
        var title = "Apple Music";
        foreach (Match m in JsonLd.Matches(html))
        {
            try
            {
                using var ld = JsonDocument.Parse(m.Groups[1].Value);
                if (Str(ld.RootElement, "name") is { Length: > 0 } name) { title = name; break; }
            }
            catch (JsonException) { /* другой блок */ }
        }
        var cover = OgImage.Match(html) is { Success: true } og ? WebUtility.HtmlDecode(og.Groups[1].Value) : null;
        var tracks = new List<(string, string, double, string?)>();
        foreach (Match m in AppleSong.Matches(html))
            tracks.Add((Unescape(m.Groups[1].Value), Unescape(m.Groups[2].Value), 0, null));
        return new Parsed(title, "Apple Music", cover, tracks);
    }

    private static readonly Regex YouTubeTitle = new("""
        "lockupMetadataViewModel":\{"title":\{"content":"((?:[^"\\]|\\.)*)"
        """.Trim());
    private static readonly Regex YouTubeName = new("""<meta property="og:title" content="([^"]*)""");
    private static readonly Regex YouTubeNoise = new(@"\s*[\(\[][^\)\]]*(?:official|video|audio|lyric|клип|hd|4k)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase);

    private static async Task<Parsed?> YouTubeAsync(string list, CancellationToken ct)
    {
        var html = await Http.GetStringAsync($"https://www.youtube.com/playlist?list={list}&hl=ru", ct).ConfigureAwait(false);
        var tracks = new List<(string, string, double, string?)>();
        foreach (Match m in YouTubeTitle.Matches(html))
        {
            var name = YouTubeNoise.Replace(Unescape(m.Groups[1].Value), "").Trim();
            var parts = Separator.Split(name, 2);
            tracks.Add(parts.Length == 2 ? (parts[0].Trim(), parts[1].Trim(), 0, null) : ("", name, 0, null));
        }
        var title = YouTubeName.Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value) : "YouTube";
        return new Parsed(title, "YouTube", null, tracks);
    }

    // ---- JSON

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Unescape(string s)
    {
        try { return JsonSerializer.Deserialize<string>($"\"{s}\"") ?? s; }
        catch (JsonException) { return s; }
    }
}
