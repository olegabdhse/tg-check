using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Models;
using TgCheck.Core.Music;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Трек в списках раздела «Музыка»: обложка грузится, только когда строка появилась на экране.</summary>
public sealed partial class MusicTrackViewModel(MusicTrack track, IList<MusicTrackViewModel>? list = null) : ObservableObject
{
    public MusicTrack Track { get; } = track;
    /// <summary>Список, из которого трек запущен, — он и играет дальше по порядку.</summary>
    public IList<MusicTrackViewModel>? List { get; set; } = list;

    public string Title => Track.Title;
    public string Artist => Track.Artist.Length > 0 ? Track.Artist : "Неизвестный исполнитель";
    public string DurationText => Track.Duration > 0 ? Fmt.Duration(Track.Duration) : "";
    public string SourceText => Track.Source switch
    {
        MusicSource.Telegram => "Telegram",
        MusicSource.Audius => "Audius",
        MusicSource.Bot => "@" + Track.BotName,
        _ => "Deezer",
    };
    public Brush CoverBrush { get; } = MessageViewModel.GradientFor(track.Title, track.Artist);
    /// <summary>Плашка источника — только у ботов: песни Telegram (их большинство) и каталога (файл всё равно из Telegram) без неё.</summary>
    public bool ShowSource => Track.Source is not (MusicSource.Telegram or MusicSource.Deezer);
    public string Initial => Track.Title.Length > 0 ? char.ToUpper(Track.Title[0]).ToString() : "♪";

    [ObservableProperty] private bool _isCurrent;
    /// <summary>Обложка сама грузится при первом показе (история); у плиток и результатов поиска её просит страница — начиная с видимых.</summary>
    public bool AutoCover { get; init; } = true;
    /// <summary>Для истории: «3 раза · вчера».</summary>
    public string? PlaysText { get; init; }
    public bool IsHistory => PlaysText is not null;

    private ImageSource? _cover;
    private bool _coverRequested;

    /// <summary>Обложка (плитка/строка) — запрашивается при первом показе.</summary>
    public ImageSource? Cover
    {
        get
        {
            if (AutoCover) RequestCover();
            return _cover;
        }
    }

    public ImageSource? LoadedCover => _cover;

    /// <summary>Загрузить обложку (если ещё не просили).</summary>
    public void RequestCover()
    {
        if (_coverRequested) return;
        _coverRequested = true;
        CoverLoaded = LoadCoverAsync();
    }

    /// <summary>Обложка загрузилась (или её нет) — для незаметной подмены плитки.</summary>
    public Task CoverLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>Обложка для трека без ссылки на неё (песни Telegram): из самой песни или подобранная по названию.</summary>
    public static Func<MusicTrack, Task<ImageSource?>>? FindCover { get; set; }

    private async Task LoadCoverAsync()
    {
        var img = await MusicCovers.LoadAsync(Track.CoverUrl ?? Track.CoverBigUrl, 220, Track.CoverMirrors);
        if (img is null && Track.Source is MusicSource.Telegram or MusicSource.Bot && FindCover is { } find) img = await find(Track);
        if (img is null) return;
        _cover = img;
        OnPropertyChanged(nameof(Cover));
    }

    /// <summary>Нашлась обложка позже (песня Telegram без своей обложки).</summary>
    public void ReloadCover()
    {
        _coverRequested = false;
        RequestCover();
    }
}

/// <summary>Плейлист на полке: обложка грузится, когда плитка появилась на экране.</summary>
public sealed class MusicPlaylistViewModel(MusicPlaylist playlist) : ObservableObject
{
    public MusicPlaylist Playlist { get; } = playlist;
    public string Title => Playlist.Title;
    public string CountText => Playlist.TrackCount > 0 ? $"{Playlist.TrackCount} {Fmt.Plural(Playlist.TrackCount, "песня", "песни", "песен")}" : "";
    public Brush CoverBrush { get; } = MessageViewModel.GradientFor(playlist.Title, "");

    private ImageSource? _cover;
    private bool _coverRequested;

    public ImageSource? Cover
    {
        get
        {
            if (!_coverRequested)
            {
                _coverRequested = true;
                _ = LoadCoverAsync();
            }
            return _cover;
        }
    }

    private async Task LoadCoverAsync()
    {
        _cover = await MusicCovers.LoadAsync(Playlist.Cover ?? Playlist.CoverBig, 220);
        if (_cover is not null) Anim.Ui(() => OnPropertyChanged(nameof(Cover)));
    }
}

/// <summary>Полка плейлистов: популярные на главной, найденные в поиске.</summary>
public sealed partial class MusicPlaylistShelf(string title, string subtitle) : ObservableObject
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public ObservableCollection<MusicPlaylistViewModel> Items { get; } = [];
    public bool HasItems => Items.Count > 0;

    public void Fill(IEnumerable<MusicPlaylist> playlists)
    {
        Items.Clear();
        foreach (var p in playlists) Items.Add(new MusicPlaylistViewModel(p));
        OnPropertyChanged(nameof(HasItems));
    }
}

/// <summary>Полка главной: «Для вас», «Музыка из ваших чатов»…</summary>
public sealed partial class MusicShelf(string title, string? subtitle = null) : ObservableObject
{
    public string Title { get; } = title;
    public string? Subtitle { get; } = subtitle;
    public ObservableCollection<MusicTrackViewModel> Items { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsVisible), nameof(ShowSkeleton))] private bool _isLoading = true;
    public bool IsVisible => IsLoading || Items.Count > 0;
    /// <summary>Грузится и ещё пусто — вместо пустоты «скелеты» плиток.</summary>
    public bool ShowSkeleton => IsLoading && Items.Count == 0;
    /// <summary>Сколько «скелетов» рисовать.</summary>
    public static int[] Placeholders { get; } = [0, 1, 2, 3, 4, 5, 6, 7];
    public static int[] RowPlaceholders { get; } = [0, 1, 2, 3];
    public static int[] GridPlaceholders { get; } = [0, 1, 2, 3];
    /// <summary>Сколько треков показывать (остальное не добавляется).</summary>
    public int Max { get; init; } = int.MaxValue;
    /// <summary>Показывается вперемешку; новый порядок — только когда полки не видно (Reshuffle зовёт страница).</summary>
    public bool Shuffled { get; init; }
    /// <summary>Полку можно открыть страницей («Все»).</summary>
    public bool CanOpenPage { get; init; }
    /// <summary>В результатах поиска: пустая группа не показывается (без «Ничего не нашлось»).</summary>
    public bool Optional { get; init; }

    public void Fill(IEnumerable<MusicTrack> tracks)
    {
        Items.Clear();
        Append(Shuffled ? tracks.OrderBy(_ => Random.Shared.Next()) : tracks);
        Done();
    }

    /// <summary>Перемешать заново (полку сейчас не видно — перестановку никто не заметит).</summary>
    public void Reshuffle()
    {
        if (!Shuffled || Items.Count < 2) return;
        var order = Items.OrderBy(_ => Random.Shared.Next()).ToList();
        for (var i = 0; i < order.Count; i++)
            if (Items.IndexOf(order[i]) is var from && from != i) Items.Move(from, i);
    }

    /// <summary>Начать заново: пусто, грузится.</summary>
    public void Begin()
    {
        Items.Clear();
        IsLoading = true;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ShowSkeleton));
    }

    /// <summary>
    /// Дописать пришедшее (без повторов той же песни) — полка показывает его сразу, не дожидаясь остального.
    /// Обложки плиток грузит страница — по очереди, начиная с видимых.
    /// </summary>
    public void Append(IEnumerable<MusicTrack> tracks, Func<MusicTrack, bool>? skip = null)
    {
        foreach (var t in tracks)
            if (Items.Count < Max && skip?.Invoke(t) != true && !Items.Any(x => MusicTrack.SameSong(x.Track, t)))
                Items.Add(new MusicTrackViewModel(t, Items) { AutoCover = false });
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ShowSkeleton));
    }

    /// <summary>
    /// Привести полку к списку: уже показанные песни остаются (с обложками) и только переставляются, новые встают на свои
    /// места, лишние убираются.
    /// </summary>
    public void Sync(IReadOnlyList<MusicTrack> tracks)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            var existing = -1;
            for (var j = i; j < Items.Count; j++)
                if (Items[j].Track.Key == tracks[i].Key) { existing = j; break; }
            if (existing == i) continue;
            if (existing > i) Items.Move(existing, i);
            else Items.Insert(i, new MusicTrackViewModel(tracks[i], Items) { AutoCover = false });
        }
        while (Items.Count > tracks.Count) Items.RemoveAt(Items.Count - 1);
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ShowSkeleton));
    }

    public void Done()
    {
        IsLoading = false;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ShowSkeleton));
    }

    /// <summary>
    /// Подменить полку новой незаметно: старые плитки остаются, пока не готова замена; новые встают по одной, когда их
    /// обложка уже загрузилась (не больше 3 с ожидания), с небольшой паузой. Совпавшие и играющая сейчас не трогаются.
    /// </summary>
    public async Task ReplaceGraduallyAsync(IEnumerable<MusicTrack> tracks)
    {
        var target = MusicTrack.Distinct(Shuffled ? tracks.OrderBy(_ => Random.Shared.Next()) : tracks).Take(Max).ToList();
        for (var i = 0; i < target.Count; i++)
        {
            var t = target[i];
            if (i < Items.Count && (MusicTrack.SameSong(Items[i].Track, t) || Items[i].IsCurrent)) continue;
            if (Items.Where((x, j) => j != i).Any(x => MusicTrack.SameSong(x.Track, t))) continue; // уже есть в другом месте
            var vm = new MusicTrackViewModel(t, Items);
            vm.RequestCover();
            await Task.WhenAny(vm.CoverLoaded, Task.Delay(3000));
            if (i < Items.Count) Items[i] = vm;
            else Items.Add(vm);
            await Task.Delay(250);
        }
        for (var i = Items.Count - 1; i >= target.Count; i--)
            if (!Items[i].IsCurrent) Items.RemoveAt(i);
        Done();
    }
}

/// <summary>
/// Раздел «Музыка»: поиск сразу в Telegram (полные песни из чатов и каналов); подбор по прослушанному,
/// история и плеер. Запущенное отсюда играет по списку, а когда список кончился — продолжается подбором.
/// </summary>
public sealed partial class MusicViewModel : ObservableObject
{
    private readonly TelegramService _tg;
    private readonly Action<string, bool> _toast;
    private readonly MusicLibrary _library;
    private bool _loaded;

    public MusicViewModel(TelegramService tg, Action<string, bool> toast)
    {
        _tg = tg;
        _toast = toast;
        _library = tg.MusicLibrary;
        MusicCovers.Init(_library);
        MusicTrackViewModel.FindCover = FindTelegramCoverAsync;
        // Сердечко в плеерах: песня в музыке профиля. Список любимых — сразу (по нему закрашено сердечко).
        MusicNow.IsLikedQuery = song => _tg.IsLikedSong(song.MediaId);
        MusicNow.SetLiked = SetLikedAsync;
        _ = LoadFavoritesAsync();
        // Послушали что-то (и в чатах тоже) — история и «Для вас» обновляются.
        _library.Changed += () => Anim.Ui(() => { RefreshHistory(); if (_loaded) { RequestNext(); _ = RefreshPlaylistsAsync(); } });
        MusicPlayer.Instance.Ended += key => Anim.Ui(() => { if (key == _current?.Track.Key) _ = NextAsync(auto: true); });
        MusicNow.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MusicNow.Title)) OnPropertyChanged(nameof(HasNowPlaying));
        };
    }

    // ===================================================================== Главная

    public MusicShelf ForYou { get; } = new("Похожие исполнители", "Полные песни из Telegram");
    public MusicShelf MoreFromArtists { get; } = new("Ещё от ваших исполнителей", "Полные песни из Telegram") { Max = 6 };
    public MusicShelf InChats { get; } = new("Музыка из ваших чатов", "Полные песни из чатов и каналов Telegram") { Shuffled = true, CanOpenPage = true };

    /// <summary>«Любимые» — музыка своего профиля Telegram (сердечко в плеере добавляет туда).</summary>
    public MusicShelf Favorites { get; } = new("Любимые", "Музыка вашего профиля — сердечко в плеере добавляет сюда") { Shuffled = true };

    public IReadOnlyList<MusicShelf> Shelves => [Favorites, MoreFromArtists, InChats]; // «Для вас» не показываем — его песни играют в подборе после очереди

    /// <summary>«Для вас»: 4 песни сеткой по семи последним прослушанным — перестраивается после каждой песни.</summary>
    public MusicShelf NextForYou { get; } = new("Для вас", "По последним песням");

    private IEnumerable<MusicShelf> AllShelves => [NextForYou, .. Shelves, .. SearchShelves, ChatsPage, PlaylistPage];

    // ===================================================================== Плейлисты

    private const int PlaylistsShown = 4;
    public MusicPlaylistShelf Playlists { get; } = new("Плейлисты", "");
    public MusicPlaylistShelf FoundPlaylists { get; } = new("Плейлисты", "Подборки по вашему запросу");
    public bool ShowFoundPlaylists => IsSearch && FoundPlaylists.HasItems;

    /// <summary>Открытый плейлист: страница со списком песен.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPlaylistPage))] private MusicPlaylistViewModel? _openedPlaylist;
    public bool IsPlaylistPage => OpenedPlaylist is not null;
    public MusicShelf PlaylistPage { get; } = new("Плейлист");
    private CancellationTokenSource? _playlistCts;


    private string? _playlistSeeds;
    private CancellationTokenSource? _playlistPickCts;

    // Плейлисты, в которых реально есть исполнители последних песен (и любимые): поиск по слову в названии находил «Pop México» вместо своего.
    // По одному от каждого исполнителя, чтобы подборка была разной; не хватило — чарт основного жанра, потом общий. Выбор с прошлого раза виден
    // сразу, а подходящие плейлисты остаются на месте: от каждой новой песни полка не мигает. Песни плейлиста грузятся только при открытии.
    private async Task RefreshPlaylistsAsync()
    {
        if (!Playlists.HasItems && _library.LoadPlaylists() is { } saved)
        {
            Playlists.Fill(saved.Playlists);
            _playlistSeeds = saved.Signature;
        }
        _playlistPickCts?.Cancel();
        var cts = _playlistPickCts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            var recent = _library.History.Take(7).Select(e => MusicTrack.MainArtist(e.Track)).Where(a => a.Length > 0).ToList();
            var artists = recent.Concat(_library.TopArtists(4)).DistinctBy(MusicTrack.Normalize).Take(8).ToList();
            var seeds = string.Join("|", artists.Select(MusicTrack.Normalize));
            if (seeds == _playlistSeeds) return;
            var perArtist = await Task.WhenAll(artists.Select(a => MusicCatalog.DeezerArtistPlaylistsAsync(a, 25, ct)));
            // Чем больше ваших исполнителей в плейлисте, тем он выше у каждого из них.
            var hits = perArtist.SelectMany(l => l).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Count());
            var candidates = perArtist.Select(l => l.OrderByDescending(p => hits[p.Id]).ToList()).ToList();
            // Что уже показано и всё ещё подходит, остаётся.
            var picked = Playlists.Items.Select(v => v.Playlist).Where(p => hits.ContainsKey(p.Id)).Take(PlaylistsShown).ToList();
            bool Add(MusicPlaylist p)
            {
                if (picked.Count >= PlaylistsShown || picked.Any(x => x.Id == p.Id)) return false;
                picked.Add(p);
                return true;
            }
            for (var progress = true; progress && picked.Count < PlaylistsShown;)
            {
                progress = false;
                foreach (var list in candidates)
                    if (list.FirstOrDefault(p => picked.All(x => x.Id != p.Id)) is { } next) progress |= Add(next);
            }
            if (picked.Count < PlaylistsShown)
            {
                var genres = await Task.WhenAll(recent.DistinctBy(MusicTrack.Normalize).Select(a => _library.ArtistGenreAsync(a, ct)));
                var main = genres.Where(g => g.Length > 0).GroupBy(g => g).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
                if (main is not null)
                    foreach (var p in await MusicCatalog.DeezerGenrePlaylistsAsync(main, PlaylistsShown * 2, ct)) Add(p);
            }
            if (picked.Count < PlaylistsShown)
                foreach (var p in await MusicCatalog.DeezerChartPlaylistsAsync(PlaylistsShown * 2, ct)) Add(p);
            // В списке плейлистов исполнителя числа песен нет — спрашиваем только у выбранных.
            var complete = await Task.WhenAll(picked.Select(p => p.TrackCount > 0 ? Task.FromResult(p) : MusicCatalog.DeezerPlaylistWithCountAsync(p, ct)));
            if (ct.IsCancellationRequested || complete.Length == 0) return;
            _playlistSeeds = seeds;
            _library.SavePlaylists(seeds, complete);
            if (!complete.Select(p => p.Id).SequenceEqual(Playlists.Items.Select(v => v.Playlist.Id))) Playlists.Fill(complete);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logging.Write($"music playlists: {ex.Message}"); }
    }

    private async Task SearchPlaylistsAsync(string q, CancellationToken ct)
    {
        var found = await MusicCatalog.DeezerSearchPlaylistsAsync(q, ct: ct);
        if (ct.IsCancellationRequested) return;
        FoundPlaylists.Fill(found);
        OnPropertyChanged(nameof(ShowFoundPlaylists));
    }

    [RelayCommand]
    private async Task OpenPlaylist(MusicPlaylistViewModel? playlist)
    {
        if (playlist is null) return;
        _playlistCts?.Cancel();
        var cts = _playlistCts = new CancellationTokenSource();
        OpenedPlaylist = playlist;
        PlaylistPage.Begin();
        IReadOnlyList<MusicTrack> tracks;
        try { tracks = await MusicCatalog.DeezerPlaylistTracksAsync(playlist.Playlist.Id, ct: cts.Token); }
        catch (OperationCanceledException) { return; } // закрыли или открыли другой
        if (cts.IsCancellationRequested) return;
        PlaylistPage.Fill(tracks);
        SetCurrentMarks();
    }

    [RelayCommand]
    private void ClosePlaylist()
    {
        _playlistCts?.Cancel();
        OpenedPlaylist = null;
    }

    [RelayCommand]
    private Task PlayPlaylist() => PlaylistPage.Items.Count > 0 ? Play(PlaylistPage.Items[0]) : Task.CompletedTask;

    [RelayCommand]
    private Task ShufflePlaylist()
    {
        if (PlaylistPage.Items.Count == 0) return Task.CompletedTask;
        StopSearch();
        _queue = [.. PlaylistPage.Items.OrderBy(_ => Random.Shared.Next())];
        _index = 0;
        QueueTitle = OpenedPlaylist?.Title;
        return PlayCurrentAsync();
    }

    private CancellationTokenSource? _nextCts;
    private string? _nextSeeds;
    private Task _nextTask = Task.CompletedTask;
    // Плитки, уже заменённые в этом проходе (их не трогаем, пока не заменим остальные).
    private readonly HashSet<MusicTrackViewModel> _nextFresh = [];

    private void RequestNext(bool soon = false) => _nextTask = BuildNextAsync(soon);

    // Жанр песен «Для вас» и доля каждого жанра в семи последних — следующей чаще играет песня того жанра, который слушали больше.
    private readonly Dictionary<string, string> _genreOf = [];
    private Dictionary<string, double> _genreShare = [];

    /// <summary>
    /// «Для вас». Семь последних песен делятся по жанрам (жанр исполнителя — из Deezer), и 4 места делятся между жанрами
    /// в той же пропорции: 5 из 7 — рэп, 2 из 7 — рок → 3 рэп и 1 рок. Песни жанра — из «радио» его исполнителей в Deezer
    /// (похожее звучание и настроение), без исполнителей, которых только что слушали. У каждой находится полная версия —
    /// в чатах или у музыкальных ботов; песня, которую уже слушали, берётся именно та, что играла.
    /// Если полка уже на экране, она не очищается: через 10 секунд (вдруг песню сразу переключат) плитки заменяются
    /// по одной, по мере нахождения — сначала уже прослушанные, играющая сейчас не трогается.
    /// </summary>
    /// <param name="soon">Без паузы в 10 с (раздел только что открыли, а полка с прошлого раза устарела).</param>
    private async Task BuildNextAsync(bool soon = false)
    {
        var recent = _library.History.Take(7).Select(e => e.Track).ToList();
        var seeds = string.Join("|", recent.Select(t => t.Key));
        if (seeds == _nextSeeds) return;
        _nextSeeds = seeds;
        _nextCts?.Cancel();
        var cts = _nextCts = new CancellationTokenSource();
        var ct = cts.Token;
        if (recent.Count == 0) { NextForYou.Done(); return; }
        var replace = NextForYou.Items.Count > 0;
        if (!replace) NextForYou.Begin();
        _nextFresh.Clear();
        try
        {
            if (replace && !soon) await Task.Delay(10_000, ct);
            await WhenIdleAsync();
            var recentArtists = recent.Select(t => MusicTrack.Normalize(MusicTrack.MainArtist(t))).Where(a => a.Length > 0).ToHashSet();
            // Жанр каждой из семи песен; неизвестный жанр — сам исполнитель (его «радио» — то же звучание).
            var genres = await Task.WhenAll(recent.Select(t => Task.Run(async () =>
            {
                var artist = MusicTrack.MainArtist(t);
                var g = await _library.ArtistGenreAsync(artist, ct);
                return g.Length > 0 ? g : "artist:" + MusicTrack.Normalize(artist);
            }, ct)));
            var groups = recent.Select((t, i) => (Track: t, Genre: genres[i])).GroupBy(x => x.Genre)
                .Select(g => (Genre: g.Key, Artists: g.Select(x => MusicTrack.MainArtist(x.Track)).Where(a => a.Length > 0)
                                                       .DistinctBy(MusicTrack.Normalize).Take(3).ToList(),
                              Share: (double)g.Count() / recent.Count))
                .OrderByDescending(g => g.Share).ToList();
            _genreShare = groups.ToDictionary(g => g.Genre, g => g.Share);
            var slots = Apportion(groups.Select(g => g.Share).ToList(), 4);
            Logging.Write("music next: " + string.Join(", ", groups.Select((g, i) => $"{g.Genre} {g.Share:P0} → {slots[i]}")));
            // Кандидаты жанра — «радио» его исполнителей вперемешку (сначала самого свежего).
            var pools = await Task.WhenAll(groups.Select((g, i) => slots[i] == 0 ? Task.FromResult(new List<MusicTrack>()) : Task.Run(async () =>
            {
                var pool = new List<MusicTrack>();
                foreach (var name in g.Artists.Take(2))
                {
                    if (await MusicCatalog.DeezerFindArtistAsync(name, ct) is not { } artist) continue;
                    pool.AddRange((await MusicCatalog.DeezerArtistRadioAsync(artist.Id, 25, ct))
                        .Where(c => !recentArtists.Contains(MusicTrack.Normalize(MusicTrack.MainArtist(c))))
                        .OrderBy(_ => Random.Shared.Next()));
                }
                return MusicTrack.Distinct(pool);
            }, ct)));
            // По очереди между жанрами (больший — первым), пока у каждого не наберётся его доля мест.
            var found = new List<MusicTrack>();
            var taken = new int[groups.Count];
            var next = new int[groups.Count];
            var tries = new int[groups.Count];
            for (var progress = true; progress && found.Count < 4 && !ct.IsCancellationRequested;)
            {
                progress = false;
                for (var s = 0; s < groups.Count && found.Count < 4; s++)
                {
                    if (taken[s] >= slots[s] || next[s] >= pools[s].Count || tries[s] >= 3 + slots[s] * 3) continue;
                    progress = true;
                    var candidate = pools[s][next[s]++];
                    if (found.Any(f => MusicTrack.SameSong(f, candidate))) continue;
                    if (!replace && NextForYou.Items.Any(x => MusicTrack.SameSong(x.Track, candidate))) continue;
                    if (_played.Any(p => MusicTrack.SameSong(p, candidate))) continue; // уже звучала в этот запуск
                    tries[s]++;
                    MusicTrack? full;
                    if (_library.Played(candidate) is { } before) full = before; // уже слушали — та же самая, что играла
                    else
                    {
                        await WhenIdleAsync();
                        full = await FullSongAsync(candidate);
                        if (full is not null)
                        {
                            // Обложка — из Deezer (крупная и чёткая), если у найденной своей нет.
                            full.CoverUrl ??= candidate.CoverUrl;
                            full.CoverBigUrl ??= candidate.CoverBigUrl;
                        }
                    }
                    if (full is null || ct.IsCancellationRequested) continue;
                    taken[s]++;
                    found.Add(full);
                    _genreOf[full.Key] = groups[s].Genre;
                    if (replace) await ReplaceNextSlotAsync(full, ct);
                    else NextForYou.Append([full]);
                }
                // Жанр не набрал своих мест (не нашлось полных версий) — свободные места отдаются остальным по очереди.
                if (!progress && found.Count < 4)
                    for (var s = 0; s < groups.Count; s++)
                        if (next[s] < pools[s].Count && tries[s] < 12) { slots[s] = 4; progress = true; }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Logging.Write($"music next: {ex.Message}"); }
        if (ct.IsCancellationRequested) return;
        NextForYou.Done();
        _library.SaveShelf("next", seeds, NextForYou.Items.Select(v => v.Track));
        PrefetchNext(); // новые плитки — заранее подготовить
    }

    // Мест — по долям (метод наибольших остатков): [5/7, 2/7] на 4 места → [3, 1]; сумма всегда ровно total.
    private static int[] Apportion(IReadOnlyList<double> shares, int total)
    {
        var exact = shares.Select(s => s * total).ToArray();
        var seats = exact.Select(e => (int)Math.Floor(e)).ToArray();
        foreach (var i in Enumerable.Range(0, shares.Count).OrderByDescending(i => exact[i] - seats[i]).Take(total - seats.Sum()))
            seats[i]++;
        return seats;
    }

    // Заменить одну плитку «Для вас»: сначала уже прослушанную, потом по порядку слева; играющую сейчас
    // и уже заменённые в этом проходе не трогаем. Плиток меньше четырёх — новая добавляется.
    private async Task ReplaceNextSlotAsync(MusicTrack track, CancellationToken ct)
    {
        var items = NextForYou.Items;
        if (items.Any(x => MusicTrack.SameSong(x.Track, track))) return;
        var vm = new MusicTrackViewModel(track, items);
        // Подмена незаметная: плитка встаёт, когда её обложка уже загрузилась.
        vm.RequestCover();
        await Task.WhenAny(vm.CoverLoaded, Task.Delay(3000, ct));
        if (ct.IsCancellationRequested) return;
        _nextFresh.Add(vm);
        if (items.Count < 4) { items.Add(vm); return; }
        var free = Enumerable.Range(0, items.Count).Where(i => !items[i].IsCurrent && !_nextFresh.Contains(items[i])).ToList();
        if (free.Count == 0) return;
        var slot = free.FirstOrDefault(i => _played.Any(p => MusicTrack.SameSong(p, items[i].Track)), free[0]);
        items[slot] = vm;
    }

    // Полная версия трека каталога: в чатах и каналах; нет — у двух лучших музыкальных ботов.
    private async Task<MusicTrack?> FullSongAsync(MusicTrack t)
    {
        var query = $"{MusicTrack.MainArtist(t)} {t.Title}".Trim();
        try
        {
            var chats = (await _tg.SearchMusicAsync(query, 10, background: true)).Select(MusicTrack.FromTelegram).Where(MusicTrack.IsSong);
            if (chats.FirstOrDefault(c => c.Duration >= 60 && MusicTrack.SameSong(c, t)) is { } inChats) return inChats;
            var bots = await _tg.SearchBotMusicAsync(query, maxBots: 2, background: true);
            return bots.FirstOrDefault(b => MusicTrack.SameSong(b, t));
        }
        catch (Exception ex)
        {
            Logging.Write($"music full song: {ex.Message}");
            return null;
        }
    }

    private Recommendations? _recs;
    private int _recsVersion = -1;

    // Когда полки собраны и по чему: «Ещё от ваших исполнителей» — по четырём любимым исполнителям (раз в 6 часов
    // или когда они сменились), «Музыка из ваших чатов» — раз в 30 минут. До этого — из кэша, без поиска.
    private DateTime _artistsAt, _chatsAt;
    private string? _artistsSignature;
    private static readonly TimeSpan ArtistsFresh = TimeSpan.FromHours(6), ChatsFresh = TimeSpan.FromMinutes(30);
    private bool _homeBusy;

    /// <summary>
    /// Раздел открыли. Сразу — полки с прошлого раза (без ожидания). Первым — «Для вас»; остальное
    /// обновляется после него и только если устарело.
    /// </summary>
    public void Activate()
    {
        RefreshHistory();
        if (!_loaded)
        {
            _loaded = true;
            ShowCachedShelves();
            if (!_favoritesLoaded) _ = LoadFavoritesOnceAsync();
            _ = Task.Run(_tg.PrepareMusicBotsAsync); // боты — заранее, чтобы первый поиск ответил быстрее
            RequestNext(soon: true);
        }
        else RequestNext();
        _ = LoadHomeAsync();
        _ = RefreshPlaylistsAsync();
    }

    private void ShowCachedShelves()
    {
        if (_library.LoadShelf("next") is { Tracks.Count: > 0 } next)
        {
            NextForYou.Fill(next.Tracks);
            _nextSeeds = next.Signature; // с тех пор ничего не слушали — полка та же
        }
        if (_library.LoadShelf("artists") is { Tracks.Count: > 0 } artists)
        {
            MoreFromArtists.Fill(artists.Tracks.Where(t => !_library.WasPlayed(t)));
            _artistsAt = artists.SavedUtc;
            _artistsSignature = artists.Signature;
        }
        if (_library.LoadShelf("chats") is { Tracks.Count: > 0 } chats)
        {
            InChats.Fill(chats.Tracks);
            _chatsAt = chats.SavedUtc;
        }
    }

    private async Task LoadHomeAsync()
    {
        if (_homeBusy) return;
        _homeBusy = true;
        try
        {
            // «Для вас» — в первую очередь: остальное ждёт его (но не дольше 25 с).
            await Task.WhenAny(_nextTask, Task.Delay(25_000));
            if (InChats.Items.Count == 0 || DateTime.UtcNow - _chatsAt > ChatsFresh)
            {
                var chats = await Task.Run(async () =>
                {
                    try { return await _tg.SearchMusicAsync("", 30, background: true); }
                    catch (Exception ex) { Logging.Write($"music chats: {ex.Message}"); return []; }
                });
                if (chats.Count > 0 || InChats.Items.Count == 0)
                {
                    var fresh = MusicTrack.Distinct(chats.Select(MusicTrack.FromTelegram).Where(MusicTrack.IsSong));
                    if (InChats.Items.Count == 0) InChats.Fill(fresh);
                    else await InChats.ReplaceGraduallyAsync(fresh); // на экране старое — подменяется по одной
                    _chatsAt = DateTime.UtcNow;
                    _library.SaveShelf("chats", "", InChats.Items.Select(v => v.Track));
                }
            }
            if (MoreFromArtists.Items.Count == 0 || DateTime.UtcNow - _artistsAt > ArtistsFresh
                || _artistsSignature != ArtistsSignature())
                await LoadRecommendationsAsync();
        }
        finally { _homeBusy = false; }
    }

    private string ArtistsSignature() => string.Join("|", _library.TopArtists(4).Select(MusicTrack.Normalize));

    /// <summary>
    /// Подбор по любимым исполнителям. Полка пустая — заполняется по мере поиска; уже есть (из кэша) — остаётся
    /// на экране, а новая подменяет её целиком, когда собрана.
    /// </summary>
    [RelayCommand]
    private Task LoadRecommendationsAsync() => _recsTask is { IsCompleted: false } running ? running : _recsTask = LoadRecommendationsCoreAsync();

    // Один подбор за раз: повторные просьбы (кнопка, конец очереди, открытие раздела) ждут уже идущий.
    private Task? _recsTask;

    private async Task LoadRecommendationsCoreAsync()
    {
        var progressive = MoreFromArtists.Items.Count == 0;
        ForYou.Begin();
        if (progressive) MoreFromArtists.Begin();
        _recsVersion = _library.Version;
        var signature = ArtistsSignature();
        var recs = _recs = await Task.Run(() => _library.RecommendAsync(SearchTelegramArtistAsync,
            (similar, songs) => Anim.Ui(() =>
            {
                if (similar) ForYou.Append(songs);
                else if (progressive) MoreFromArtists.Append(songs.Take(2)); // сначала по две от каждого — разные исполнители
            })));
        ForYou.Done();
        if (progressive) MoreFromArtists.Append(recs.MoreFromArtists); // исполнителей мало — добрать до шести
        else if (recs.MoreFromArtists.Count > 0)
        {
            // По две от каждого исполнителя, потом остальные — до шести.
            var mixed = recs.MoreFromArtists.GroupBy(t => MusicTrack.Normalize(MusicTrack.MainArtist(t)))
                .SelectMany(g => g.Take(2)).Concat(recs.MoreFromArtists);
            await MoreFromArtists.ReplaceGraduallyAsync(mixed); // старое на экране — подменяется по одной
        }
        MoreFromArtists.Done();
        if (MoreFromArtists.Items.Count > 0)
        {
            _artistsAt = DateTime.UtcNow;
            _artistsSignature = signature;
            _library.SaveShelf("artists", signature, MoreFromArtists.Items.Select(v => v.Track));
        }
        OnPropertyChanged(nameof(HasHistory));
    }

    // Песни исполнителя для подбора: из чатов; мало (меньше трёх) — ещё у музыкальных ботов (двух лучших, без чатов с ними).
    private async Task<IReadOnlyList<MusicTrack>> SearchTelegramArtistAsync(string artist)
    {
        await WhenIdleAsync();
        var found = (await _tg.SearchMusicAsync(artist, 20, background: true)).Select(MusicTrack.FromTelegram).Where(MusicTrack.IsSong).ToList();
        var want = MusicTrack.Normalize(artist);
        if (found.Count(t => MusicTrack.Normalize(t.Artist).Contains(want)) >= 3) return found;
        try { found.AddRange(await _tg.SearchBotMusicAsync(artist, maxBots: 2, background: true)); }
        catch (Exception ex) { Logging.Write($"music recs bots: {ex.Message}"); }
        return found;
    }

    public bool HasHistory => History.Count > 0;

    // ===================================================================== Поиск

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSearch), nameof(ShowFoundPlaylists))] private string _query = "";
    [ObservableProperty] private bool _isSearchLoading;
    public bool IsSearch => Query.Trim().Length > 0;

    public MusicShelf FoundHistory { get; } = new("Вы слушали", "Те самые песни, что уже играли") { Optional = true, Max = 6 };
    public MusicShelf FoundBots { get; } = new("Музыкальные боты", "Полные песни через ботов Telegram — до пяти от каждого") { Max = 40 };
    public MusicShelf FoundTelegram { get; } = new("В Telegram", "Полные песни из ваших чатов и каналов") { Max = 20 };

    public IReadOnlyList<MusicShelf> SearchShelves => [FoundHistory, FoundBots, FoundTelegram];

    private CancellationTokenSource? _searchCts;

    partial void OnQueryChanged(string value) => _ = SearchAsync(value.Trim());

    // Подходит ли песня к запросу: все слова запроса есть в исполнителе или названии (регистр и знаки не важны).
    private static bool Matches(MusicTrack t, string q)
    {
        var text = (t.Artist + " " + t.Title).ToLowerInvariant().Replace('ё', 'е');
        return System.Text.RegularExpressions.Regex.Matches(q.ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{N}]+")
            .All(w => text.Contains(w.Value, StringComparison.Ordinal));
    }

    // Поиск — когда перестали печатать (0,7 с) и не меньше двух букв. Сверху — что уже слушали (сразу, без сети), потом
    // боты по одному (до трёх песен от каждого), потом чаты и каналы. Новый текст отменяет всё, что ещё не отправлено по старому.
    private async Task SearchAsync(string q)
    {
        _searchCts?.Cancel();
        if (q.Length < 2) { IsSearchLoading = false; return; }
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        try { await Task.Delay(900, ct); } // допечатали — тогда ищем: каждый лишний запрос к ботам приближает FLOOD_WAIT
        catch (TaskCanceledException) { return; }
        IsSearchLoading = true;
        _ = SearchPlaylistsAsync(q, ct);
        FoundHistory.Fill(_library.History.Select(e => e.Track).Where(t => Matches(t, q)));
        FoundBots.Begin();
        FoundTelegram.Begin();
        bool Shown(MusicTrack t) => FoundHistory.Items.Concat(FoundBots.Items).Any(y => MusicTrack.SameSong(t, y.Track));
        // Все боты — разом; песни идут вперемешку по кругу: по одной от каждого бота (лучшие боты — первыми), и так до пяти
        // от каждого. Повтор той же песни у другого бота не занимает место — берётся следующая его песня.
        var answers = new Dictionary<string, IReadOnlyList<MusicTrack>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.Run(() => _tg.SearchAllBotsAsync(q, maxBots: 12, ct: ct, onBot: (bot, songs) => Anim.Ui(() =>
            {
                if (ct.IsCancellationRequested) return;
                answers[bot] = songs;
                FoundBots.Sync(Interleave(answers, perBot: 5, max: FoundBots.Max));
            })));
        }
        catch (Exception ex) { Logging.Write($"music search bots: {ex.Message}"); }
        if (ct.IsCancellationRequested) return;
        FoundBots.Done();
        IReadOnlyList<MessageItem> chats;
        try { chats = await Task.Run(() => _tg.SearchMusicAsync(q, 40)); }
        catch (Exception ex) { Logging.Write($"music search tg: {ex.Message}"); chats = []; }
        if (ct.IsCancellationRequested) return;
        FoundTelegram.Fill(MusicTrack.Distinct(chats.Select(MusicTrack.FromTelegram).Where(t => MusicTrack.IsSong(t) && !Shown(t))).Take(20).OrderBy(t => HasOwnCover(t) ? 0 : 1)); // с обложкой — выше
        IsSearchLoading = false;
    }

    // Песни ботов по кругу: по одной от каждого (в порядке качества бота). Внутри одного бота повторы отсеивает сам
    // поиск у бота; между ботами убирается только точная копия — то же название, тот же исполнитель и та же длительность
    // («Тону — ELVORAZ» и «Тону — ELVORAZ, Vann Adam» остаются обе). Из «Вы слушали» — так же.
    private List<MusicTrack> Interleave(Dictionary<string, IReadOnlyList<MusicTrack>> answers, int perBot, int max)
    {
        var bots = answers.Keys.OrderBy(TelegramService.MusicBotRank).ToList();
        var next = bots.ToDictionary(b => b, _ => 0, StringComparer.OrdinalIgnoreCase);
        var taken = bots.ToDictionary(b => b, _ => 0, StringComparer.OrdinalIgnoreCase);
        var result = new List<MusicTrack>();
        static bool Exact(MusicTrack a, MusicTrack b) =>
            a.Key == b.Key || (MusicTrack.Normalize(a.Title) == MusicTrack.Normalize(b.Title)
                               && MusicTrack.Normalize(a.Artist) == MusicTrack.Normalize(b.Artist)
                               && Math.Abs(a.Duration - b.Duration) <= 1);
        bool Dup(MusicTrack t) => result.Any(r => Exact(r, t)) || FoundHistory.Items.Any(h => Exact(h.Track, t));
        for (var progress = true; progress && result.Count < max;)
        {
            progress = false;
            foreach (var bot in bots)
            {
                if (taken[bot] >= perBot || result.Count >= max) continue;
                var songs = answers[bot];
                while (next[bot] < songs.Count && Dup(songs[next[bot]])) next[bot]++;
                if (next[bot] >= songs.Count) continue;
                result.Add(songs[next[bot]++]);
                taken[bot]++;
                progress = true;
            }
        }
        return CoversFirst(result);
    }

    // Своя обложка: ссылка на картинку (боты с файлами по ссылке) или миниатюра в самом файле Telegram.
    private static bool HasOwnCover(MusicTrack t) => t.CoverUrl is not null || t.Song?.HasThumbnail == true;

    // Песни с обложкой — выше, без неё — ниже; внутри каждой части порядок (чередование ботов) не меняется.
    private static List<MusicTrack> CoversFirst(List<MusicTrack> tracks) => [.. tracks.OrderBy(t => HasOwnCover(t) ? 0 : 1)];

    // Остановить поиск, что ещё идёт (найденное остаётся на экране).
    private void StopSearch()
    {
        if (!IsSearchLoading) return;
        _searchCts?.Cancel();
        FoundTelegram.Done();
        FoundBots.Done();
        IsSearchLoading = false;
    }

    // ===================================================================== Страница «Музыка из ваших чатов»

    [ObservableProperty] private bool _isChatsPage;
    [ObservableProperty] private string _chatsQuery = "";
    /// <summary>Все песни из чатов и каналов (или найденные среди них).</summary>
    public MusicShelf ChatsPage { get; } = new("Музыка из ваших чатов", "Полные песни из чатов и каналов Telegram");
    private CancellationTokenSource? _chatsCts;

    [RelayCommand]
    private void OpenChatsPage()
    {
        IsChatsPage = true;
        if (ChatsQuery.Length > 0) ChatsQuery = ""; // поиск начнётся сам
        else _ = LoadChatsPageAsync("");
    }

    [RelayCommand]
    private void CloseChatsPage()
    {
        IsChatsPage = false;
        _chatsCts?.Cancel();
    }

    partial void OnChatsQueryChanged(string value) => _ = LoadChatsPageAsync(value.Trim());

    // Только чаты и каналы (без ботов): пусто — последние песни, иначе — поиск среди них.
    private async Task LoadChatsPageAsync(string q)
    {
        _chatsCts?.Cancel();
        var cts = _chatsCts = new CancellationTokenSource();
        var ct = cts.Token;
        if (q.Length > 0)
        {
            try { await Task.Delay(600, ct); }
            catch (TaskCanceledException) { return; }
        }
        ChatsPage.Begin();
        IReadOnlyList<MessageItem> found;
        try { found = await Task.Run(() => _tg.SearchMusicAsync(q, 100)); }
        catch (Exception ex) { Logging.Write($"music chats page: {ex.Message}"); found = []; }
        if (ct.IsCancellationRequested) return;
        ChatsPage.Fill(MusicTrack.Distinct(found.Select(MusicTrack.FromTelegram).Where(MusicTrack.IsSong)));
        SetCurrentMarks();
    }

    private void SetCurrentMarks()
    {
        if (_current is null) return;
        foreach (var v in AllVisible()) v.IsCurrent = v.Track.Key == _current.Track.Key;
    }

    [RelayCommand]
    private void ClearQuery() => Query = "";

    // ===================================================================== История

    public ObservableCollection<MusicTrackViewModel> History { get; } = [];
    private readonly Dictionary<string, MusicTrackViewModel> _historyVms = [];

    private void RefreshHistory()
    {
        var entries = _library.History;
        History.Clear();
        foreach (var e in entries)
        {
            // Та же строка (и её обложка) — не создаём заново.
            var key = e.Track.Key + "|" + e.Plays;
            if (!_historyVms.TryGetValue(key, out var vm))
                _historyVms[key] = vm = new MusicTrackViewModel(e.Track, History) { PlaysText = PlaysText(e) };
            vm.IsCurrent = vm.Track.Key == _current?.Track.Key;
            History.Add(vm);
        }
        OnPropertyChanged(nameof(HasHistory));
    }

    private static string PlaysText(MusicHistoryEntry e)
    {
        var days = (DateTime.UtcNow.Date - e.LastPlayedUtc.Date).TotalDays;
        var when = days < 1 ? "сегодня" : days < 2 ? "вчера" : Fmt.DateShort(e.LastPlayedUtc);
        return e.Plays > 1 ? $"{e.Plays} {Fmt.Plural(e.Plays, "раз", "раза", "раз")} · {when}" : when;
    }

    [RelayCommand]
    private void RemoveFromHistory(MusicTrackViewModel? vm)
    {
        if (vm is not null) _library.Remove(vm.Track.Key);
    }

    /// <summary>
    /// Песня из чата или профиля зазвучала — в историю (голосовые и музыка историй сюда не попадают).
    /// Нет своей обложки — подбирается по исполнителю и названию.
    /// </summary>
    public static void RecordTelegram(TelegramService tg, MessageItem item, ImageSource? preview)
    {
        var library = tg.MusicLibrary;
        MusicCovers.Init(library);
        var track = MusicTrack.FromTelegram(item);
        // Обложка из Deezer, найденная раньше, — она крупнее и чётче миниатюры из песни.
        var known = library.History.FirstOrDefault(e => e.Track.Key == track.Key)?.Track;
        if (known?.CoverUrl is { } url && !url.StartsWith("file:"))
        {
            track.CoverUrl = url;
            track.CoverBigUrl = known.CoverBigUrl;
            library.Record(track);
            return;
        }
        // Пока ищется в Deezer — миниатюра из песни (если есть).
        if (MusicCovers.SaveTelegramCover(track.Id, preview) is { } file) track.CoverUrl = track.CoverBigUrl = file;
        library.Record(track);
        _ = Task.Run(async () =>
        {
            if (await MusicCatalog.FindCoverAsync(track.Artist, track.Title) is not { } cover) return;
            library.SetCover(track.Key, cover.Small, cover.Big);
            Anim.Ui(() => Instance?.CoverFound(track.Key));
        });
    }

    // Песня Telegram без обложки по ссылке: сначала обложка из Deezer (по исполнителю и названию — крупная и чёткая),
    // не нашлась — миниатюра, вшитая в саму песню. Найденная сохраняется (на диск и в историю), второй раз не ищется.
    private readonly Dictionary<string, Task<ImageSource?>> _coverSearches = [];

    private Task<ImageSource?> FindTelegramCoverAsync(MusicTrack t)
    {
        if (!_coverSearches.TryGetValue(t.Key, out var task)) _coverSearches[t.Key] = task = SearchCoverAsync(t);
        return task;
    }

    private static readonly SemaphoreSlim DeezerGate = new(2);

    private async Task<ImageSource?> SearchCoverAsync(MusicTrack t)
    {
        // Deezer — не больше двух поисков разом: аккуратно, без отказов за частые запросы.
        await DeezerGate.WaitAsync();
        (string Small, string Big)? found;
        try { found = await Task.Run(() => MusicCatalog.FindCoverAsync(t.Artist, t.Title)); }
        finally { DeezerGate.Release(); }
        if (found is { } f)
        {
            t.CoverUrl = f.Small;
            t.CoverBigUrl = f.Big;
            _library.SetCover(t.Key, f.Small, f.Big);
            if (await MusicCovers.LoadAsync(f.Small, 220) is { } img) return img;
        }
        if (t.Song is { HasThumbnail: true } song)
        {
            try
            {
                await WhenIdleAsync();
                var bytes = await _tg.Media.GetMessagePreviewAsync(song);
                if (MusicCovers.SaveTelegramCoverBytes(t.Id, bytes) is { } file)
                {
                    t.CoverUrl = t.CoverBigUrl = file;
                    _library.SetCover(t.Key, file, file);
                    return await MusicCovers.LoadAsync(file, 220);
                }
            }
            catch (Exception ex) { Logging.Write($"music cover tg: {ex.Message}"); }
        }
        return null;
    }

    /// <summary>Последний созданный раздел — для обложек, найденных позже.</summary>
    public static MusicViewModel? Instance { get; set; }

    private void CoverFound(string key)
    {
        foreach (var vm in History.Where(v => v.Track.Key == key)) vm.ReloadCover();
    }

    // ===================================================================== Воспроизведение

    private List<MusicTrackViewModel> _queue = [];
    private int _index = -1;
    private MusicTrackViewModel? _current;
    private MusicTrackViewModel? _requested;
    // Что уже звучало за этот запуск — подбор его не повторяет (та же песня из другого канала — тоже).
    private readonly List<MusicTrack> _played = [];
    private int _failures;

    /// <summary>Откуда играет: «Подбор для вас», «Музыка из ваших чатов»…</summary>
    [ObservableProperty] private string? _queueTitle;
    /// <summary>Пояснение к треку: «Из Telegram», «Через @бот».</summary>
    [ObservableProperty] private string? _playingNote;
    [ObservableProperty] private string? _nextUpText;

    public bool HasNowPlaying => MusicNow.Instance.IsActive;

    [RelayCommand]
    private Task Play(MusicTrackViewModel? vm)
    {
        if (vm is null) return Task.CompletedTask;
        if (vm == _current && MusicPlayer.Instance.Key == vm.Track.Key)
        {
            MusicPlayer.Instance.Toggle(vm.Track.Key, "");
            return Task.CompletedTask;
        }
        StopSearch(); // выбрали песню — оставшихся ботов не спрашиваем: соединение — ей
        var list = vm.List?.ToList() ?? [vm];
        // Полка успела обновиться (подбор пересобран), а трек нажат в очереди — играем дальше по очереди.
        if (!list.Contains(vm)) list = _queue.Contains(vm) ? _queue : [vm];
        else if (ReferenceEquals(vm.List, MoreFromArtists.Items))
            list = [vm, .. list.Where(v => v != vm).OrderBy(_ => Random.Shared.Next()).Take(1)]; // выбранная и ещё одна, дальше — подбор
        _queue = list;
        _index = Math.Max(0, list.IndexOf(vm));
        QueueTitle = AllShelves.FirstOrDefault(s => ReferenceEquals(s.Items, vm.List))?.Title
            ?? (ReferenceEquals(vm.List, History) ? "Вы слушали" : null);
        if (ReferenceEquals(vm.List, PlaylistPage.Items)) QueueTitle = OpenedPlaylist?.Title;
        return PlayCurrentAsync();
    }

    private bool _favoritesLoaded;

    // При запуске связь ещё поднимается — пробуем несколько раз (и ещё раз при открытии раздела).
    private async Task LoadFavoritesAsync()
    {
        for (var attempt = 0; attempt < 6 && !_favoritesLoaded; attempt++)
        {
            if (attempt > 0) await Task.Delay(5000);
            await LoadFavoritesOnceAsync();
        }
    }

    private async Task LoadFavoritesOnceAsync()
    {
        try
        {
            var liked = await Task.Run(_tg.GetLikedSongsAsync);
            _favoritesLoaded = true;
            Favorites.Fill(liked);
            // Играющая уже песня — сердечко по свежему списку.
            if (MusicNow.Instance.Song is { } song) MusicNow.Instance.IsLiked = _tg.IsLikedSong(song.MediaId);
        }
        catch (Exception ex)
        {
            Logging.Write($"music favorites: {ex.Message}");
            Favorites.Done();
        }
    }

    // Сердечко: в профиль (наверх «Любимых») или из профиля.
    private async Task SetLikedAsync(MessageItem song, bool liked)
    {
        await _tg.SetSongLikedAsync(song, liked);
        Anim.Ui(() =>
        {
            var track = MusicTrack.FromTelegram(song);
            var existing = Favorites.Items.FirstOrDefault(v => v.Track.Song?.MediaId == song.MediaId || MusicTrack.SameSong(v.Track, track));
            if (liked && existing is null) Favorites.Items.Insert(0, new MusicTrackViewModel(track, Favorites.Items));
            else if (!liked && existing is not null) Favorites.Items.Remove(existing);
            Favorites.Done();
        });
    }

    /// <summary>«Вы слушали» как плейлист: по порядку, а когда кончится — подбор, как обычно.</summary>
    [RelayCommand]
    private Task PlayHistory() => History.Count > 0 ? Play(History[0]) : Task.CompletedTask;

    /// <summary>Вся полка по порядку.</summary>
    [RelayCommand]
    private Task PlayShelf(MusicShelf? shelf) =>
        shelf is { Items.Count: > 0 } ? Play(shelf.Shuffled ? shelf.Items[Random.Shared.Next(shelf.Items.Count)] : shelf.Items[0]) : Task.CompletedTask;

    [RelayCommand]
    private Task Next() => NextAsync(auto: false);

    [RelayCommand]
    private Task Previous()
    {
        // Как в плеерах: дальше 3 секунд — в начало трека, иначе — предыдущий.
        if (MusicPlayer.Instance.Position.TotalSeconds > 3 || _index <= 0)
        {
            MusicNow.Instance.Seek(0);
            return Task.CompletedTask;
        }
        _index--;
        return PlayCurrentAsync();
    }

    private async Task NextAsync(bool auto)
    {
        if (_index + 1 < _queue.Count)
        {
            _index++;
            await PlayCurrentAsync();
            return;
        }
        // Список кончился — дальше подбор по прослушанному (только то, что ещё не звучало).
        var recs = await AutoQueueAsync();
        if (recs.Count == 0)
        {
            if (!auto) _toast("Подбор пока пуст — послушайте что-нибудь", false);
            return;
        }
        _queue = recs;
        _index = 0;
        QueueTitle = "Подбор для вас";
        await PlayCurrentAsync();
    }

    private async Task<List<MusicTrackViewModel>> AutoQueueAsync()
    {
        // Подбор ещё не считали в этот запуск (полки из кэша) — не ждём его: берём то, что уже на полках.
        var r = _recs ?? new Recommendations([], MoreFromArtists.Items.Select(v => v.Track).ToList(), false);
        if (_recs is null) _ = LoadRecommendationsAsync();
        await Task.CompletedTask;
        // Похожее и ещё от любимых исполнителей — вперемешку; подбора нет (истории ещё нет) — музыка из чатов.
        var mix = WeightedByGenre(NextForYou.Items.Select(v => v.Track)); // сначала «Для вас»
        for (var i = 0; i < Math.Max(r.MoreFromArtists.Count, r.Similar.Count); i++)
        {
            if (i < r.Similar.Count) mix.Add(r.Similar[i]);
            if (i < r.MoreFromArtists.Count) mix.Add(r.MoreFromArtists[i]);
        }
        if (mix.Count == 0) mix.AddRange(InChats.Items.Select(v => v.Track));
        var list = new List<MusicTrackViewModel>();
        // Уже слушали раньше — можно, но именно тот файл, что играл; в этот запуск уже звучавшее — нет.
        foreach (var t in mix.Select(t => _library.Played(t) ?? t)
                     .Where(t => !_failed.Contains(t.Key) && !_played.Any(p => MusicTrack.SameSong(p, t))).Take(40))
            if (!list.Any(v => MusicTrack.SameSong(v.Track, t))) list.Add(new MusicTrackViewModel(t, list));
        return list;
    }

    // Порядок «Для вас» для очереди: следующей с большей вероятностью встаёт песня того жанра, которого среди семи последних
    // больше (5 из 7 — рэп → рэп следующим с шансом 5/7 от остального).
    private List<MusicTrack> WeightedByGenre(IEnumerable<MusicTrack> tracks)
    {
        var pool = tracks.ToList();
        var order = new List<MusicTrack>();
        while (pool.Count > 0)
        {
            var weights = pool.Select(t => _genreOf.TryGetValue(t.Key, out var g) && _genreShare.TryGetValue(g, out var s) ? s : 1.0 / 7).ToList();
            var pick = Random.Shared.NextDouble() * weights.Sum();
            var i = 0;
            while (i < pool.Count - 1 && (pick -= weights[i]) > 0) i++;
            order.Add(pool[i]);
            pool.RemoveAt(i);
        }
        return order;
    }

    // Пока трек запускается, фоновое (обложки из Telegram, поиск для подбора) ждёт — вся скорость соединения ему.
    private static long _startingUntil;

    private static async Task WhenIdleAsync()
    {
        while (Environment.TickCount64 < Volatile.Read(ref _startingUntil)) await Task.Delay(150);
    }

    private async Task PlayCurrentAsync()
    {
        if (_index < 0 || _index >= _queue.Count) return;
        var vm = _queue[_index];
        var t = vm.Track;
        _requested = vm;
        SetCurrent(vm);
        NextUpText = _index + 1 < _queue.Count ? $"Далее: {_queue[_index + 1].Artist} — {_queue[_index + 1].Title}" : "Далее: подбор для вас";
        PlayingNote = null;
        MusicNow.Instance.ShowPending(t.Key, t.Title, vm.Artist, BestCover(vm), vm.CoverBrush, t.Song); // сразу на экране
        Volatile.Write(ref _startingUntil, Environment.TickCount64 + 8000);
        try
        {
            switch (t.Source)
            {
                case MusicSource.Telegram:
                    await PlayTelegramAsync(t, t.Song ??= await _tg.GetSongAsync(t.PeerKey, t.MessageId, long.Parse(t.Id))
                        ?? throw new IOException("Песня больше недоступна."));
                    break;
                case MusicSource.Bot:
                    // Из истории (после перезапуска) — у того же бота заново: его ссылки на файлы временные.
                    if (!t.Live)
                    {
                        var again = await _tg.RefindBotSongAsync(t) ?? throw new IOException($"@{t.BotName} больше не находит эту песню.");
                        t.Song = again.Song;
                        t.StreamUrl = again.StreamUrl;
                        t.Live = true;
                        if (_requested != vm) return;
                    }
                    if (t.Song is { } botSong) await PlayTelegramAsync(t, botSong);
                    else await Task.Run(() => MusicPlayer.Instance.Play(t.Key, t.StreamUrl ?? throw new IOException("У песни нет файла.")));
                    PlayingNote = $"Через @{t.BotName}";
                    break;
                default:
                    // Трек каталога (плейлист, история): сам файл только из Telegram — чаты и каналы, потом боты. Фрагментов не играем.
                    var full = await FindFullAsync(t) ?? throw new IOException("Песня не нашлась в Telegram.");
                    if (_requested != vm) return;
                    t.Song ??= full.Song;
                    if (full.Song is { } fullFile) await PlayTelegramAsync(t, fullFile);
                    else await Task.Run(() => MusicPlayer.Instance.Play(t.Key, full.StreamUrl ?? throw new IOException("У песни нет файла.")));
                    PlayingNote = full.Source == MusicSource.Bot ? $"Через @{full.BotName}" : "Из Telegram";
                    break;
            }
            if (_requested != vm) return;
            Volatile.Write(ref _startingUntil, Environment.TickCount64 + 1500); // ещё немного — на первые секунды звука
            MusicNow.Instance.Start(t.Key, t.Title, vm.Artist, BestCover(vm), vm.CoverBrush, t.Song);
            // В «Вы слушали» — только когда песня реально зазвучала (WatchStartAsync): раньше туда попадала каждая
            // пролистанная и даже незагрузившаяся.
            _ = ShowBigCoverAsync(vm);
            _ = WatchStartAsync(vm);
            PrefetchNext();
        }
        catch (Exception ex)
        {
            if (_requested != vm) return;
            MusicNow.Instance.CancelPending(t.Key);
            Volatile.Write(ref _startingUntil, 0);
            Logging.Write($"music play {t.Key}: {ex.Message}");
            // Не играет — убрать из списка и дальше (но не крутимся бесконечно на сломанном списке).
            DropFailed(vm);
            if (++_failures <= 8 && _index < _queue.Count)
            {
                await PlayCurrentAsync();
                return;
            }
            _failures = 0;
            _toast(TgErrors.Describe(ex), true);
        }
    }

    // Крупная обложка в плеер — когда найдётся (плеер её не ждёт). Раньше, если обложку находили уже после запуска
    // (песня из Telegram без своей обложки), в плеере так и оставался градиент.
    // Песня не зазвучала за 15 с (файл не качается: недоступный сервер Telegram, ссылка бота умерла) — в лог и дальше,
    // а не бесконечная «загрузка».
    // Песня не загрузилась: убрать её из очереди и с полки (или из результатов поиска) — чтобы не нажимать на неё снова,
    // и в подбор она больше не попадёт. Индекс очереди теперь указывает на следующую.
    private readonly HashSet<string> _failed = [];

    private void DropFailed(MusicTrackViewModel vm)
    {
        _failed.Add(vm.Track.Key);
        var i = _queue.IndexOf(vm);
        if (i >= 0)
        {
            _queue.RemoveAt(i);
            if (i < _index) _index--;
        }
        if (vm.List is ObservableCollection<MusicTrackViewModel> list && !ReferenceEquals(list, History)) list.Remove(vm);
    }

    private async Task WatchStartAsync(MusicTrackViewModel vm)
    {
        var key = vm.Track.Key;
        for (var i = 0; i < 50; i++) // 25 с: связь с файловым сервером иногда замирает на десятки секунд
        {
            await Task.Delay(500);
            if (MusicPlayer.Instance.Key != key || _current != vm) return; // переключили или остановили
            if (MusicPlayer.Instance.Position.TotalSeconds > 0)
            {
                // Зазвучала — теперь в историю и в «уже звучало».
                _failures = 0;
                _played.Add(vm.Track);
                _library.Record(vm.Track);
                return;
            }
            if (!MusicPlayer.Instance.IsPlaying && !MusicPlayer.Instance.IsBuffering) return; // поставили на паузу
        }
        Logging.Write($"music: {key} не зазвучала за 25 с — следующая");
        _toast("Песня не загрузилась — включаю следующую", true);
        MusicPlayer.Instance.Stop();
        DropFailed(vm);
        if (_index < _queue.Count) await PlayCurrentAsync();
        else _ = NextAsync(auto: true);
    }

    // Обложка, которая уже есть в памяти: крупная, плиточная или у такой же плитки на странице — плеер не мигает градиентом
    // и не грузит её заново при переключении.
    private ImageSource? BestCover(MusicTrackViewModel vm)
    {
        var t = vm.Track;
        return MusicCovers.Cached(t.CoverBigUrl ?? t.CoverUrl, 640)
            ?? MusicCovers.Cached(t.CoverUrl ?? t.CoverBigUrl, 220)
            ?? vm.LoadedCover
            ?? AllVisible().FirstOrDefault(v => v.Track.Key == t.Key && v.LoadedCover is not null)?.LoadedCover;
    }

    private async Task ShowBigCoverAsync(MusicTrackViewModel vm)
    {
        var t = vm.Track;
        try
        {
            if (t.CoverUrl is null && t.CoverBigUrl is null && t.Source is MusicSource.Telegram or MusicSource.Bot) await FindTelegramCoverAsync(t);
            var img = await MusicCovers.LoadAsync(t.CoverBigUrl ?? t.CoverUrl, 640, t.CoverMirrors) ?? vm.LoadedCover;
            if (img is not null && MusicPlayer.Instance.Key == t.Key && !ReferenceEquals(MusicNow.Instance.Cover, img)) MusicNow.Instance.Cover = img;
        }
        catch (Exception ex) { Logging.Write($"music big cover: {ex.Message}"); }
    }

    // Пока играет песня — тихо подготовить то, что могут включить следующим, чтобы переход был почти мгновенным:
    // следующую в очереди (а на последней — первую из подбора) — около 1 МБ начала, плитки «Для вас» — понемногу
    // (256 КБ), чтобы и клик по ним стартовал сразу. Песни из сохранённых полок заранее находятся в Telegram заново.
    private void PrefetchNext()
    {
        var targets = new List<(MusicTrack Track, long Bytes)>();
        var current = _current?.Track;
        if (_index + 1 < _queue.Count) targets.Add((_queue[_index + 1].Track, 1024 * 1024));
        else if (NextForYou.Items.Select(v => v.Track).FirstOrDefault(t => t != current && !_library.WasPlayed(t)) is { } auto)
            targets.Add((auto, 1024 * 1024));
        foreach (var t in NextForYou.Items.Select(v => v.Track))
            if (t != current && targets.All(x => x.Track != t)) targets.Add((t, 256 * 1024));
        if (targets.Count == 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // текущая уже разогналась — не отнимаем у неё скорость на старте
            foreach (var (t, bytes) in targets)
            {
                await WhenIdleAsync();
                try
                {
                    await PrepareAsync(t);
                    if (bytes > 256 * 1024) _ = MusicCovers.LoadAsync(t.CoverBigUrl ?? t.CoverUrl, 640, t.CoverMirrors); // и крупную обложку — заранее
                    if (t.Song is { } song) _tg.Media.Prefetch(song, bytes);
                }
                catch (Exception ex) { Logging.Write($"music prefetch: {ex.Message}"); }
                await Task.Delay(300); // по одной — не забивать соединение разом
            }
        });
    }

    // Песня готова к запуску: из Telegram — сообщение найдено заново (после перезапуска ссылки на файл нет),
    // от бота — свежая ссылка у того же бота.
    private async Task PrepareAsync(MusicTrack t)
    {
        switch (t.Source)
        {
            case MusicSource.Telegram when t.Song is null:
                t.Song = await _tg.GetSongAsync(t.PeerKey, t.MessageId, long.Parse(t.Id));
                break;
            case MusicSource.Bot when !t.Live:
                if (await _tg.RefindBotSongAsync(t, background: true) is { } again)
                {
                    t.Song = again.Song;
                    t.StreamUrl = again.StreamUrl;
                    t.Live = true;
                }
                break;
        }
    }

    private async Task PlayTelegramAsync(MusicTrack t, MessageItem song)
    {
        var stream = await _tg.Media.OpenMediaStreamAsync(song) ?? throw new IOException("Не удалось загрузить песню.");
        if (_requested?.Track != t) { stream.Dispose(); return; }
        await Task.Run(() => MusicPlayer.Instance.PlayStream(t.Key, stream));
    }

    // Полная версия трека каталога: чаты и каналы (не дольше 5 с), потом все боты разом — первый ответ с той же песней (не дольше 15 с).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MusicTrack> _fullOf = new();

    private async Task<MusicTrack?> FindFullAsync(MusicTrack t)
    {
        if (_fullOf.TryGetValue(t.Key, out var known)) return known;
        var query = $"{MusicTrack.MainArtist(t)} {t.Title}".Trim();
        MusicTrack? found = null;
        try
        {
            var chats = _tg.SearchMusicAsync(query, 10);
            if (await Task.WhenAny(chats, Task.Delay(5000)) == chats)
                found = (await chats).Select(MusicTrack.FromTelegram).Where(MusicTrack.IsSong).FirstOrDefault(c => c.Duration >= 60 && MusicTrack.SameSong(c, t));
            if (found is null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var answer = new TaskCompletionSource<MusicTrack?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var bots = _tg.SearchAllBotsAsync(query, maxBots: 12, ct: stop.Token, onBot: (_, songs) =>
                {
                    if (songs.FirstOrDefault(s => s.Duration is 0 or >= 60 && MusicTrack.SameSong(s, t)) is { } song) answer.TrySetResult(song);
                });
                await Task.WhenAny(answer.Task, bots, Task.Delay(15_000));
                stop.Cancel();
                if (answer.Task.IsCompletedSuccessfully) found = answer.Task.Result;
            }
        }
        catch (Exception ex) { Logging.Write($"music full song: {ex.Message}"); }
        if (found is not null) _fullOf[t.Key] = found;
        return found;
    }


    private void SetCurrent(MusicTrackViewModel vm)
    {
        if (_current is not null) _current.IsCurrent = false;
        foreach (var v in AllVisible().Where(v => v.IsCurrent)) v.IsCurrent = false;
        _current = vm;
        foreach (var v in AllVisible().Where(v => v.Track.Key == vm.Track.Key)) v.IsCurrent = true;
        vm.IsCurrent = true;
    }

    private IEnumerable<MusicTrackViewModel> AllVisible() =>
        AllShelves.SelectMany(s => s.Items).Concat(History);
}
