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
    public string DurationText => Track.Duration > 0 && !Track.IsPreview ? Fmt.Duration(Track.Duration) : "";
    public string SourceText => Track.Source switch
    {
        MusicSource.Telegram => "Telegram",
        MusicSource.Audius => "Audius",
        MusicSource.Bot => "@" + Track.BotName,
        _ => "Deezer",
    };
    public Brush CoverBrush { get; } = MessageViewModel.GradientFor(track.Title, track.Artist);
    /// <summary>Плашка источника — только не у песен Telegram (их теперь большинство).</summary>
    public bool ShowSource => Track.Source != MusicSource.Telegram;
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

    public void Fill(IEnumerable<MusicTrack> tracks)
    {
        Items.Clear();
        Append(tracks);
        Done();
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
        var target = MusicTrack.Distinct(tracks).Take(Max).ToList();
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
        // Послушали что-то (и в чатах тоже) — история и «Дальше для вас» обновляются.
        _library.Changed += () => Anim.Ui(() => { RefreshHistory(); if (_loaded) RequestNext(); });
        MusicPlayer.Instance.Ended += key => Anim.Ui(() => { if (key == _current?.Track.Key) _ = NextAsync(auto: true); });
        MusicNow.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MusicNow.Title)) OnPropertyChanged(nameof(HasNowPlaying));
        };
    }

    // ===================================================================== Главная

    public MusicShelf ForYou { get; } = new("Для вас", "Похожие исполнители · полные песни из Telegram");
    public MusicShelf MoreFromArtists { get; } = new("Ещё от ваших исполнителей", "Полные песни из Telegram") { Max = 6 };
    public MusicShelf InChats { get; } = new("Музыка из ваших чатов", "Полные песни из чатов и каналов Telegram");

    public IReadOnlyList<MusicShelf> Shelves => [MoreFromArtists, InChats]; // «Для вас» не показываем — его песни играют в подборе после очереди

    /// <summary>«Дальше для вас»: 4 песни сеткой по трём последним прослушанным — перестраивается после каждой песни.</summary>
    public MusicShelf NextForYou { get; } = new("Дальше для вас", "По трём последним песням — больше всего по самой последней");

    private IEnumerable<MusicShelf> AllShelves => [NextForYou, .. Shelves, FoundTelegram, FoundBots];

    private CancellationTokenSource? _nextCts;
    private string? _nextSeeds;
    private Task _nextTask = Task.CompletedTask;
    // Плитки, уже заменённые в этом проходе (их не трогаем, пока не заменим остальные).
    private readonly HashSet<MusicTrackViewModel> _nextFresh = [];

    private void RequestNext(bool soon = false) => _nextTask = BuildNextAsync(soon);

    /// <summary>
    /// «Дальше для вас». Для каждой из трёх последних песен — «радио» её исполнителя в Deezer (треки в похожем
    /// звучании и настроении); по самой последней — две песни, по двум предыдущим — по одной. Исполнители, которых только
    /// что слушали, и уже прослушанное не берутся. У каждой находится полная версия — в чатах или у музыкальных ботов.
    /// Если полка уже на экране, она не очищается: через 10 секунд (вдруг песню сразу переключат) плитки заменяются
    /// по одной, по мере нахождения — сначала уже прослушанные, играющая сейчас не трогается.
    /// </summary>
    /// <param name="soon">Без паузы в 10 с (раздел только что открыли, а полка с прошлого раза устарела).</param>
    private async Task BuildNextAsync(bool soon = false)
    {
        var recent = _library.History.Take(3).Select(e => e.Track).ToList();
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
            var radios = await Task.WhenAll(recent.Select(t => Task.Run(async () =>
            {
                var name = MusicTrack.MainArtist(t);
                if (name.Length == 0 || await MusicCatalog.DeezerFindArtistAsync(name, ct) is not { } artist) return new List<MusicTrack>();
                var rnd = new Random();
                return (await MusicCatalog.DeezerArtistRadioAsync(artist.Id, 25, ct))
                    .Where(c => !recentArtists.Contains(MusicTrack.Normalize(MusicTrack.MainArtist(c))))
                    .OrderBy(_ => rnd.Next()).ToList();
            }, ct)));
            // По очереди: две по последней, по одной по предыдущим, и так по кругу — пока не наберётся 4.
            var order = new List<MusicTrack>();
            int[] weights = [2, 1, 1];
            for (var round = 0; round < 8; round++)
                for (var s = 0; s < radios.Length; s++)
                    for (var k = 0; k < weights[s]; k++)
                        if (round * weights[s] + k < radios[s].Count) order.Add(radios[s][round * weights[s] + k]);
            var found = new List<MusicTrack>();
            var tried = 0;
            foreach (var candidate in MusicTrack.Distinct(order))
            {
                if (ct.IsCancellationRequested || found.Count >= 4 || ++tried > 14) break;
                if (_library.WasPlayed(candidate) || found.Any(f => MusicTrack.SameSong(f, candidate))) continue;
                if (!replace && NextForYou.Items.Any(x => MusicTrack.SameSong(x.Track, candidate))) continue;
                await WhenIdleAsync();
                if (await FullSongAsync(candidate) is not { } full || ct.IsCancellationRequested) continue;
                // Обложка — из Deezer (крупная и чёткая), если у найденной своей нет.
                full.CoverUrl ??= candidate.CoverUrl;
                full.CoverBigUrl ??= candidate.CoverBigUrl;
                found.Add(full);
                if (replace) await ReplaceNextSlotAsync(full, ct);
                else NextForYou.Append([full]);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Logging.Write($"music next: {ex.Message}"); }
        if (ct.IsCancellationRequested) return;
        NextForYou.Done();
        _library.SaveShelf("next", seeds, NextForYou.Items.Select(v => v.Track));
        PrefetchNext(); // новые плитки — заранее подготовить
    }

    // Заменить одну плитку «Дальше для вас»: сначала уже прослушанную, потом по порядку слева; играющую сейчас
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
        var slot = free.FirstOrDefault(i => _library.WasPlayed(items[i].Track), free[0]);
        items[slot] = vm;
    }

    // Полная версия трека каталога: в чатах и каналах; нет — у двух лучших музыкальных ботов.
    private async Task<MusicTrack?> FullSongAsync(MusicTrack t)
    {
        var query = $"{MusicTrack.MainArtist(t)} {t.Title}".Trim();
        try
        {
            var chats = (await _tg.SearchMusicAsync(query, 10)).Select(MusicTrack.FromTelegram);
            if (chats.FirstOrDefault(c => c.Duration >= 60 && MusicTrack.SameSong(c, t)) is { } inChats) return inChats;
            var bots = await _tg.SearchBotMusicAsync(query, maxBots: 2);
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
    /// Раздел открыли. Сразу — полки с прошлого раза (без ожидания). Первым — «Дальше для вас»; остальное
    /// обновляется после него и только если устарело.
    /// </summary>
    public void Activate()
    {
        RefreshHistory();
        if (!_loaded)
        {
            _loaded = true;
            ShowCachedShelves();
            RequestNext(soon: true);
        }
        else RequestNext();
        _ = LoadHomeAsync();
    }

    private void ShowCachedShelves()
    {
        if (_library.LoadShelf("next") is { Tracks.Count: > 0 } next)
        {
            NextForYou.Fill(next.Tracks.Where(t => !_library.WasPlayed(t)));
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
            // «Дальше для вас» — в первую очередь: остальное ждёт его (но не дольше 25 с).
            await Task.WhenAny(_nextTask, Task.Delay(25_000));
            if (InChats.Items.Count == 0 || DateTime.UtcNow - _chatsAt > ChatsFresh)
            {
                var chats = await Task.Run(async () =>
                {
                    try { return await _tg.SearchMusicAsync("", 30); }
                    catch (Exception ex) { Logging.Write($"music chats: {ex.Message}"); return []; }
                });
                if (chats.Count > 0 || InChats.Items.Count == 0)
                {
                    var fresh = MusicTrack.Distinct(chats.Select(MusicTrack.FromTelegram));
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
        var found = (await _tg.SearchMusicAsync(artist, 20)).Select(MusicTrack.FromTelegram).ToList();
        var want = MusicTrack.Normalize(artist);
        if (found.Count(t => MusicTrack.Normalize(t.Artist).Contains(want)) >= 3) return found;
        try { found.AddRange(await _tg.SearchBotMusicAsync(artist, maxBots: 2)); }
        catch (Exception ex) { Logging.Write($"music recs bots: {ex.Message}"); }
        return found;
    }

    public bool HasHistory => History.Count > 0;

    // ===================================================================== Поиск

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSearch))] private string _query = "";
    [ObservableProperty] private bool _isSearchLoading;
    public bool IsSearch => Query.Trim().Length > 0;

    public MusicShelf FoundTelegram { get; } = new("В Telegram", "Полные песни из ваших чатов и каналов");
    public MusicShelf FoundBots { get; } = new("Музыкальные боты", "Полные песни через ботов Telegram");

    public IReadOnlyList<MusicShelf> SearchShelves => [FoundTelegram, FoundBots];

    private CancellationTokenSource? _searchCts;

    partial void OnQueryChanged(string value) => _ = SearchAsync(value.Trim());

    private async Task SearchAsync(string q)
    {
        _searchCts?.Cancel();
        if (q.Length == 0) { IsSearchLoading = false; return; }
        var cts = _searchCts = new CancellationTokenSource();
        try { await Task.Delay(400, cts.Token); } // печатают — ждём паузу
        catch (TaskCanceledException) { return; }
        IsSearchLoading = true;
        FoundTelegram.Begin();
        FoundBots.Begin();
        // Чаты и боты — независимо, каждый ответ сразу на странице (песни, которые уже есть в чатах, у ботов не повторяем).
        bool InChatsAlready(MusicTrack t) => FoundTelegram.Items.Any(y => MusicTrack.SameSong(t, y.Track));
        var tg = Task.Run(async () =>
        {
            try { return await _tg.SearchMusicAsync(q, 30); }
            catch (Exception ex) { Logging.Write($"music search tg: {ex.Message}"); return []; }
        });
        var bots = Task.Run(async () =>
        {
            try
            {
                return await _tg.SearchBotMusicAsync(q, partial: songs => Anim.Ui(() =>
                {
                    if (!cts.IsCancellationRequested) FoundBots.Append(songs, InChatsAlready);
                }));
            }
            catch (Exception ex) { Logging.Write($"music search bots: {ex.Message}"); return []; }
        });
        var t = await tg;
        if (cts.IsCancellationRequested) return;
        FoundTelegram.Fill(MusicTrack.Distinct(t.Select(MusicTrack.FromTelegram)));
        // Боты ответили раньше чатов — убрать у них то, что нашлось в чатах.
        foreach (var dup in FoundBots.Items.Where(x => InChatsAlready(x.Track)).ToList()) FoundBots.Items.Remove(dup);
        await bots;
        if (cts.IsCancellationRequested) return;
        FoundBots.Done();
        IsSearchLoading = false;
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
    // Трек Deezer → его полная версия в Telegram (null — не нашлась).
    private readonly Dictionary<string, MessageItem?> _fullVersions = [];

    /// <summary>Откуда играет: «Подбор для вас», «Музыка из ваших чатов»…</summary>
    [ObservableProperty] private string? _queueTitle;
    /// <summary>Пояснение к треку: «Полная версия из Telegram» / «Фрагмент 30 с».</summary>
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
        var list = vm.List?.ToList() ?? [vm];
        // Полка успела обновиться (подбор пересобран), а трек нажат в очереди — играем дальше по очереди.
        if (!list.Contains(vm)) list = _queue.Contains(vm) ? _queue : [vm];
        _queue = list;
        _index = Math.Max(0, list.IndexOf(vm));
        QueueTitle = AllShelves.FirstOrDefault(s => ReferenceEquals(s.Items, vm.List))?.Title
            ?? (ReferenceEquals(vm.List, History) ? "Вы слушали" : null);
        return PlayCurrentAsync();
    }

    /// <summary>Вся полка по порядку.</summary>
    [RelayCommand]
    private Task PlayShelf(MusicShelf? shelf) =>
        shelf is { Items.Count: > 0 } ? Play(shelf.Items[0]) : Task.CompletedTask;

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
        var mix = NextForYou.Items.Select(v => v.Track).ToList(); // сначала «Дальше для вас»
        for (var i = 0; i < Math.Max(r.MoreFromArtists.Count, r.Similar.Count); i++)
        {
            if (i < r.Similar.Count) mix.Add(r.Similar[i]);
            if (i < r.MoreFromArtists.Count) mix.Add(r.MoreFromArtists[i]);
        }
        if (mix.Count == 0) mix.AddRange(InChats.Items.Select(v => v.Track));
        var list = new List<MusicTrackViewModel>();
        foreach (var t in mix.Where(t => !_played.Any(p => MusicTrack.SameSong(p, t)) && !_library.WasPlayed(t)).Take(40)) list.Add(new MusicTrackViewModel(t, list));
        return list;
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
        MusicNow.Instance.ShowPending(t.Key, t.Title, vm.Artist, BestCover(vm), vm.CoverBrush); // сразу на экране
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
                    // Из истории (Deezer, Audius): полная версия в Telegram, если есть в чатах и каналах; иначе у Deezer — фрагмент.
                    if (await FullVersionAsync(t) is { } full)
                    {
                        if (_requested != vm) return;
                        await PlayTelegramAsync(t, full);
                        PlayingNote = "Полная версия из Telegram";
                    }
                    else
                    {
                        if (t.Source != MusicSource.Deezer) throw new IOException("Песня не нашлась в Telegram.");
                        var preview = await MusicCatalog.DeezerPreviewAsync(t.Id) ?? throw new IOException("Трек недоступен.");
                        if (_requested != vm) return;
                        await Task.Run(() => MusicPlayer.Instance.Play(t.Key, preview));
                        PlayingNote = "Фрагмент 30 с · полной версии в Telegram не нашлось";
                    }
                    break;
            }
            if (_requested != vm) return;
            _failures = 0;
            Volatile.Write(ref _startingUntil, Environment.TickCount64 + 1500); // ещё немного — на первые секунды звука
            _played.Add(t);
            MusicNow.Instance.Start(t.Key, t.Title, vm.Artist, BestCover(vm), vm.CoverBrush);
            _library.Record(t);
            _ = ShowBigCoverAsync(vm);
            PrefetchNext();
        }
        catch (Exception ex)
        {
            if (_requested != vm) return;
            MusicNow.Instance.CancelPending(t.Key);
            Volatile.Write(ref _startingUntil, 0);
            Logging.Write($"music play {t.Key}: {ex.Message}");
            // Не играет — следующий (но не крутимся бесконечно на сломанном списке).
            if (++_failures <= 3 && _index + 1 < _queue.Count)
            {
                _index++;
                await PlayCurrentAsync();
                return;
            }
            _failures = 0;
            _toast(TgErrors.Describe(ex), true);
        }
    }

    // Крупная обложка в плеер — когда найдётся (плеер её не ждёт). Раньше, если обложку находили уже после запуска
    // (песня из Telegram без своей обложки), в плеере так и оставался градиент.
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
    // следующую в очереди (а на последней — первую из подбора) — около 1 МБ начала, плитки «Дальше для вас» — понемногу
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
                if (await _tg.RefindBotSongAsync(t) is { } again)
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

    private async Task<MessageItem?> FullVersionAsync(MusicTrack t)
    {
        if (_fullVersions.TryGetValue(t.Id, out var known)) return known;
        MessageItem? found = null;
        try
        {
            var search = _tg.FindFullSongAsync(t.Artist, t.Title);
            // Поиск не дольше 5 секунд — иначе сразу фрагмент.
            if (await Task.WhenAny(search, Task.Delay(5000)) == search) found = await search;
        }
        catch (Exception ex) { Logging.Write($"music full: {ex.Message}"); }
        _fullVersions[t.Id] = found;
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
