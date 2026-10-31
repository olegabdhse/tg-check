using System.Text.Json;
using TgCheck.Core.Services;

namespace TgCheck.Core.Music;

/// <summary>
/// Что слушали (только песни: голосовые и музыка историй сюда не попадают) и подбор по прослушанному.
/// История хранится в music\history.json, пишется в фоне не чаще раза в пару секунд.
/// </summary>
public sealed class MusicLibrary
{
    private const int MaxHistory = 300;
    private readonly string _file;
    private readonly List<MusicHistoryEntry> _items = [];
    private readonly object _lock = new();
    private int _saveQueued;

    public MusicLibrary(string dataDirectory)
    {
        Directory = Path.Combine(dataDirectory, "music");
        System.IO.Directory.CreateDirectory(Directory);
        CoversDirectory = Path.Combine(Directory, "covers");
        System.IO.Directory.CreateDirectory(CoversDirectory);
        _file = Path.Combine(Directory, "history.json");
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<List<MusicHistoryEntry>>(File.ReadAllText(_file)) is { } saved)
                _items.AddRange(saved.Where(e => e.Track is { Title.Length: > 0 } && e.Track.Source != MusicSource.Audius)); // Audius больше не используется
        }
        catch (Exception ex) { Logging.Write($"music history: {ex.Message}"); }
    }

    public string Directory { get; }
    public string CoversDirectory { get; }

    /// <summary>История изменилась (из любого потока).</summary>
    public event Action? Changed;

    /// <summary>Сколько раз что-то слушали за всё время (для «подбор устарел»).</summary>
    public int Version { get; private set; }

    /// <summary>Сначала последнее прослушанное.</summary>
    public IReadOnlyList<MusicHistoryEntry> History
    {
        get { lock (_lock) return _items.ToList(); }
    }

    /// <summary>Трек зазвучал — наверх истории.</summary>
    public void Record(MusicTrack track)
    {
        lock (_lock)
        {
            var i = _items.FindIndex(e => MusicTrack.SameSong(e.Track, track));
            MusicHistoryEntry entry;
            if (i >= 0)
            {
                entry = _items[i];
                _items.RemoveAt(i);
                // Обложка, найденная позже, остаётся.
                track.CoverUrl ??= entry.Track.CoverUrl;
                track.CoverBigUrl ??= entry.Track.CoverBigUrl;
                entry = new MusicHistoryEntry { Track = track, Plays = entry.Plays + 1, LastPlayedUtc = DateTime.UtcNow };
            }
            else entry = new MusicHistoryEntry { Track = track, Plays = 1, LastPlayedUtc = DateTime.UtcNow };
            _items.Insert(0, entry);
            if (_items.Count > MaxHistory) _items.RemoveRange(MaxHistory, _items.Count - MaxHistory);
            Version++;
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Нашлась обложка для песни из истории.</summary>
    public void SetCover(string key, string small, string big)
    {
        lock (_lock)
            foreach (var e in _items.Where(e => e.Track.Key == key))
            {
                e.Track.CoverUrl = small;
                e.Track.CoverBigUrl = big;
            }
        Save();
    }

    public void Remove(string key)
    {
        lock (_lock) _items.RemoveAll(e => e.Track.Key == key);
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (Interlocked.Exchange(ref _saveQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            Interlocked.Exchange(ref _saveQueued, 0);
            try
            {
                string json;
                lock (_lock) json = JsonSerializer.Serialize(_items);
                var tmp = _file + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, _file, overwrite: true);
            }
            catch (Exception ex) { Logging.Write($"music history save: {ex.Message}"); }
        });
    }

    // ===================================================================== Кэш полок

    /// <summary>Сохранённая полка: что на ней было, когда собрана и по чему (подпись — от чего она зависит).</summary>
    public sealed record ShelfCache(DateTime SavedUtc, string Signature, List<MusicTrack> Tracks);

    private string ShelfFile(string name) => Path.Combine(Directory, "shelf_" + name + ".json");

    /// <summary>Полка с прошлого раза (null — нет или не читается).</summary>
    public ShelfCache? LoadShelf(string name)
    {
        try
        {
            var file = ShelfFile(name);
            return File.Exists(file) ? JsonSerializer.Deserialize<ShelfCache>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex)
        {
            Logging.Write($"music shelf {name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Запомнить полку (в фоне) — при следующем открытии она видна сразу.</summary>
    public void SaveShelf(string name, string signature, IEnumerable<MusicTrack> tracks)
    {
        var cache = new ShelfCache(DateTime.UtcNow, signature, tracks.ToList());
        _ = Task.Run(() =>
        {
            try
            {
                var tmp = ShelfFile(name) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache));
                File.Move(tmp, ShelfFile(name), overwrite: true);
            }
            catch (Exception ex) { Logging.Write($"music shelf {name} save: {ex.Message}"); }
        });
    }

    // ===================================================================== Подбор

    /// <summary>Исполнители, которых слушают больше и чаще в последнее время (вес: прослушивания, свежесть).</summary>
    public IReadOnlyList<string> TopArtists(int count)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
            return _items.Where(e => !string.IsNullOrWhiteSpace(e.Track.Artist))
                .GroupBy(e => MusicTrack.Normalize(e.Track.Artist))
                .Select(g => (Name: g.First().Track.Artist,
                    Score: g.Sum(e => e.Plays / (1 + (now - e.LastPlayedUtc).TotalDays / 7))))
                .OrderByDescending(a => a.Score).Take(count).Select(a => a.Name).ToList();
    }

    public bool WasPlayed(MusicTrack t)
    {
        lock (_lock) return _items.Any(e => MusicTrack.SameSong(e.Track, t));
    }

    /// <summary>
    /// Подбор по прослушанному — только полные треки: песни похожих исполнителей (кто похож — подсказывает Deezer)
    /// и ещё от любимых исполнителей ищутся в Telegram.
    /// Уже прослушанное не предлагается.
    /// </summary>
    /// <param name="searchTelegram">Песни исполнителя в чатах и каналах Telegram.</param>
    /// <param name="partial">Найденное по одному исполнителю — сразу (true — похожие, false — ещё от любимых), без уже прослушанного.</param>
    public async Task<Recommendations> RecommendAsync(Func<string, Task<IReadOnlyList<MusicTrack>>> searchTelegram,
        Action<bool, IReadOnlyList<MusicTrack>>? partial = null, CancellationToken ct = default)
    {
        var artists = TopArtists(4);
        var similar = new List<MusicTrack>();
        var more = new List<MusicTrack>();
        // Поиск в Telegram — не больше трёх разом и не больше 12 исполнителей (не упираться в ограничения Telegram).
        var gate = new SemaphoreSlim(3);

        async Task<IReadOnlyList<MusicTrack>> SongsOf(string artist, int take)
        {
            await gate.WaitAsync(ct);
            try
            {
                var want = MusicTrack.Normalize(artist);
                return (await searchTelegram(artist)).Where(t => t.Duration >= 60 && MusicTrack.Normalize(t.Artist).Contains(want))
                    .Take(take).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Logging.Write($"music recs tg: {ex.Message}"); return []; }
            finally { gate.Release(); }
        }

        var tasks = new List<Task>();
        foreach (var name in artists)
            tasks.Add(Task.Run(async () =>
            {
                var own = SongsOf(name, 6);
                if (await MusicCatalog.DeezerFindArtistAsync(name, ct) is { } artist)
                {
                    var related = await MusicCatalog.DeezerRelatedAsync(artist.Id, 3, ct);
                    var found = await Task.WhenAll(related.Select(async r =>
                    {
                        var songs = await SongsOf(r.Name, 4);
                        partial?.Invoke(true, songs.Where(t => !WasPlayed(t)).ToList());
                        return songs;
                    }));
                    lock (similar) similar.AddRange(found.SelectMany(t => t));
                }
                var ownSongs = await own;
                partial?.Invoke(false, ownSongs.Where(t => !WasPlayed(t)).ToList());
                lock (more) more.AddRange(ownSongs);
            }, ct));
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logging.Write($"music recs: {ex.Message}"); }

        return new Recommendations(Fresh(similar, 30), Fresh(more, 24), artists.Count > 0);
    }

    // Без повторов и без уже прослушанного; перемешано, чтобы исполнители чередовались.
    private List<MusicTrack> Fresh(List<MusicTrack> tracks, int take)
    {
        var rnd = new Random();
        return MusicTrack.Distinct(tracks.Where(t => !WasPlayed(t))).OrderBy(_ => rnd.Next()).Take(take).ToList();
    }
}

/// <summary>Подбор: похожее на прослушанное и ещё от любимых исполнителей (полные песни из Telegram).</summary>
public sealed record Recommendations(IReadOnlyList<MusicTrack> Similar, IReadOnlyList<MusicTrack> MoreFromArtists, bool HasHistory);
