using TgCheck.Core.Services;

namespace TgCheck.Core.Music;

/// <summary>
/// Свои подборки из того, что вы слушаете. Миксы 1, 2, 3 — по одному на ваш жанр (раз в день): знакомые песни этого жанра вперемешку
/// с «радио» его исполнителей. «Настроение дня» — по тому, что звучало за последние сутки, и меняется с временем суток.
/// Нужны только названия: файлы ищутся в Telegram при запуске песни.
/// </summary>
public static class MusicMixes
{
    public const int MixSize = 30;
    /// <summary>Меньше — не подборка.</summary>
    public const int MinSize = 10;
    private const int MinHistory = 3;
    // Правила подбора поменялись — сохранённые подборки собираются заново.
    private const int CurrentVersion = 4;

    public sealed record Mixes(string Date, string MoodKey, List<MusicPlaylist> Playlists, int Version = 0);

    /// <summary>Сутки делятся на четыре части: подборка настроения — на каждую свою.</summary>
    public static string DayPart(DateTime now) => now.Hour switch
    {
        >= 5 and < 12 => "Утро",
        >= 12 and < 18 => "День",
        >= 18 and < 23 => "Вечер",
        _ => "Ночь",
    };

    private static string Today(DateTime now) => now.ToString("yyyy-MM-dd");
    private static string MoodKey(DateTime now) => $"{Today(now)}|{DayPart(now)}";

    /// <summary>Сохранённые подборки ещё годятся (те же сутки и та же часть суток).</summary>
    public static bool IsFresh(Mixes? saved, DateTime now) =>
        saved is { Playlists.Count: > 0, Version: CurrentVersion } && saved.Date == Today(now) && saved.MoodKey == MoodKey(now);

    public static async Task<Mixes?> BuildAsync(MusicLibrary library, Mixes? saved, DateTime now, CancellationToken ct = default)
    {
        var history = library.History;
        if (history.Count < MinHistory) return null;
        var used = new List<MusicTrack>();
        var result = new List<MusicPlaylist>();

        // Настроение — первым: при нехватке песен миксы уступают ему.
        if (await BuildMoodAsync(library, history, used, now, ct).ConfigureAwait(false) is { } mood) result.Add(mood);

        if (saved is { Version: CurrentVersion } && saved.Date == Today(now))
            result.AddRange(saved.Playlists.Where(p => p.Kind is MusicPlaylist.KindMix or MusicPlaylist.KindChat)); // эти меняются раз в день
        else
            result.AddRange(await BuildMixesAsync(library, history, used, now, ct).ConfigureAwait(false));
        return result.Count == 0 ? null : new Mixes(Today(now), MoodKey(now), result, CurrentVersion);
    }

    private static async Task<List<MusicPlaylist>> BuildMixesAsync(MusicLibrary library, IReadOnlyList<MusicHistoryEntry> history, List<MusicTrack> used, DateTime now, CancellationToken ct)
    {
        var artists = library.TopArtists(18);
        var genres = await Task.WhenAll(artists.Select(a => library.ArtistGenreAsync(a, ct))).ConfigureAwait(false);
        var groups = artists.Select((a, i) => (Artist: a, Genre: genres[i])).Where(x => x.Genre.Length > 0)
            .GroupBy(x => x.Genre).Select(g => (Genre: g.Key, Artists: g.Select(x => x.Artist).ToList())).Take(3).ToList();
        // Жанров меньше трёх — остальные миксы из исполнителей, которым жанровых не досталось.
        var taken = groups.SelectMany(g => g.Artists).ToHashSet();
        var rest = artists.Where(a => !taken.Contains(a)).ToList();
        for (var i = 0; groups.Count < 3 && i < rest.Count; i += 3) groups.Add(("", rest.Skip(i).Take(3).ToList()));
        if (groups.Count is > 0 and < 3 && groups[0].Artists.Count > 3)
        {
            groups.Add((groups[0].Genre, groups[0].Artists.Skip(3).Take(3).ToList()));
            groups[0] = (groups[0].Genre, groups[0].Artists.Take(3).ToList());
        }

        var mixes = new List<MusicPlaylist>();
        for (var i = 0; i < groups.Count; i++)
        {
            var (genre, seeds) = groups[i];
            var known = history.Where(e => seeds.Any(s => MusicTrack.Normalize(s) == MusicTrack.Normalize(e.Track.Artist))).Select(e => e.Track).ToList();
            var random = new Random(now.DayNumber() * 31 + i);
            var tracks = await PickAsync(seeds.Take(3).ToList(), known, MixSize / 3, used, random, ct).ConfigureAwait(false);
            // Один исполнитель с коротким «радио» — добираем от других любимых, а не показываем куцый микс.
            if (tracks.Count < MinSize)
                tracks = await PickAsync([.. seeds.Take(3), .. artists.Except(seeds).Take(3)], known, MixSize / 3, used, random, ct).ConfigureAwait(false);
            if (tracks.Count < MinSize) continue;
            used.AddRange(tracks);
            var who = string.Join(", ", seeds.Take(2));
            mixes.Add(new MusicPlaylist(-(i + 1), $"Микс {mixes.Count + 1}", tracks.Count, null, null)
            {
                Kind = MusicPlaylist.KindMix,
                Caption = genre.Length > 0 ? $"{genre} · {who}" : who,
                Tracks = tracks,
            });
        }
        return mixes;
    }

    private static async Task<MusicPlaylist?> BuildMoodAsync(MusicLibrary library, IReadOnlyList<MusicHistoryEntry> history, List<MusicTrack> used, DateTime now, CancellationToken ct)
    {
        var utc = now.ToUniversalTime();
        var today = history.Where(e => utc - e.LastPlayedUtc < TimeSpan.FromHours(24)).ToList();
        if (today.Count < 5) today = history.Take(10).ToList(); // сегодня почти не слушали — последнее
        var seeds = today.Where(e => e.Track.Artist.Length > 0).GroupBy(e => MusicTrack.Normalize(MusicTrack.MainArtist(e.Track)))
            .OrderByDescending(g => g.Sum(e => e.Plays)).Select(g => MusicTrack.MainArtist(g.First().Track)).Take(4).ToList();
        if (seeds.Count == 0) return null;
        var part = DayPart(now);
        var random = new Random(now.DayNumber() * 7 + part.Length * 13 + now.Hour / 6);
        var known = today.Select(e => e.Track).ToList();
        var tracks = await PickAsync(seeds, known, 6, used, random, ct).ConfigureAwait(false);
        if (tracks.Count < MinSize)
            tracks = await PickAsync([.. seeds, .. library.TopArtists(4).Except(seeds)], known, 6, used, random, ct).ConfigureAwait(false);
        if (tracks.Count < MinSize) return null;
        used.AddRange(tracks);
        return new MusicPlaylist(-10, "Настроение дня", tracks.Count, null, null)
        {
            Kind = MusicPlaylist.KindMood,
            Caption = $"{part} · {string.Join(", ", seeds.Take(2))}",
            Tracks = tracks,
        };
    }

    // Знакомые песни вперемешку с «радио» исполнителей: после каждых двух новых — одна знакомая. Названия не повторяются (и в других подборках),
    // от одного автора — не больше двух песен подряд.
    private static async Task<List<MusicTrack>> PickAsync(IReadOnlyList<string> seeds, IReadOnlyList<MusicTrack> known, int knownCount, List<MusicTrack> used, Random random, CancellationToken ct)
    {
        var pools = await Task.WhenAll(seeds.Select(async name =>
        {
            if (await MusicCatalog.DeezerFindArtistAsync(name, ct).ConfigureAwait(false) is not { } artist) return (IReadOnlyList<MusicTrack>)[];
            return await MusicCatalog.DeezerArtistRadioAsync(artist.Id, 25, ct).ConfigureAwait(false);
        })).ConfigureAwait(false);
        var fresh = new Queue<MusicTrack>(MusicTrack.Distinct(pools.SelectMany(p => p)).OrderBy(_ => random.Next()));
        var old = new Queue<MusicTrack>(known.OrderBy(_ => random.Next()).Take(knownCount));
        var titles = used.Select(MusicTrack.TitleKey).ToHashSet();
        var list = new List<MusicTrack>();
        while (fresh.Count > 0 || old.Count > 0)
        {
            var next = (list.Count % 3 == 2 && old.Count > 0) || fresh.Count == 0 ? old.Dequeue() : fresh.Dequeue();
            var title = MusicTrack.TitleKey(next);
            if (title.Length == 0 || !titles.Add(title)) continue;
            list.Add(next);
        }
        list = MusicTrack.LimitRuns(list).Take(MixSize).ToList();
        Logging.Write($"music mix: {string.Join(", ", seeds.Take(2))} — {list.Count} песен");
        return list;
    }

    private static int DayNumber(this DateTime d) => DateOnly.FromDateTime(d).DayNumber;
}
