using TgCheck.Core.Models;
using TgCheck.Core.Music;
using TL;

namespace TgCheck.Core.Services;

// Раздел «Музыка»: песни из Telegram (поиск по музыке во всех чатах и каналах, как вкладка «Музыка» в поиске Telegram)
// и история прослушанного.
public sealed partial class TelegramService
{
    private MusicLibrary? _musicLibrary;

    /// <summary>История прослушанного и подбор (создаётся при первом обращении).</summary>
    public MusicLibrary MusicLibrary => _musicLibrary ??= new MusicLibrary(_env.DataDirectory);

    // Поиск музыки нагружает Telegram: когда подбор слал десятки поисков разом, Telegram начинал придерживать запросы
    // (FLOOD_WAIT), и поиск, который набрал пользователь, ждал за ними. Теперь фоновые запросы (подбор, полки,
    // подготовка следующей песни) идут по одному, с паузой, и ждут, пока идёт поиск пользователя.
    private readonly SemaphoreSlim _musicBackground = new(1);
    private int _musicForeground;
    // До какого момента фон молчит: пока пользователь ищет и ещё 10 с после (вдруг он печатает дальше).
    private long _musicQuietUntil;

    private async Task<T> MusicRequestAsync<T>(bool background, Func<Task<T>> request)
    {
        if (!background)
        {
            Interlocked.Increment(ref _musicForeground);
            Volatile.Write(ref _musicQuietUntil, Environment.TickCount64 + 10_000);
            try { return await request().ConfigureAwait(false); }
            finally
            {
                Interlocked.Decrement(ref _musicForeground);
                Volatile.Write(ref _musicQuietUntil, Environment.TickCount64 + 10_000);
            }
        }
        await _musicBackground.WaitAsync().ConfigureAwait(false);
        try
        {
            while (Volatile.Read(ref _musicForeground) > 0 || Environment.TickCount64 < Volatile.Read(ref _musicQuietUntil))
                await Task.Delay(300).ConfigureAwait(false);
            return await request().ConfigureAwait(false);
        }
        finally
        {
            _ = Task.Delay(1000).ContinueWith(_ => _musicBackground.Release(), TaskScheduler.Default); // не чаще раза в секунду
        }
    }

    /// <summary>Песни во всех чатах и каналах (полные треки, новые первыми).</summary>
    /// <param name="background">Для подбора и полок: по очереди, после поиска пользователя.</param>
    public Task<IReadOnlyList<MessageItem>> SearchMusicAsync(string query, int limit = 30, bool background = false) =>
        MusicRequestAsync(background, () => SearchMusicCoreAsync(query, limit));

    private async Task<IReadOnlyList<MessageItem>> SearchMusicCoreAsync(string query, int limit)
    {
        var res = await _client.Messages_SearchGlobal(query, new InputMessagesFilterMusic(), limit: limit).ConfigureAwait(false);
        await Task.Yield();
        return await Task.Run(() =>
        {
            res.CollectUsersChats(_users, _chats);
            return res.Messages.Where(m => m is Message && !IsHiddenMessage(m)).Select(MapMessage)
                .Where(m => m.Media == MediaKind.Audio && m.CanDownload).ToList();
        });
    }

    /// <summary>
    /// Полная версия трека в Telegram (для треков Deezer, у которых только 30 с): та же песня, не короче минуты.
    /// </summary>
    public async Task<MessageItem?> FindFullSongAsync(string artist, string title)
    {
        var found = await SearchMusicAsync($"{artist} {title}".Trim(), 20);
        var wantTitle = MusicTrack.Normalize(title);
        var wantArtist = MusicTrack.Normalize(artist);
        return found.FirstOrDefault(m => m.DurationSeconds >= 60
            && MusicTrack.Normalize(m.SongTitle ?? m.MediaTitle ?? "") is var t && t.Length > 0 && (t == wantTitle || t.StartsWith(wantTitle) || wantTitle.StartsWith(t))
            && (wantArtist.Length == 0 || MusicTrack.Normalize(m.SongPerformer ?? m.MediaTitle ?? "").Contains(wantArtist)
                || wantArtist.Contains(MusicTrack.Normalize(m.SongPerformer ?? "x"))));
    }

    // ===================================================================== Музыкальные боты

    // Инлайн-режим ботов — как «@бот песня» в поле ввода, только ничего не отправляется: запрос идёт «из Избранного»,
    // чат с ботом не создаётся и в списке чатов не появляется. Кроме известных музыкальных ботов пробуем и недавних
    // инлайн-ботов пользователя; кто не вернул ни одной песни — за этот запуск больше не спрашиваем.
    private static readonly string[] KnownMusicBots =
        ["scdlbot", "soundcloud_youtube_music_bot", "fmusbot", "vkm_bot", "Buddy_music_bot", "KDMusic_Bot", "melobot", "vkmusic_bot", "lybot", "MusicsHuntersbot"];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _musicBots = new(StringComparer.OrdinalIgnoreCase);
    private Task<IReadOnlyList<string>>? _musicBotCandidates;

    private Task<IReadOnlyList<string>> MusicBotCandidatesAsync() => _musicBotCandidates ??= Task.Run(async () =>
    {
        var recent = await GetRecentInlineBotsAsync(8);
        return (IReadOnlyList<string>)KnownMusicBots.Concat(recent.Select(b => b.Peer.Username).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    });

    /// <summary>Заранее найти музыкальных ботов по имени (раздел открыли) — первый поиск не тратит на это время.</summary>
    public async Task PrepareMusicBotsAsync()
    {
        try
        {
            foreach (var name in (await MusicBotCandidatesAsync()).Take(6))
                if (await ResolveInlineBotAsync(name) is null) _musicBots[name] = false;
        }
        catch (Exception ex) { Logging.Write($"music bots: {ex.Message}"); }
    }

    /// <summary>
    /// Песни от музыкальных ботов (полные, не короче минуты). Боты спрашиваются по очереди: следующий — только если
    /// песен ещё меньше maxSongs; новый запрос (ct) останавливает очередь — старые варианты набранного не досылаются.
    /// </summary>
    /// <param name="partial">Ответ каждого бота — сразу, как пришёл (страница показывает его, не дожидаясь остальных).</param>
    /// <param name="background">Для подбора: после поиска пользователя, сначала проверенные боты.</param>
    public async Task<IReadOnlyList<MusicTrack>> SearchBotMusicAsync(string query, int maxBots = 6, Action<IReadOnlyList<MusicTrack>>? partial = null,
        bool background = false, int maxSongs = 10, CancellationToken ct = default)
    {
        // Порядок списка (новые и любимые боты — первыми); не ответившие ни разу и не музыкальные — пропускаются.
        var candidates = (await MusicBotCandidatesAsync()).Where(b => !_musicBots.TryGetValue(b, out var works) || works);
        if (background) candidates = candidates.OrderByDescending(b => _musicBots.TryGetValue(b, out var works) && works);
        var found = new List<MusicTrack>();
        foreach (var bot in candidates.Take(maxBots))
        {
            if (ct.IsCancellationRequested || found.Count >= maxSongs) break;
            var request = MusicRequestAsync(background, () => InlineMusicAsync(bot, query));
            // Бот думает дольше 6 с — не ждём его, спрашиваем следующего (его ответ всё равно покажется, если успеет).
            if (await Task.WhenAny(request, Task.Delay(6000, ct)).ConfigureAwait(false) != request)
            {
                if (ct.IsCancellationRequested) break;
                _ = request.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && !ct.IsCancellationRequested && t.Result.Count > 0) partial?.Invoke(t.Result.Take(maxSongs).ToList());
                }, TaskScheduler.Default);
                continue;
            }
            var fresh = (await request).Where(t => !found.Any(f => MusicTrack.SameSong(f, t))).Take(maxSongs - found.Count).ToList();
            if (fresh.Count == 0 || ct.IsCancellationRequested) continue;
            found.AddRange(fresh);
            partial?.Invoke(fresh);
        }
        return found;
    }

    private async Task<IReadOnlyList<MusicTrack>> InlineMusicAsync(string botName, string query)
    {
        try
        {
            if (await ResolveInlineBotAsync(botName) is not { } bot) { _musicBots[botName] = false; return []; }
            var res = await _client.Messages_GetInlineBotResults((User)bot.Peer.Info, InputPeer.Self, query, "").ConfigureAwait(false);
            await Task.Yield();
            foreach (var (id, user) in res.users) _users[id] = user;
            var songs = new List<MusicTrack>();
            foreach (var r in res.results)
            {
                switch (r)
                {
                    // Файл в Telegram — играет так же, как песня из чата (по кусочкам, сразу).
                    case BotInlineMediaResult { document: Document d } m when d.attributes.OfType<DocumentAttributeAudio>().FirstOrDefault() is { } a
                                                                            && !a.flags.HasFlag(DocumentAttributeAudio.Flags.voice):
                        var song = MapSong(d, bot.Peer.Id);
                        if (MusicTrack.Tidy(song.SongPerformer ?? m.description, song.SongTitle ?? m.title) is not { } named) break;
                        songs.Add(new MusicTrack
                        {
                            Source = MusicSource.Bot, Id = d.id.ToString(), BotName = botName, Song = song, Live = true,
                            Title = named.Title, Artist = named.Artist,
                            Duration = song.DurationSeconds,
                        });
                        break;
                    // Ссылка на файл (бот отдаёт аудио со своего сервера).
                    case BotInlineResult { content: { } c } web when c.MimeType?.StartsWith("audio/") == true:
                        var audio = c.Attributes?.OfType<DocumentAttributeAudio>().FirstOrDefault();
                        if (MusicTrack.Tidy(audio?.performer ?? web.description, audio?.title ?? web.title) is not { } tidy) break;
                        songs.Add(new MusicTrack
                        {
                            Source = MusicSource.Bot, Id = botName + ":" + web.id, BotName = botName, StreamUrl = c.Url, Live = true,
                            Title = tidy.Title, Artist = tidy.Artist,
                            Duration = audio?.duration ?? 0, CoverUrl = web.thumb?.Url, CoverBigUrl = web.thumb?.Url,
                        });
                        break;
                }
            }
            songs.RemoveAll(s => s.Duration is > 0 and < 60); // отрывки и рингтоны
            songs = MusicTrack.Distinct(songs); // один бот часто отдаёт ту же песню несколькими файлами
            // Ответил, но ни одной песни — это не музыкальный бот (гифки, статьи…); просто ничего не нашёл — ещё спросим.
            if (songs.Count > 0) _musicBots[botName] = true;
            else if (res.results.Length > 0 && !_musicBots.GetValueOrDefault(botName)) _musicBots[botName] = false;
            Logging.Write($"music bot @{botName}: «{query}» — {songs.Count} песен");
            return songs;
        }
        catch (Exception ex)
        {
            Logging.Write($"music bot @{botName}: {ex.Message}");
            if (!_musicBots.ContainsKey(botName)) _musicBots[botName] = false;
            return [];
        }
    }

    /// <summary>Песня от бота заново (после перезапуска — ссылки на файлы у ботов временные): тот же запрос тому же боту.</summary>
    public async Task<MusicTrack?> RefindBotSongAsync(MusicTrack track, bool background = false)
    {
        if (track.BotName is not { } bot) return null;
        var found = await MusicRequestAsync(background, () => InlineMusicAsync(bot, $"{track.Artist} {track.Title}".Trim()));
        return found.FirstOrDefault(t => t.Id == track.Id) ?? found.FirstOrDefault(t => MusicTrack.SameSong(t, track));
    }

    /// <summary>Песня из истории заново (после перезапуска): сообщение или музыка профиля.</summary>
    public async Task<MessageItem?> GetSongAsync(long peerKey, int messageId, long mediaId)
    {
        if (messageId == 0)
        {
            if (!_users.TryGetValue(peerKey, out var user) || IsIgnored(user.id)) return null;
            var res = await _client.Users_GetSavedMusic(user, 0, 100, 0).ConfigureAwait(false);
            await Task.Yield();
            return res is Users_SavedMusic list
                ? list.documents.OfType<Document>().Where(d => d.id == mediaId).Select(d => MapSong(d, user.id)).FirstOrDefault()
                : null;
        }
        var m = await RefetchMessageAsync(peerKey, messageId).ConfigureAwait(false);
        if (m is null || IsHiddenMessage(m)) return null;
        var item = MapMessage(m);
        return item.MediaId == mediaId ? item : null;
    }
}
