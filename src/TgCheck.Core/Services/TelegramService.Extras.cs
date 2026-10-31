using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

// Истории, стикеры, GIF, отправка файлов и медиа.
public sealed partial class TelegramService
{
    /// <summary>Обновились результаты опроса: id опроса и новые данные (у вариантов может не быть текста — взять из старых).</summary>
    public event Action<long, PollInfo>? PollUpdated;

    private void OnPollUpdate(UpdateMessagePoll p)
    {
        if (p.results is null) return;
        PollUpdated?.Invoke(p.poll_id, MapPoll(p.poll, p.results));
    }

    // ===================================================================== Истории

    /// <summary>Активные истории контактов и каналов (свои — первыми). Игнорируемые скрыты.</summary>
    public async Task<IReadOnlyList<StoryPeer>> GetStoriesAsync()
    {
        var result = new List<StoryPeer>();
        string? state = null;
        for (var page = 0; page < 5; page++)
        {
            if (await _client.Stories_GetAllStories(state, next: state is not null).ConfigureAwait(false) is not Stories_AllStories all) break;
            await Task.Yield(); // ответ получен — дальше в пуле потоков, а не в сетевом потоке библиотеки (иначе обновления ждут)
            // Первая страница — в кэш: при следующем запуске полоса историй видна сразу, вместе со списком чатов.
            if (page == 0) _ = Task.Run(() => _cache.Save("stories", all));
            MapAllStories(all, result);
            if (!all.flags.HasFlag(Stories_AllStories.Flags.has_more)) break;
            state = all.state;
        }
        return result.OrderByDescending(p => p.Peer.IsSelf).ThenByDescending(p => p.HasUnread).ToList();
    }

    /// <summary>Истории из локального кэша (до ответа сервера), null — кэша нет.</summary>
    public IReadOnlyList<StoryPeer>? GetCachedStories()
    {
        if (_cache.Load<Stories_AllStories>("stories") is not { } all) return null;
        var result = new List<StoryPeer>();
        MapAllStories(all, result, onlyFresh: true);
        return result.OrderByDescending(p => p.Peer.IsSelf).ThenByDescending(p => p.HasUnread).ToList();
    }

    private void MapAllStories(Stories_AllStories all, List<StoryPeer> result, bool onlyFresh = false)
    {
        foreach (var (k, v) in all.users) _users[k] = v;
        foreach (var (k, v) in all.chats) _chats[k] = v;
        foreach (var ps in all.peer_stories)
        {
            if (IsIgnored(ps.peer) || PeerOf(ps.peer) is not { } peer) continue;
            var stories = ps.stories.OfType<StoryItem>().Select(MapStory).OfType<StoryInfo>().Select(s => { s.Owner = peer; return s; })
                .Where(s => !onlyFresh || s.DateUtc > DateTime.UtcNow.AddHours(-24)) // из кэша — только ещё не истёкшие
                .OrderBy(s => s.Id).ToList();
            if (stories.Count > 0) result.Add(new StoryPeer { Peer = peer, Stories = stories, MaxReadId = ps.max_read_id });
        }
    }

    /// <summary>Лайк истории (реакция ❤) или снять его.</summary>
    public async Task LikeStoryAsync(PeerRef peer, int storyId, bool like) =>
        await _client.Stories_SendReaction(peer.Info.ToInputPeer(), storyId,
            like ? new ReactionEmoji { emoticon = "❤" } : null);

    /// <summary>Ответ на историю — сообщение автору в личный чат с цитатой истории.</summary>
    public async Task<MessageItem> ReplyToStoryAsync(PeerRef peer, int storyId, string text)
    {
        EnsureNotIgnored(peer);
        var reply = new InputReplyToStory { peer = peer.Info.ToInputPeer(), story_id = storyId };
        var updates = await _client.Messages_SendMessage(peer.Info.ToInputPeer(), text, WTelegram.Helpers.RandomLong(), reply_to: reply);
        var sent = updates.UpdateList.OfType<UpdateNewMessage>().Select(u => u.message).FirstOrDefault();
        return sent is not null ? MapMessage(sent) : new MessageItem { Id = 0, PeerKey = peer.Key, Text = text, IsOut = true, DateUtc = DateTime.UtcNow };
    }

    /// <summary>Кто посмотрел мою историю: просмотры и реакции (новые первыми).</summary>
    public async Task<(int Views, int Reactions, IReadOnlyList<StoryViewer> Viewers)> GetStoryViewersAsync(int storyId)
    {
        var res = await _client.Stories_GetStoryViewsList(InputPeer.Self, storyId, "", 100);
        foreach (var (k, v) in res.users) _users[k] = v;
        foreach (var (k, v) in res.chats) _chats[k] = v;
        var viewers = res.views.Select(v => v switch
            {
                StoryView sv when !IsIgnored(sv.user_id) && _users.GetValueOrDefault(sv.user_id) is { } u =>
                    new StoryViewer(PeerOf(u), sv.date, (sv.reaction as ReactionEmoji)?.emoticon ?? (sv.reaction is null ? null : "❤")),
                _ => null,
            })
            .OfType<StoryViewer>()
            .ToList();
        return (res.views_count, res.reactions_count, viewers);
    }

    /// <summary>Истории в профиле (закреплённые), для своего профиля — по желанию архив. Новые первыми.</summary>
    public async Task<IReadOnlyList<StoryInfo>> GetProfileStoriesAsync(PeerRef peer, bool archive = false)
    {
        if (IsIgnored(peer)) return [];
        try
        {
            var res = archive
                ? await _client.Stories_GetStoriesArchive(peer.Info.ToInputPeer(), limit: 60).ConfigureAwait(false)
                : await _client.Stories_GetPinnedStories(peer.Info.ToInputPeer(), limit: 60).ConfigureAwait(false);
            await Task.Yield(); // ответ получен — дальше в пуле потоков, а не в сетевом потоке библиотеки (иначе обновления ждут)
            foreach (var (k, v) in res.users) _users[k] = v;
            foreach (var (k, v) in res.chats) _chats[k] = v;
            return res.stories.OfType<StoryItem>().Select(MapStory).OfType<StoryInfo>().Select(s => { s.Owner = peer; return s; }).OrderByDescending(s => s.Id).ToList();
        }
        catch (Exception ex)
        {
            Logging.Write("profile stories: " + ex.Message);
            return [];
        }
    }

    private static StoryInfo? MapStory(StoryItem s) => s.media switch
    {
        MessageMediaPhoto { photo: Photo p } => new StoryInfo { Id = s.id, DateUtc = s.date, Caption = s.caption, Photo = p,
            ViewsCount = s.views?.views_count ?? 0, ReactionsCount = s.views?.reactions_count ?? 0, Liked = s.sent_reaction is not null },
        MessageMediaDocument { document: Document d } => new StoryInfo
        {
            Id = s.id, DateUtc = s.date, Caption = s.caption, Document = d,
            ViewsCount = s.views?.views_count ?? 0, ReactionsCount = s.views?.reactions_count ?? 0, Liked = s.sent_reaction is not null,
            DurationSeconds = d.attributes?.OfType<DocumentAttributeVideo>().FirstOrDefault()?.duration ?? 0,
        },
        _ => null,
    };

    public async Task MarkStoriesReadAsync(PeerRef peer, int maxId)
    {
        try { await _client.Stories_ReadStories(peer.Info.ToInputPeer(), maxId); }
        catch (Exception ex) { Logging.Write("stories read: " + ex.Message); }
    }

    // ===================================================================== Стикеры и GIF

    public async Task<IReadOnlyList<StickerSetInfo>> GetStickerSetsAsync()
    {
        var all = await _client.Messages_GetAllStickers(0);
        return all.sets
            .Where(s => !s.flags.HasFlag(StickerSet.Flags.archived) && !s.flags.HasFlag(StickerSet.Flags.masks) && !s.flags.HasFlag(StickerSet.Flags.emojis))
            .Select(s => new StickerSetInfo { Id = s.id, AccessHash = s.access_hash, Title = s.title, Count = s.count })
            .ToList();
    }

    public async Task<IReadOnlyList<StickerItem>> GetStickerSetAsync(StickerSetInfo set)
    {
        var res = await _client.Messages_GetStickerSet(new InputStickerSetID { id = set.Id, access_hash = set.AccessHash }, 0);
        return res.documents.OfType<Document>().Select(ToSticker).ToList();
    }

    /// <summary>Стикер — в «Недавние» на сервере (видно и в других клиентах).</summary>
    public async Task SaveRecentStickerAsync(MediaHolder sticker)
    {
        if (sticker.Document is not { } doc) return;
        try { await _client.Messages_SaveRecentSticker(doc, unsave: false); }
        catch (Exception ex) { Logging.Write("recent sticker: " + ex.Message); }
    }

    public async Task<IReadOnlyList<StickerItem>> GetRecentStickersAsync()
    {
        var res = await _client.Messages_GetRecentStickers(0);
        return (res as Messages_RecentStickers)?.stickers.OfType<Document>().Select(ToSticker).ToList() ?? [];
    }

    /// <summary>Избранные (сохранённые) стикеры пользователя.</summary>
    public async Task<IReadOnlyList<StickerItem>> GetFavedStickersAsync()
    {
        var res = await _client.Messages_GetFavedStickers(0);
        return (res as Messages_FavedStickers)?.stickers.OfType<Document>().Select(ToSticker).ToList() ?? [];
    }

    /// <summary>Набор, к которому относится стикер: название, стикеры и установлен ли он у пользователя.</summary>
    public async Task<(StickerSetInfo Info, IReadOnlyList<StickerItem> Stickers, bool Installed)?> GetStickerPackOfAsync(MediaHolder sticker)
    {
        if (sticker.Document?.attributes?.OfType<DocumentAttributeSticker>().FirstOrDefault()?.stickerset is not { } input) return null;
        var res = await _client.Messages_GetStickerSet(input, 0);
        var set = res.set;
        var info = new StickerSetInfo { Id = set.id, AccessHash = set.access_hash, Title = set.title, Count = set.count };
        return (info, res.documents.OfType<Document>().Select(ToSticker).ToList(), set.flags.HasFlag(StickerSet.Flags.has_installed_date));
    }

    /// <summary>Добавить набор стикеров себе или удалить его.</summary>
    public async Task SetStickerPackInstalledAsync(StickerSetInfo set, bool install)
    {
        var input = new InputStickerSetID { id = set.Id, access_hash = set.AccessHash };
        if (install) await _client.Messages_InstallStickerSet(input, false);
        else await _client.Messages_UninstallStickerSet(input);
    }

    // ===================================================================== Эмодзи Telegram

    /// <summary>
    /// Эмодзи в оформлении Telegram (набор «анимированных эмодзи» — те же картинки, что в официальных приложениях):
    /// эмодзи (без вариационного селектора U+FE0F) → стикер. Сначала из кэша, затем обновляется с сервера.
    /// </summary>
    public IReadOnlyDictionary<string, StickerItem>? GetCachedTelegramEmoji() =>
        _cache.Load<Messages_StickerSet>("tg_emoji") is { } set ? MapEmojiSet(set) : null;

    public async Task<IReadOnlyDictionary<string, StickerItem>?> GetTelegramEmojiAsync()
    {
        try
        {
            if (await _client.Messages_GetStickerSet(new InputStickerSetAnimatedEmoji(), 0).ConfigureAwait(false) is not { } set) return null;
            await Task.Yield(); // разбор — в пуле потоков, не в сетевом потоке
            _cache.Save("tg_emoji", set);
            return MapEmojiSet(set);
        }
        catch (Exception ex) { Logging.Write("tg emoji: " + ex.Message); return null; }
    }

    private static Dictionary<string, StickerItem> MapEmojiSet(Messages_StickerSet set)
    {
        var docs = set.documents.OfType<Document>().ToDictionary(d => d.id);
        var result = new Dictionary<string, StickerItem>();
        foreach (var pack in set.packs)
        {
            var key = pack.emoticon.Replace("️", "");
            if (result.ContainsKey(key) || pack.documents.Length == 0 || !docs.TryGetValue(pack.documents[0], out var doc)) continue;
            result[key] = ToSticker(doc);
        }
        return result;
    }

    private static StickerItem ToSticker(Document d) => new()
    {
        Document = d,
        Emoji = d.attributes?.OfType<DocumentAttributeSticker>().FirstOrDefault()?.alt,
    };

    public async Task<IReadOnlyList<GifItem>> GetSavedGifsAsync()
    {
        var res = await _client.Messages_GetSavedGifs(0);
        return (res as Messages_SavedGifs)?.gifs.OfType<Document>().Select(d =>
        {
            var v = d.attributes?.OfType<DocumentAttributeVideo>().FirstOrDefault();
            return new GifItem { Document = d, Width = v?.w ?? 0, Height = v?.h ?? 0, DurationSeconds = v?.duration ?? 0 };
        }).ToList() ?? [];
    }

    /// <summary>Отправить стикер или GIF (готовый документ Telegram).</summary>
    public async Task<MessageItem> SendDocumentAsync(PeerRef peer, MediaHolder media, int? replyToId = null)
    {
        EnsureNotIgnored(peer);
        if (media.Document is not { } doc) throw new InvalidOperationException("Нет документа для отправки.");
        var input = new InputMediaDocument { id = new InputDocument { id = doc.id, access_hash = doc.access_hash, file_reference = doc.file_reference } };
        var msg = await _client.SendMessageAsync(peer.Info.ToInputPeer(), "", input, replyToId ?? 0);
        return MapMessage(msg);
    }

    // ===================================================================== Темы в группах-форумах (как в Telegram)

    /// <summary>Темы форума: закреплённые первыми, затем по последнему сообщению.</summary>
    public async Task<IReadOnlyList<ForumTopicInfo>> GetForumTopicsAsync(PeerRef forum)
    {
        if (forum.Info is not Channel channel) return [];
        var res = await _client.Messages_GetForumTopics(channel, default, 0, 0, 100).ConfigureAwait(false); // разбор ответа — не в потоке интерфейса
        await Task.Yield(); // ответ получен — дальше в пуле потоков, а не в сетевом потоке библиотеки (иначе обновления ждут)
        res.CollectUsersChats(_users, _chats);
        var lastMessages = res.messages.Where(m => m is Message && !IsHiddenMessage(m)).ToDictionary(m => m.ID, MapMessage);
        return res.topics.OfType<ForumTopic>()
            .Select(t => new ForumTopicInfo
            {
                Id = t.id,
                Title = t.title,
                IconColor = t.icon_color,
                IconEmojiId = t.flags.HasFlag(ForumTopic.Flags.has_icon_emoji_id) ? t.icon_emoji_id : 0,
                UnreadCount = t.unread_count,
                IsPinned = t.flags.HasFlag(ForumTopic.Flags.pinned),
                IsClosed = t.flags.HasFlag(ForumTopic.Flags.closed),
                Last = lastMessages.GetValueOrDefault(t.top_message),
            })
            .OrderByDescending(t => t.IsPinned)
            .ThenByDescending(t => t.Last?.DateUtc ?? DateTime.MinValue)
            .ToList();
    }

    /// <summary>Отметить тему форума прочитанной до сообщения maxId.</summary>
    public async Task ReadTopicAsync(PeerRef forum, int topicId, int maxId)
    {
        try { await _client.Messages_ReadDiscussion(forum.Info.ToInputPeer(), topicId, maxId); }
        catch (Exception ex) { Logging.Write("read topic: " + ex.Message); }
    }

    /// <summary>
    /// Сообщения темы «Общее» (id 1): у них нет привязки к теме, поэтому берётся общая история группы
    /// без сообщений других тем.
    /// </summary>
    public async Task<IReadOnlyList<MessageItem>> GetGeneralTopicAsync(PeerRef forum, int offsetId = 0, int limit = 50)
    {
        var history = await GetHistoryAsync(forum, offsetId, limit);
        return history.Where(m => m.TopicId is null).ToList();
    }

    // ===================================================================== Закрепление и уведомления чата

    /// <summary>Закрепить чат в списке или открепить.</summary>
    public async Task SetPinnedAsync(PeerRef peer, bool pinned) =>
        await _client.Messages_ToggleDialogPin(new InputDialogPeer { peer = peer.Info.ToInputPeer() }, pinned);

    /// <summary>Выключить уведомления чата (навсегда) или включить снова.</summary>
    public async Task SetMutedAsync(PeerRef peer, bool muted) =>
        await _client.Account_UpdateNotifySettings(new InputNotifyPeer { peer = peer.Info.ToInputPeer() },
            new InputPeerNotifySettings
            {
                flags = InputPeerNotifySettings.Flags.has_mute_until,
                mute_until = muted ? DateTime.UtcNow.AddYears(30) : DateTime.UnixEpoch,
            });

    /// <summary>Свой статус «в сети» (true) или «был(а) недавно» (false).</summary>
    public async Task SetOnlineAsync(bool online)
    {
        try { await _client.Account_UpdateStatus(offline: !online); }
        catch (Exception ex) { Logging.Write("status: " + ex.Message); }
    }

    // ===================================================================== Управление чатом

    /// <summary>Очистить историю (чат остаётся). forEveryone — и у собеседника (личка, бот, обычная группа).</summary>
    public async Task ClearHistoryAsync(PeerRef peer, bool forEveryone)
    {
        EnsureNotIgnored(peer);
        if (peer.Info is Channel ch) { await _client.Channels_DeleteHistory(ch, 0, for_everyone: true); return; }
        await DeleteHistoryAllAsync(peer, justClear: true, revoke: forEveryone);
    }

    /// <summary>
    /// Удалить чат: личка и бот — история целиком (forEveryone — и у собеседника); обычная группа — выйти и удалить;
    /// супергруппа и канал — покинуть, а создатель с forEveryone — удалить для всех.
    /// </summary>
    public async Task DeleteChatAsync(PeerRef peer, bool forEveryone)
    {
        EnsureNotIgnored(peer);
        switch (peer.Info)
        {
            case Channel ch when peer.IsCreator && forEveryone:
                await _client.Channels_DeleteChannel(ch);
                break;
            case Channel ch:
                await _client.Channels_LeaveChannel(ch);
                break;
            case ChatBase chat:
                try { await _client.Messages_DeleteChatUser(chat.ID, InputUser.Self, revoke_history: true); }
                catch (RpcException ex) when (ex.Message.Contains("USER_NOT_PARTICIPANT")) { }
                await DeleteHistoryAllAsync(peer, justClear: false, revoke: false);
                break;
            default:
                await DeleteHistoryAllAsync(peer, justClear: false, revoke: forEveryone);
                break;
        }
    }

    // Сервер удаляет историю порциями: повторяем, пока он сообщает, что осталось ещё.
    private async Task DeleteHistoryAllAsync(PeerRef peer, bool justClear, bool revoke)
    {
        for (var i = 0; i < 100; i++)
        {
            var res = await _client.Messages_DeleteHistory(peer.Info.ToInputPeer(), 0, just_clear: justClear, revoke: revoke);
            if (res.offset <= 0) break;
        }
    }

    /// <summary>Заблокирован ли пользователь (из полного профиля).</summary>
    public async Task<bool> IsBlockedAsync(PeerRef peer)
    {
        if (peer.Info is not User u) return false;
        var full = await _client.Users_GetFullUser(u);
        return full.full_user.flags.HasFlag(UserFull.Flags.blocked);
    }

    public async Task SetBlockedAsync(PeerRef peer, bool blocked)
    {
        if (blocked) await _client.Contacts_Block(peer.Info.ToInputPeer());
        else await _client.Contacts_Unblock(peer.Info.ToInputPeer());
        _freshFull.TryRemove(peer.Id, out _); // в профиле — свежее состояние
        _freshProfiles.TryRemove(peer.Id, out _);
    }

    /// <summary>Новое название группы или канала.</summary>
    public async Task EditTitleAsync(PeerRef peer, string title)
    {
        if (peer.Info is Channel ch) await _client.Channels_EditTitle(ch, title);
        else await _client.Messages_EditChatTitle(peer.Id, title);
    }

    /// <summary>Новое описание группы или канала.</summary>
    public async Task EditAboutAsync(PeerRef peer, string about) =>
        await _client.Messages_EditChatAbout(peer.Info.ToInputPeer(), about);

    /// <summary>Ссылка-приглашение (основная, без срока).</summary>
    public async Task<string?> GetInviteLinkAsync(PeerRef peer) =>
        (await _client.Messages_ExportChatInvite(peer.Info.ToInputPeer()) as ChatInviteExported)?.link;

    // ===================================================================== Реакции

    internal static IReadOnlyList<ReactionInfo> MapReactions(MessageReactions? reactions)
    {
        if (reactions?.results is not { Length: > 0 } results) return [];
        return results.Select(r => r.reaction switch
            {
                ReactionEmoji e => new ReactionInfo(e.emoticon, 0, r.count, r.flags.HasFlag(ReactionCount.Flags.has_chosen_order)),
                ReactionCustomEmoji c => new ReactionInfo(null, c.document_id, r.count, r.flags.HasFlag(ReactionCount.Flags.has_chosen_order)),
                ReactionPaid => new ReactionInfo("⭐", 0, r.count, r.flags.HasFlag(ReactionCount.Flags.has_chosen_order), IsPaid: true),
                _ => null,
            })
            .OfType<ReactionInfo>()
            .ToList();
    }

    /// <summary>Поставить реакции (пустой список — снять). Возвращает новые реакции сообщения.</summary>
    public async Task SetReactionsAsync(PeerRef peer, int messageId, IReadOnlyList<ReactionInfo> mine)
    {
        Reaction[] reaction = [.. mine.Where(r => !r.IsPaid).Select(r => r.CustomEmojiId != 0
            ? (Reaction)new ReactionCustomEmoji { document_id = r.CustomEmojiId }
            : new ReactionEmoji { emoticon = r.Emoji })];
        var updates = await _client.Messages_SendReaction(peer.Info.ToInputPeer(), messageId, reaction);
        // Сервер присылает UpdateMessageReactions — его разберёт общий обработчик обновлений.
        foreach (var u in updates.UpdateList) if (u is UpdateMessageReactions r && PeerOf(r.peer) is { } rp)
            ReactionsUpdated?.Invoke(rp.Key, r.msg_id, MapReactions(r.reactions));
    }

    private Task<IReadOnlyList<ReactionDef>>? _reactionDefs;

    /// <summary>
    /// Каталог реакций Telegram с их анимациями (как в официальных клиентах): иконка, анимация выбора,
    /// анимация постановки и эффект. Загружается один раз.
    /// </summary>
    public Task<IReadOnlyList<ReactionDef>> GetReactionDefsAsync() => _reactionDefs ??= LoadReactionDefsAsync();

    private async Task<IReadOnlyList<ReactionDef>> LoadReactionDefsAsync()
    {
        try
        {
            if (await _client.Messages_GetAvailableReactions(0) is not Messages_AvailableReactions all) return [];
            static StickerItem? S(Document? d) => d is null ? null : ToSticker(d);
            return all.reactions
                .Where(r => !r.flags.HasFlag(AvailableReaction.Flags.inactive))
                .Select(r => new ReactionDef(r.reaction, r.title, r.flags.HasFlag(AvailableReaction.Flags.premium),
                    S(r.center_icon as Document) ?? S(r.static_icon as Document),
                    S(r.select_animation as Document), S(r.activate_animation as Document), S(r.effect_animation as Document)))
                .ToList();
        }
        catch (Exception ex)
        {
            Logging.Write("reactions: " + ex.Message);
            _reactionDefs = null; // попробуем ещё раз позже
            return [];
        }
    }

    /// <summary>
    /// Какие реакции разрешены в чате: null — любые (личные чаты и «все реакции»), пустой список — реакции выключены,
    /// иначе — только эти эмодзи (админы каналов и групп часто ограничивают набор; чужие дают REACTION_INVALID).
    /// </summary>
    public async Task<IReadOnlyList<ReactionInfo>?> GetAllowedReactionsAsync(PeerRef peer)
    {
        if (peer.IsUser) return null;
        try
        {
            ChatReactions? reactions = peer.Info switch
            {
                Channel channel => (await _client.Channels_GetFullChannel(channel)).full_chat.AvailableReactions,
                TL.Chat chat => (await _client.Messages_GetFullChat(chat.id)).full_chat.AvailableReactions,
                _ => null,
            };
            return reactions switch
            {
                ChatReactionsAll => null,
                // Обычные эмодзи и собственные (премиум) эмодзи канала — их могут ставить все подписчики.
                ChatReactionsSome some => some.reactions.Select(r => r switch
                    {
                        ReactionEmoji e => new ReactionInfo(e.emoticon, 0, 0, false),
                        ReactionCustomEmoji c => new ReactionInfo(null, c.document_id, 0, false),
                        _ => null,
                    }).OfType<ReactionInfo>().ToList(),
                _ => [],
            };
        }
        catch (Exception ex)
        {
            Logging.Write("allowed reactions: " + ex.Message);
            return null;
        }
    }

    /// <summary>Быстрые реакции (как стандартный набор Telegram).</summary>
    public static readonly string[] QuickReactions = ["👍", "❤", "🔥", "🥰", "👏", "😁", "🤔", "😢", "🎉", "🤩", "🙏", "👌", "😍", "🤣", "💯", "😭", "🤯", "😱", "👎", "💔"];

    /// <summary>Переслать сообщения (альбом — целиком) в другой чат.</summary>
    public async Task ForwardAsync(PeerRef from, IReadOnlyList<int> ids, PeerRef to)
    {
        EnsureNotIgnored(to);
        var random = ids.Select(_ => WTelegram.Helpers.RandomLong()).ToArray();
        await _client.Messages_ForwardMessages(from.Info.ToInputPeer(), [.. ids], random, to.Info.ToInputPeer());
    }

    /// <summary>Фото в полном размере (для копирования в буфер обмена).</summary>
    public async Task<byte[]?> GetPhotoBytesAsync(MediaHolder media) => await Media.GetFullPhotoAsync(media);

    /// <summary>Отправить записанное голосовое (Ogg/Opus с волной).</summary>
    public async Task<MessageItem> SendVoiceAsync(PeerRef peer, Media.VoiceRecording voice, int? replyToId = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        EnsureNotIgnored(peer);
        // Отмена — исключением из обратного вызова загрузки (своей отмены у библиотеки нет).
        var file = await _client.UploadFileAsync(voice.Path, (done, total) =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(total > 0 ? (double)done / total : 0);
        });
        ct.ThrowIfCancellationRequested();
        var media = new InputMediaUploadedDocument
        {
            file = file,
            mime_type = "audio/ogg",
            attributes =
            [
                new DocumentAttributeAudio
                {
                    flags = DocumentAttributeAudio.Flags.voice | DocumentAttributeAudio.Flags.has_waveform,
                    duration = (int)Math.Round(voice.DurationSeconds),
                    waveform = voice.Waveform,
                },
            ],
        };
        var msg = await _client.SendMessageAsync(peer.Info.ToInputPeer(), "", media, replyToId ?? 0);
        try { File.Delete(voice.Path); } catch { }
        return MapMessage(msg);
    }

    // ===================================================================== Файлы

    private static readonly HashSet<string> PhotoExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".webm", ".mkv" };

    /// <summary>
    /// Отправляет файлы. Фото/видео (если не asFiles) уходят альбомом до 10 штук, остальное — документами;
    /// подпись прикрепляется к первому сообщению.
    /// </summary>
    public async Task SendFilesAsync(PeerRef peer, IReadOnlyList<string> paths, string? caption, int? replyToId,
        bool asFiles, IProgress<(int Index, double Fraction)>? progress = null, CancellationToken ct = default)
    {
        EnsureNotIgnored(peer);
        var input = peer.Info.ToInputPeer();
        var medias = new List<InputMedia>();
        for (var i = 0; i < paths.Count; i++)
        {
            var index = i;
            var path = paths[i];
            var file = await _client.UploadFileAsync(path, (done, total) =>
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((index, total > 0 ? (double)done / total : 0));
            });
            var ext = Path.GetExtension(path);
            if (!asFiles && PhotoExt.Contains(ext) && new FileInfo(path).Length < 10 * 1024 * 1024)
            {
                medias.Add(new InputMediaUploadedPhoto { file = file });
            }
            else
            {
                var attrs = new List<DocumentAttribute> { new DocumentAttributeFilename { file_name = Path.GetFileName(path) } };
                if (!asFiles && VideoExt.Contains(ext))
                    attrs.Add(new DocumentAttributeVideo { flags = DocumentAttributeVideo.Flags.supports_streaming });
                medias.Add(new InputMediaUploadedDocument
                {
                    file = file,
                    mime_type = MimeOf(ext),
                    attributes = [.. attrs],
                    flags = asFiles ? InputMediaUploadedDocument.Flags.force_file : 0,
                });
            }
        }

        ct.ThrowIfCancellationRequested();
        var first = true;
        foreach (var chunk in medias.Chunk(10))
        {
            var (text, entities) = first ? MarkdownEntities.Parse(caption ?? "") : ("", null);
            if (chunk.Length == 1) await _client.SendMessageAsync(input, text, chunk[0], first ? replyToId ?? 0 : 0, entities);
            else await _client.SendAlbumAsync(input, chunk, text, first ? replyToId ?? 0 : 0, entities);
            first = false;
        }
    }

    private static string MimeOf(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".ogg" or ".oga" => "audio/ogg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream",
    };

    // ===================================================================== Боты: кнопки, меню, мини-приложения

    private static IReadOnlyList<IReadOnlyList<BotButton>> MapInline(ReplyInlineMarkup? markup) =>
        markup?.rows.Select(r => (IReadOnlyList<BotButton>)r.buttons.Select(b => b.type switch
        {
            InlineButtonTypeCallback c => new BotButton { Text = b.text, Kind = BotButtonKind.Callback, Data = c.data },
            InlineButtonTypeUrl u => new BotButton { Text = b.text, Kind = BotButtonKind.Url, Url = u.url },
            InlineButtonTypeUrlAuth ua => new BotButton { Text = b.text, Kind = BotButtonKind.Url, Url = ua.url },
            InlineButtonTypeWebView w => new BotButton { Text = b.text, Kind = BotButtonKind.WebApp, Url = w.url },
            InlineButtonTypeCopy cp => new BotButton { Text = b.text, Kind = BotButtonKind.Copy, CopyText = cp.copy_text },
            InlineButtonTypeUserProfile p => new BotButton { Text = b.text, Kind = BotButtonKind.Profile, UserId = p.user_id },
            InlineButtonTypeSwitchInline => new BotButton { Text = b.text, Kind = BotButtonKind.SwitchInline },
            _ => new BotButton { Text = b.text, Kind = BotButtonKind.Other },
        }).ToList()).ToList() ?? [];

    /// <summary>Нажатие callback-кнопки: бот отвечает всплывающим текстом и/или ссылкой.</summary>
    public async Task<BotCallbackResult> PressCallbackAsync(PeerRef peer, int messageId, BotButton button)
    {
        var answer = await _client.Messages_GetBotCallbackAnswer(peer.Info.ToInputPeer(), messageId, button.Data);
        return new BotCallbackResult(answer.message, answer.url, answer.flags.HasFlag(Messages_BotCallbackAnswer.Flags.alert));
    }

    private const string Platform = "tdesktop";

    // Цвета темы для мини-приложений — в стиле приложения (как отдают официальные клиенты).
    private static DataJSON ThemeParams => new()
    {
        data = """{"bg_color":"#17212b","secondary_bg_color":"#0e1621","text_color":"#f5f7fa","hint_color":"#8c9bab","link_color":"#6ab3f3","button_color":"#2aabee","button_text_color":"#ffffff","header_bg_color":"#17212b","accent_text_color":"#6ab3f3","section_bg_color":"#17212b","section_header_text_color":"#6ab3f3","subtitle_text_color":"#8c9bab","destructive_text_color":"#e5534b"}""",
    };

    public static string ThemeJson => ThemeParams.data;

    /// <summary>Кнопка меню бота и наличие главного мини-приложения.</summary>
    public async Task<BotMenu?> GetBotMenuAsync(PeerRef bot)
    {
        if (bot.Info is not User { IsBot: true } user) return null;
        try
        {
            var full = await _client.Users_GetFullUser(user).ConfigureAwait(false);
            await Task.Yield(); // ответ получен — дальше в пуле потоков, а не в сетевом потоке библиотеки (иначе обновления ждут)
            full.CollectUsersChats(_users, _chats);
            var hasMain = user.flags2.HasFlag(User.Flags2.bot_has_main_app);
            return full.full_user.bot_info?.menu_button switch
            {
                BotMenuButton b => new BotMenu(b.text, b.url, hasMain),
                _ => hasMain ? new BotMenu("Открыть", null, true) : null,
            };
        }
        catch (Exception ex)
        {
            Logging.Write("bot menu: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Адрес мини-приложения: по кнопке под сообщением (url), из меню бота (fromMenu) или главное (url == null).
    /// </summary>
    public async Task<string> RequestWebAppAsync(PeerRef chat, PeerRef bot, string? url, bool fromMenu, bool simple = false)
    {
        if (bot.Info is not User botUser) throw new InvalidOperationException("Мини-приложение есть только у ботов.");
        if (simple)
            return UrlOf(await _client.Messages_RequestSimpleWebView(botUser, Platform, ThemeParams, url));
        if (url is null)
            return UrlOf(await _client.Messages_RequestMainWebView(chat.Info.ToInputPeer(), botUser, Platform, ThemeParams));
        return UrlOf(await _client.Messages_RequestWebView(chat.Info.ToInputPeer(), botUser, Platform, url: url, theme_params: ThemeParams, from_bot_menu: fromMenu));
    }

    private static string UrlOf(WebViewResult r) => (r as WebViewResultUrl)?.url ?? throw new InvalidOperationException("Бот не вернул адрес мини-приложения.");

    /// <summary>Кнопка «Старт» у бота, с которым ещё нет переписки (как /start в Telegram).</summary>
    public async Task StartBotAsync(PeerRef bot)
    {
        if (bot.Info is not User user) return;
        EnsureNotIgnored(bot);
        await _client.Messages_StartBot(user, bot.Info.ToInputPeer(), WTelegram.Helpers.RandomLong(), "");
    }

    /// <summary>Отправить текст кнопки клавиатуры бота (как обычное сообщение).</summary>
    public Task<MessageItem> SendKeyboardButtonAsync(PeerRef peer, BotButton button) => SendTextAsync(peer, button.Text);

    // ===================================================================== Комментарии к постам каналов

    /// <summary>Обсуждение поста: группа обсуждения, id исходного сообщения в ней и комментарии (по порядку).</summary>
    public async Task<(PeerRef Discussion, int TopId, MessageItem Top)> GetThreadAsync(PeerRef channel, int postId)
    {
        var discussion = await _client.Messages_GetDiscussionMessage(channel.Info.ToInputPeer(), postId);
        foreach (var (k, v) in discussion.users) _users[k] = v;
        foreach (var (k, v) in discussion.chats) _chats[k] = v;
        var top = discussion.messages.OfType<Message>().MinBy(m => m.id) ?? throw new PeerNotFoundException("Обсуждение недоступно.");
        var group = PeerOf(top.peer_id) ?? throw new PeerNotFoundException("Обсуждение недоступно.");
        return (group, top.id, MapMessage(top));
    }

    /// <summary>Ответы в ветке обсуждения (по порядку), страницами от offsetId вниз.</summary>
    public async Task<IReadOnlyList<MessageItem>> GetRepliesAsync(PeerRef discussion, int topId, int offsetId = 0, int limit = 50)
    {
        var replies = await _client.Messages_GetReplies(discussion.Info.ToInputPeer(), topId, offset_id: offsetId, limit: limit);
        return await Task.Run(() => MapHistory(replies));
    }

    public async Task<MessageItem> SendCommentAsync(PeerRef discussion, int topId, string text)
    {
        var (clean, entities) = MarkdownEntities.Parse(text);
        return MapMessage(await _client.SendMessageAsync(discussion.Info.ToInputPeer(), clean, reply_to_msg_id: topId, entities: entities));
    }

    // ===================================================================== Закреплённые и переход к сообщению

    /// <summary>Закреплённые сообщения чата (новые первыми).</summary>
    public async Task<IReadOnlyList<MessageItem>> GetPinnedAsync(PeerRef peer)
    {
        try
        {
            var res = await _client.Messages_Search(peer.Info.ToInputPeer(), null, new InputMessagesFilterPinned(), limit: 20);
            return await Task.Run(() =>
            {
                res.CollectUsersChats(_users, _chats);
                return res.Messages.Where(m => m is Message && !IsHiddenMessage(m)).Select(MapMessage).ToList();
            });
        }
        catch (Exception ex)
        {
            Logging.Write("pinned: " + ex.Message);
            return [];
        }
    }

    /// <summary>История вокруг сообщения (для перехода к нему), по порядку.</summary>
    public async Task<IReadOnlyList<MessageItem>> GetHistoryAroundAsync(PeerRef peer, int messageId, int limit = 50)
    {
        var res = await _client.Messages_GetHistory(peer.Info.ToInputPeer(), offset_id: messageId + 1, add_offset: -limit / 2, limit: limit);
        return await Task.Run(() => MapHistory(res));
    }

    // ===================================================================== Кастомные (премиум) эмодзи

    private readonly Dictionary<long, TaskCompletionSource<StickerItem?>> _customEmoji = [];
    private readonly List<long> _customEmojiQueue = [];
    private bool _customEmojiScheduled;

    /// <summary>
    /// Документ кастомного эмодзи по id. Запросы за ~40 мс собираются в один вызов Telegram (до 100 штук).
    /// </summary>
    public Task<StickerItem?> GetCustomEmojiAsync(long id)
    {
        lock (_customEmoji)
        {
            if (_customEmoji.TryGetValue(id, out var existing)) return existing.Task;
            var tcs = new TaskCompletionSource<StickerItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _customEmoji[id] = tcs;
            _ = Task.Run(() =>
            {
                // Уже знакомое эмодзи — из локального кэша; иначе — в очередь к серверу (пачками).
                if (Media.HasCachedDocumentPreview(id) && _cache.Load<Document>($"emoji_{id}") is { } cached && ToSticker(cached) is { } sticker)
                {
                    tcs.TrySetResult(sticker);
                    return;
                }
                lock (_customEmoji)
                {
                    _customEmojiQueue.Add(id);
                    if (!_customEmojiScheduled)
                    {
                        _customEmojiScheduled = true;
                        _ = FlushCustomEmojiAsync();
                    }
                }
            });
            return tcs.Task;
        }
    }

    private async Task FlushCustomEmojiAsync()
    {
        await Task.Delay(40).ConfigureAwait(false);
        long[] batch;
        lock (_customEmoji)
        {
            batch = _customEmojiQueue.Take(100).ToArray();
            _customEmojiQueue.RemoveRange(0, batch.Length);
            _customEmojiScheduled = _customEmojiQueue.Count > 0;
        }
        if (_customEmojiScheduled) _ = FlushCustomEmojiAsync();
        try
        {
            var docs = (await _client.Messages_GetCustomEmojiDocuments(batch).ConfigureAwait(false)).OfType<Document>().ToDictionary(d => d.id);
            await Task.Yield(); // ответ получен — дальше в пуле потоков, а не в сетевом потоке библиотеки (иначе обновления ждут)
            // На диск: при следующем запуске эмодзи (реакции, статусы, в тексте) видны сразу, без запроса к серверу.
            foreach (var d in docs.Values) _cache.Save($"emoji_{d.id}", d);
            lock (_customEmoji)
                foreach (var id in batch)
                    _customEmoji[id].TrySetResult(docs.TryGetValue(id, out var d) ? ToSticker(d) : null);
        }
        catch (Exception ex)
        {
            Logging.Write("custom emoji: " + ex.Message);
            lock (_customEmoji)
                foreach (var id in batch)
                {
                    _customEmoji[id].TrySetResult(null);
                    _customEmoji.Remove(id); // попробовать снова позже
                }
        }
    }

    // ===================================================================== Общие медиа чата

    /// <summary>Фото и видео, файлы или голосовые/кружки из чата (новые первыми).</summary>
    public async Task<IReadOnlyList<MessageItem>> SearchMediaAsync(PeerRef peer, SharedMediaKind kind, int offsetId = 0, int limit = 60)
    {
        MessagesFilter filter = kind switch
        {
            SharedMediaKind.Files => new InputMessagesFilterDocument(),
            SharedMediaKind.Voice => new InputMessagesFilterRoundVoice(),
            SharedMediaKind.Music => new InputMessagesFilterMusic(),
            _ => new InputMessagesFilterPhotoVideo(),
        };
        var res = await _client.Messages_Search(peer.Info.ToInputPeer(), null, filter, offset_id: offsetId, limit: limit);
        return await Task.Run(() =>
        {
            res.CollectUsersChats(_users, _chats);
            return res.Messages.Where(m => m is Message && !IsHiddenMessage(m)).Select(MapMessage).ToList();
        });
    }

    // ===================================================================== Профили групп / каналов / ботов

    /// <summary>Участники группы (первые 200; для больших супергрупп Telegram может отдать не всех).</summary>
    public async Task<IReadOnlyList<PeerRef>> GetMembersAsync(PeerRef peer)
    {
        try
        {
            switch (peer.Info)
            {
                case Channel channel when !channel.IsChannel:
                    var res = await _client.Channels_GetParticipants(channel, new ChannelParticipantsRecent(), 0, 200, 0);
                    foreach (var (k, v) in res.users) _users[k] = v;
                    return res.participants.Select(p => _users.TryGetValue(p.UserId, out var u) ? PeerOf(u) : null)
                        .OfType<PeerRef>().Where(p => !IsIgnored(p)).ToList();
                case Chat chat:
                    var full = await _client.Messages_GetFullChat(chat.id);
                    full.CollectUsersChats(_users, _chats);
                    return ((full.full_chat as ChatFull)?.participants as ChatParticipants)?.participants
                        .Select(p => _users.TryGetValue(p.UserId, out var u) ? PeerOf(u) : null)
                        .OfType<PeerRef>().Where(p => !IsIgnored(p)).ToList() ?? [];
            }
        }
        catch (RpcException ex)
        {
            Logging.Write("members: " + ex.Message);
        }
        return [];
    }
}
