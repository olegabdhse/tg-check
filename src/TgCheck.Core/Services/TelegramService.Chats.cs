using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

/// <summary>Профиль/чат скрыт списком игнорирования или не найден.</summary>
public sealed class PeerNotFoundException(string message = "Пользователь не найден.") : Exception(message);

// Чаты: список диалогов, история, отправка/изменение/удаление, события в реальном времени.
public sealed partial class TelegramService
{
    private readonly ConcurrentDictionary<long, User> _users = new();
    private readonly ConcurrentDictionary<long, ChatBase> _chats = new();
    private readonly ConcurrentDictionary<long, DateTime> _typingSent = new();

    /// <summary>Новое сообщение (входящее или отправленное с другого устройства).</summary>
    public event Action<MessageItem>? MessageReceived;
    public event Action<MessageItem>? MessageEdited;
    /// <summary>Реакции под сообщением изменились (peerKey, id, реакции).</summary>
    public event Action<long, int, IReadOnlyList<ReactionInfo>>? ReactionsUpdated;
    /// <summary>Удалены сообщения: ключ чата (null — личные/обычные группы, id глобальные) и id.</summary>
    public event Action<long?, int[]>? MessagesDeleted;
    /// <summary>Прочитано: ключ чата, max id, outbox (собеседник прочитал наши) / inbox (мы прочитали на другом устройстве).</summary>
    public event Action<long, int, bool>? HistoryRead;
    /// <summary>Кто-то печатает: ключ чата, кто печатает.</summary>
    public event Action<long, PeerRef>? UserTyping;
    public event Action<long, Presence, DateTime?>? UserStatusChanged;

    public long SelfKey => _client.UserId;

    // ===================================================================== Игнорирование

    private bool IsIgnored(long userId) => _env.IgnoredUserIds.Contains(userId);
    private bool IsIgnored(Peer peer) => peer is PeerUser u && IsIgnored(u.user_id);
    public bool IsIgnored(PeerRef peer) => peer.IsUser && IsIgnored(peer.Id);

    private void EnsureNotIgnored(PeerRef peer)
    {
        if (IsIgnored(peer)) throw new PeerNotFoundException();
    }

    private bool IsHiddenMessage(MessageBase m) =>
        IsIgnored(m.Peer) || (m.From is { } f && IsIgnored(f));

    // ===================================================================== Пиры

    internal PeerRef PeerOf(IPeerInfo info) => new(info, _client.UserId);

    private PeerRef? PeerOf(Peer? peer) => peer switch
    {
        PeerUser u when _users.TryGetValue(u.user_id, out var user) => PeerOf(user),
        PeerChat c when _chats.TryGetValue(c.chat_id, out var chat) => PeerOf(chat),
        PeerChannel c when _chats.TryGetValue(c.channel_id, out var chat) => PeerOf(chat),
        _ => null,
    };

    /// <summary>
    /// Находит профиль по @username, ссылке t.me/…, tg://resolve?domain=…, tg://user?id=… или числовому ID.
    /// </summary>
    public async Task<PeerRef> ResolveAsync(string query)
    {
        var q = query.Trim();
        var idMatch = Regex.Match(q, @"^(?:tg://user\?id=)?(\d{4,})$", RegexOptions.IgnoreCase);
        if (idMatch.Success)
        {
            var id = long.Parse(idMatch.Groups[1].Value);
            if (IsIgnored(id)) throw new PeerNotFoundException();
            if (_users.TryGetValue(id, out var known)) return PeerOf(known);
            if (_chats.TryGetValue(id, out var chat)) return PeerOf(chat);
            try
            {
                var users = await _client.Users_GetUsers(new InputUser(id, 0));
                if (users.FirstOrDefault() is User u) { _users[u.id] = u; return PeerOf(u); }
            }
            catch (RpcException) { /* без access_hash Telegram не отдаёт незнакомых */ }
            throw new PeerNotFoundException("По ID удаётся открыть только тех, кто есть в ваших чатах или контактах. Попробуйте @username.");
        }

        var m = Regex.Match(q, @"^(?:@|(?:https?://)?(?:t\.me|telegram\.me)/|tg://resolve\?domain=)?([A-Za-z][A-Za-z0-9_]{3,31})(?:[/?&].*)?$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new PeerNotFoundException("Введите @username, ссылку t.me/… или ID.");
        try
        {
            var resolved = await _client.Contacts_ResolveUsername(m.Groups[1].Value);
            foreach (var (k, v) in resolved.users) _users[k] = v;
            foreach (var (k, v) in resolved.chats) _chats[k] = v;
            var info = resolved.UserOrChat ?? throw new PeerNotFoundException();
            var peer = PeerOf(info);
            EnsureNotIgnored(peer);
            return peer;
        }
        catch (RpcException ex) when (ex.Message is "USERNAME_NOT_OCCUPIED" or "USERNAME_INVALID")
        {
            throw new PeerNotFoundException();
        }
    }

    private async Task<ProfileInfo> GetChatProfileAsync(PeerRef peer, bool withAvatar)
    {
        var full = await _client.GetFullChat(peer.Info.ToInputPeer());
        full.CollectUsersChats(_users, _chats);
        var f = full.full_chat;
        byte[]? avatar = null;
        if (withAvatar && peer.HasPhoto)
        {
            try
            {
                using var ms = new MemoryStream();
                await _client.DownloadProfilePhotoAsync(peer.Info, ms, big: true);
                avatar = ms.Length > 0 ? ms.ToArray() : null;
            }
            catch (Exception ex) { Logging.Write("chat avatar: " + ex.Message); }
        }
        var info = (full.chats.TryGetValue(f.ID, out var ch) ? ch : null) as Channel;
        return new ProfileInfo
        {
            Peer = peer,
            IsSelf = false,
            Kind = peer.Kind,
            Id = peer.Id,
            FirstName = peer.Title,
            Username = peer.Username,
            ExtraUsernames = info?.ActiveUsernames?.Where(n => n != peer.Username).ToList() ?? [],
            Bio = string.IsNullOrWhiteSpace(f.About) ? null : f.About,
            MembersCount = f.ParticipantsCount > 0 ? f.ParticipantsCount : (info?.participants_count > 0 ? info.participants_count : null),
            OnlineCount = (f as ChannelFull)?.online_count is > 0 and var online ? online : null,
            AdminsCount = (f as ChannelFull)?.admins_count is > 0 and var admins ? admins : null,
            InviteLink = (f.ExportedInvite as ChatInviteExported)?.link,
            LinkedChatTitle = (f as ChannelFull)?.linked_chat_id is > 0 and var linked && full.chats.TryGetValue(linked, out var lc) ? lc.Title : null,
            IsSupergroup = info is { IsChannel: false },
            IsVerified = peer.IsVerified,
            GiftsCount = (f as ChannelFull)?.stargifts_count is > 0 and var g ? g : null,
            Avatar = avatar,
        };
    }

    // ===================================================================== Список чатов

    /// <summary>Диалоги основной папки или архива. Игнорируемые скрыты.</summary>
    public async Task<IReadOnlyList<ChatSummary>> GetDialogsAsync(bool archived)
    {
        var dialogs = await _client.Messages_GetAllDialogs(archived ? 1 : 0);
        // Разбор и шифрованное сохранение кэша — не в потоке интерфейса (архив бывает на тысячи чатов).
        return await Task.Run(() =>
        {
            _cache.Save($"dialogs_{(archived ? 1 : 0)}", dialogs);
            return MapDialogs(dialogs, archived);
        });
    }

    /// <summary>Диалоги из локального кэша (мгновенно при запуске), null — кэша нет.</summary>
    public IReadOnlyList<ChatSummary>? GetCachedDialogs(bool archived) =>
        _cache.Load<Messages_DialogsBase>($"dialogs_{(archived ? 1 : 0)}") is { } cached ? MapDialogs(cached, archived) : null;

    private List<ChatSummary> MapDialogs(Messages_DialogsBase dialogs, bool archived)
    {
        var result = new List<ChatSummary>();
        var folder = archived ? 1 : 0;
        {
            dialogs.CollectUsersChats(_users, _chats);
            var lastById = dialogs.Messages
                .GroupBy(m => (PeerRef.KeyOf(m.Peer), m.ID))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var d in dialogs.Dialogs.OfType<Dialog>())
            {
                if (IsIgnored(d.peer) || PeerOf(d.peer) is not { } peer) continue;
                if (peer.Info is Channel { flags: var cf } && cf.HasFlag(Channel.Flags.left)) continue;
                // Старые группы, превращённые в супергруппы, и деактивированные — как в официальном клиенте, не показываем.
                if (peer.Info is Chat basic && (basic.migrated_to is not null || basic.flags.HasFlag(Chat.Flags.deactivated))) continue;

                var key = PeerRef.KeyOf(d.peer);
                lastById.TryGetValue((key, d.top_message), out var last);
                result.Add(new ChatSummary
                {
                    Peer = peer,
                    Last = last is not null and not MessageEmpty && !IsHiddenMessage(last) ? MapMessage(last) : null,
                    UnreadCount = d.unread_count,
                    UnreadMentions = d.unread_mentions_count,
                    ReadInboxMaxId = d.read_inbox_max_id,
                    ReadOutboxMaxId = d.read_outbox_max_id,
                    IsPinned = d.flags.HasFlag(Dialog.Flags.pinned),
                    IsMarkedUnread = d.flags.HasFlag(Dialog.Flags.unread_mark),
                    IsMuted = d.notify_settings?.mute_until > DateTime.UtcNow,
                    IsArchived = folder == 1,
                });
            }
        }
        return result;
    }

    /// <summary>Глобальный поиск людей/групп/каналов по имени или username.</summary>
    public async Task<IReadOnlyList<PeerRef>> SearchPeersAsync(string query)
    {
        if (query.Trim().Length < 2) return [];
        var found = await _client.Contacts_Search(query.Trim(), 20);
        found.CollectUsersChats(_users, _chats);
        return found.my_results.Concat(found.results)
            .Where(p => !IsIgnored(p))
            .Select(PeerOf).OfType<PeerRef>()
            .DistinctBy(p => p.Key)
            .ToList();
    }

    // ===================================================================== Сообщения

    /// <summary>История: сообщения старше offsetId (0 — самые новые), в хронологическом порядке.</summary>
    public async Task<IReadOnlyList<MessageItem>> GetHistoryAsync(PeerRef peer, int offsetId = 0, int limit = 50)
    {
        EnsureNotIgnored(peer);
        var res = await _client.Messages_GetHistory(peer.Info.ToInputPeer(), offset_id: offsetId, limit: limit);
        return await Task.Run(() =>
        {
            if (offsetId == 0) _cache.Save($"history_{peer.Key}", res);
            return MapHistory(res);
        });
    }

    /// <summary>Последние сообщения чата из локального кэша (показываются сразу, пока грузится сеть).</summary>
    public IReadOnlyList<MessageItem>? GetCachedHistory(PeerRef peer) =>
        _cache.Load<Messages_MessagesBase>($"history_{peer.Key}") is { } cached ? MapHistory(cached) : null;

    private List<MessageItem> MapHistory(Messages_MessagesBase res)
    {
        res.CollectUsersChats(_users, _chats);
        return res.Messages.Where(m => m is not MessageEmpty && !IsHiddenMessage(m))
            .Select(MapMessage).Reverse().ToList();
    }

    /// <summary>Отдельные сообщения (например, для превью ответа).</summary>
    public async Task<IReadOnlyList<MessageItem>> GetMessagesAsync(PeerRef peer, params int[] ids)
    {
        var res = await _client.GetMessages(peer.Info.ToInputPeer(), ids.Select(i => (InputMessage)new InputMessageID { id = i }).ToArray());
        res.CollectUsersChats(_users, _chats);
        return res.Messages.Where(m => m is not MessageEmpty && !IsHiddenMessage(m)).Select(MapMessage).ToList();
    }

    public async Task<MessageItem> SendTextAsync(PeerRef peer, string text, int? replyToId = null)
    {
        EnsureNotIgnored(peer);
        var msg = await _client.SendMessageAsync(peer.Info.ToInputPeer(), text, reply_to_msg_id: replyToId ?? 0);
        return MapMessage(msg);
    }

    public async Task EditTextAsync(PeerRef peer, int messageId, string text)
    {
        var updates = await _client.Messages_EditMessage(peer.Info.ToInputPeer(), messageId, text);
        updates.CollectUsersChats(_users, _chats);
    }

    /// <param name="forEveryone">Удалить у всех (для каналов/супергрупп — всегда у всех).</param>
    public async Task DeleteAsync(PeerRef peer, int[] ids, bool forEveryone = true)
    {
        if (peer.Info is Channel channel) await _client.Channels_DeleteMessages(channel, ids);
        else await _client.Messages_DeleteMessages(ids, revoke: forEveryone);
    }

    public async Task MarkReadAsync(PeerRef peer, int maxId)
    {
        try { await _client.ReadHistory(peer.Info.ToInputPeer(), maxId); }
        catch (Exception ex) { Logging.Write("read: " + ex.Message); }
    }

    /// <summary>Показать собеседнику «печатает…» (не чаще раза в 5 секунд).</summary>
    public async Task NotifyTypingAsync(PeerRef peer)
    {
        var now = DateTime.UtcNow;
        if (_typingSent.TryGetValue(peer.Key, out var last) && now - last < TimeSpan.FromSeconds(5)) return;
        _typingSent[peer.Key] = now;
        try { await _client.Messages_SetTyping(peer.Info.ToInputPeer(), new SendMessageTypingAction()); }
        catch { /* не критично */ }
    }

    /// <summary>Скачивает фото/файл сообщения в поток.</summary>
    public async Task DownloadMediaAsync(MessageItem message, Stream output, IProgress<double>? progress = null)
    {
        WTelegram.Client.ProgressCallback? cb = progress is null ? null : (done, total) => progress.Report(total > 0 ? (double)done / total : 0);
        if (message.Photo is { } photo) await _client.DownloadFileAsync(photo, output, photo.LargestPhotoSize, cb);
        else if (message.Document is { } doc) await _client.DownloadFileAsync(doc, output, (PhotoSizeBase?)null, cb);
    }

    /// <summary>
    /// Обновить статусы «в сети» пачкой (Telegram присылает живые обновления не для всех) —
    /// для каждого изменившегося поднимается UserStatusChanged.
    /// </summary>
    public async Task RefreshPresenceAsync(IEnumerable<PeerRef> peers)
    {
        var users = peers.Select(p => p.Info).OfType<User>().Where(u => !u.IsBot && !IsIgnored(u.id)).DistinctBy(u => u.id).Take(100).ToArray();
        if (users.Length == 0) return;
        try
        {
            var fresh = await _client.Users_GetUsers(users.Select(u => (InputUserBase)u).ToArray());
            foreach (var u in fresh.OfType<User>())
            {
                var old = _users.TryGetValue(u.id, out var prev) ? MapStatus(prev.status) : (Presence.Unknown, null);
                _users[u.id] = u;
                var now = MapStatus(u.status);
                if (now != old) UserStatusChanged?.Invoke(u.id, now.Item1, now.Item2);
            }
        }
        catch (Exception ex) { Logging.Write("presence: " + ex.Message); }
    }

    /// <summary>Свежий статус пользователя (для шапки чата).</summary>
    public (Presence Presence, DateTime? LastSeen) GetPresence(PeerRef peer) =>
        _users.TryGetValue(peer.Id, out var u) && peer.IsUser ? MapStatus(u.status) : (Presence.Unknown, null);

    internal static (Presence, DateTime?) MapStatus(UserStatus? status) => status switch
    {
        UserStatusOnline => (Presence.Online, null),
        UserStatusOffline off => (Presence.Offline, off.was_online),
        UserStatusRecently => (Presence.Recently, null),
        UserStatusLastWeek => (Presence.LastWeek, null),
        UserStatusLastMonth => (Presence.LastMonth, null),
        _ => (Presence.Unknown, null),
    };

    // ===================================================================== Обновления в реальном времени

    private Task OnUpdatesAsync(UpdatesBase updates)
    {
        try
        {
            updates.CollectUsersChats(_users, _chats);
            foreach (var update in updates.UpdateList)
                HandleUpdate(update);
        }
        catch (Exception ex)
        {
            Logging.Write("updates: " + ex);
        }
        return Task.CompletedTask;
    }

    private void HandleUpdate(Update update)
    {
        switch (update)
        {
            case UpdateNewMessage { message: { } m } when m is not MessageEmpty && !IsHiddenMessage(m):
                MessageReceived?.Invoke(MapMessage(m));
                break;
            case UpdateEditMessage { message: { } m } when m is not MessageEmpty && !IsHiddenMessage(m):
                MessageEdited?.Invoke(MapMessage(m));
                break;
            case UpdateEditChannelMessage { message: { } m } when m is not MessageEmpty && !IsHiddenMessage(m):
                MessageEdited?.Invoke(MapMessage(m));
                break;
            case UpdateDeleteChannelMessages d:
                MessagesDeleted?.Invoke(-1_000_000_000_000 - d.channel_id, d.messages);
                break;
            case UpdateDeleteMessages d:
                MessagesDeleted?.Invoke(null, d.messages);
                break;
            case UpdateReadHistoryOutbox r:
                HistoryRead?.Invoke(PeerRef.KeyOf(r.peer), r.max_id, true);
                break;
            case UpdateReadHistoryInbox r:
                HistoryRead?.Invoke(PeerRef.KeyOf(r.peer), r.max_id, false);
                break;
            case UpdateReadChannelOutbox r:
                HistoryRead?.Invoke(-1_000_000_000_000 - r.channel_id, r.max_id, true);
                break;
            case UpdateReadChannelInbox r:
                HistoryRead?.Invoke(-1_000_000_000_000 - r.channel_id, r.max_id, false);
                break;
            case UpdateUserTyping t when !IsIgnored(t.user_id) && t.action is not SendMessageCancelAction
                                          && _users.TryGetValue(t.user_id, out var tu):
                UserTyping?.Invoke(t.user_id, PeerOf(tu));
                break;
            case UpdateChatUserTyping t when !IsIgnored(t.from_id) && t.action is not SendMessageCancelAction
                                              && PeerOf(t.from_id) is { } who:
                UserTyping?.Invoke(-t.chat_id, who);
                break;
            case UpdateChannelUserTyping t when !IsIgnored(t.from_id) && t.action is not SendMessageCancelAction
                                                 && PeerOf(t.from_id) is { } who2:
                UserTyping?.Invoke(-1_000_000_000_000 - t.channel_id, who2);
                break;
            case UpdateMessagePoll poll:
                OnPollUpdate(poll);
                break;
            case UpdateMessageReactions r when PeerOf(r.peer) is { } rp:
                ReactionsUpdated?.Invoke(rp.Key, r.msg_id, MapReactions(r.reactions));
                break;
            case UpdateUserStatus s when !IsIgnored(s.user_id):
                var (presence, seen) = MapStatus(s.status);
                if (_users.TryGetValue(s.user_id, out var su)) su.status = s.status;
                UserStatusChanged?.Invoke(s.user_id, presence, seen);
                break;
        }
    }

    // ===================================================================== Маппинг

    private MessageItem MapMessage(MessageBase m)
    {
        var from = m.From is { } f ? PeerOf(f) : null;
        var key = PeerRef.KeyOf(m.Peer);
        if (m is MessageService s)
        {
            return new MessageItem
            {
                Id = s.id,
                PeerKey = key,
                FromKey = s.from_id is { } sf ? PeerRef.KeyOf(sf) : null,
                From = from,
                DateUtc = s.date,
                IsOut = s.flags.HasFlag(MessageService.Flags.out_),
                ServiceText = ServiceText(s, from),
                // Служебные сообщения форума: «тема создана» — сама тема, остальные — тема, где они случились.
                TopicId = s.action is MessageActionTopicCreate ? s.id
                    : s.reply_to is MessageReplyHeader { flags: var sf2 } sh && sf2.HasFlag(MessageReplyHeader.Flags.forum_topic)
                        ? (sf2.HasFlag(MessageReplyHeader.Flags.has_reply_to_top_id) ? sh.reply_to_top_id : sh.reply_to_msg_id) : null,
            };
        }

        if (m is not Message msg)
            return new MessageItem { Id = m.ID, PeerKey = key, DateUtc = m.Date, ServiceText = "Сообщение недоступно" };
        var reply = (msg.reply_to as MessageReplyHeader)?.reply_to_msg_id;
        string? fwd = null;
        if (msg.fwd_from is { } fh)
            fwd = fh.from_name ?? (fh.from_id is { } fid ? PeerOf(fid)?.DisplayTitle : null) ?? "скрытого пользователя";

        var item = new MessageItem
        {
            Id = msg.id,
            PeerKey = key,
            FromKey = msg.from_id is { } mf ? PeerRef.KeyOf(mf) : (msg.flags.HasFlag(Message.Flags.post) ? key : PeerRef.KeyOf(msg.peer_id)),
            From = from ?? (msg.flags.HasFlag(Message.Flags.out_) ? null : PeerOf(msg.peer_id)),
            DateUtc = msg.date,
            Text = msg.message ?? "",
            IsOut = msg.flags.HasFlag(Message.Flags.out_),
            EditDateUtc = msg.flags.HasFlag(Message.Flags.has_edit_date) && !msg.flags.HasFlag(Message.Flags.edit_hide) ? msg.edit_date : null,
            ReplyToId = reply is > 0 ? reply : null,
            ThreadTopId = msg.reply_to is MessageReplyHeader { flags: var rf } rh && rf.HasFlag(MessageReplyHeader.Flags.has_reply_to_top_id) ? rh.reply_to_top_id : null,
            TopicId = msg.reply_to is MessageReplyHeader { flags: var tf } th && tf.HasFlag(MessageReplyHeader.Flags.forum_topic)
                ? (tf.HasFlag(MessageReplyHeader.Flags.has_reply_to_top_id) ? th.reply_to_top_id : th.reply_to_msg_id) : null,
            ForwardedFrom = fwd,
            PostAuthor = msg.post_author,
            Views = msg.views,
            GroupedId = msg.flags.HasFlag(Message.Flags.has_grouped_id) ? msg.grouped_id : 0,
            CustomEmoji = msg.entities?.OfType<MessageEntityCustomEmoji>()
                .Select(e => new CustomEmojiSpan(e.offset, e.length, e.document_id)).ToList() ?? [],
            InlineButtons = MapInline(msg.reply_markup as ReplyInlineMarkup),
            ReplyKeyboard = msg.reply_markup is ReplyKeyboardMarkup rk ? rk.rows.Select(r => (IReadOnlyList<BotButton>)r.buttons.Select(b => new BotButton { Text = b.text, Kind = BotButtonKind.Text }).ToList()).ToList() : null,
            KeyboardPlaceholder = (msg.reply_markup as ReplyKeyboardMarkup)?.placeholder,
            Comments = msg.replies is { } rep && rep.flags.HasFlag(MessageReplies.Flags.comments) ? rep.replies : null,
            Reactions = MapReactions(msg.reactions),
        };
        return WithMedia(item, msg.media);
    }

    private MessageItem WithMedia(MessageItem item, MessageMedia? media)
    {
        switch (media)
        {
            case MessageMediaPhoto { photo: Photo p }:
                var largest = p.sizes.OfType<PhotoSize>().MaxBy(s => s.w);
                return Clone(item, MediaKind.Photo, photo: p, w: largest?.w ?? 0, h: largest?.h ?? 0);
            case MessageMediaDocument { document: Document d } md:
                var attrs = d.attributes ?? [];
                var sticker = attrs.OfType<DocumentAttributeSticker>().FirstOrDefault();
                var video = attrs.OfType<DocumentAttributeVideo>().FirstOrDefault();
                var audio = attrs.OfType<DocumentAttributeAudio>().FirstOrDefault();
                var name = attrs.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name;
                var animated = attrs.OfType<DocumentAttributeAnimated>().Any();
                var size = attrs.OfType<DocumentAttributeImageSize>().FirstOrDefault();
                if (sticker is not null)
                    return Clone(item, MediaKind.Sticker, doc: d, emoji: sticker.alt, w: video?.w ?? size?.w ?? 512, h: video?.h ?? size?.h ?? 512);
                if (md.flags.HasFlag(MessageMediaDocument.Flags.voice) || audio?.flags.HasFlag(DocumentAttributeAudio.Flags.voice) == true)
                    return Clone(item, MediaKind.Voice, doc: d, title: "Голосовое сообщение", duration: audio?.duration ?? 0, waveform: DecodeWaveform(audio?.waveform));
                if (md.flags.HasFlag(MessageMediaDocument.Flags.round) || video?.flags.HasFlag(DocumentAttributeVideo.Flags.round_message) == true)
                    return Clone(item, MediaKind.VideoNote, doc: d, title: "Видеосообщение", duration: video?.duration ?? 0, w: 240, h: 240);
                if (animated)
                    return Clone(item, MediaKind.Gif, doc: d, w: video?.w ?? size?.w ?? 0, h: video?.h ?? size?.h ?? 0, title: "GIF", duration: video?.duration ?? 0);
                if (video is not null)
                    return Clone(item, MediaKind.Video, doc: d, w: video.w, h: video.h, title: name ?? "Видео", duration: video.duration);
                if (audio is not null)
                    return Clone(item, MediaKind.Audio, doc: d, duration: audio.duration,
                        title: Names.Join(audio.performer, audio.title) is { Length: > 0 } t ? t : name ?? "Аудио");
                return Clone(item, MediaKind.Document, doc: d, title: name ?? "Файл");
            case MessageMediaPoll poll:
                var mapped = MapPoll(poll.poll, poll.results);
                var clone = Clone(item, MediaKind.Poll, title: mapped.Question);
                clone.Poll = mapped;
                return clone;
            case MessageMediaContact c:
                return Clone(item, MediaKind.Contact, title: Names.Join(c.first_name, c.last_name) + (string.IsNullOrEmpty(c.phone_number) ? "" : $" · +{c.phone_number.TrimStart('+')}"));
            case MessageMediaGeo or MessageMediaGeoLive or MessageMediaVenue:
                var point = media switch { MessageMediaVenue v => v.geo, MessageMediaGeoLive l => l.geo, MessageMediaGeo g => g.geo, _ => null } as GeoPoint;
                var venue = media as MessageMediaVenue;
                var geoItem = Clone(item, MediaKind.Location, title: venue?.title ?? (media is MessageMediaGeoLive ? "Геопозиция в реальном времени" : "Геопозиция"));
                return point is null ? geoItem : new MessageItem
                {
                    Id = geoItem.Id, PeerKey = geoItem.PeerKey, FromKey = geoItem.FromKey, From = geoItem.From, DateUtc = geoItem.DateUtc,
                    Text = geoItem.Text, IsOut = geoItem.IsOut, EditDateUtc = geoItem.EditDateUtc, ReplyToId = geoItem.ReplyToId,
                    ForwardedFrom = geoItem.ForwardedFrom, PostAuthor = geoItem.PostAuthor, Views = geoItem.Views, GroupedId = geoItem.GroupedId,
                    CustomEmoji = geoItem.CustomEmoji, InlineButtons = geoItem.InlineButtons, Comments = geoItem.Comments, Reactions = geoItem.Reactions, Media = MediaKind.Location, MediaTitle = geoItem.MediaTitle,
                    Latitude = point.lat, Longitude = point.lon, GeoAccessHash = point.access_hash, VenueAddress = venue?.address,
                };
            case MessageMediaWebPage:
                return Clone(item, MediaKind.WebPage);
            case MessageMediaDice dice:
                return Clone(item, MediaKind.Other, title: $"{dice.emoticon} выпало {dice.value}");
            case MessageMediaStory:
                return Clone(item, MediaKind.Other, title: "Пересланная история");
            case MessageMediaGame game:
                return Clone(item, MediaKind.Other, title: "🎮 " + (game.game?.title ?? "Игра"));
            case MessageMediaInvoice inv:
                return Clone(item, MediaKind.Other, title: "🧾 " + inv.title);
            case MessageMediaGiveaway:
                return Clone(item, MediaKind.Other, title: "🎉 Розыгрыш");
            case MessageMediaPaidMedia paid:
                return Clone(item, MediaKind.Other, title: $"⭐ Платный контент · {paid.stars_amount} звёзд");
            case null:
                return item;
            default:
                return Clone(item, MediaKind.Other, title: "Сообщение этого типа пока не поддерживается");
        }
    }

    internal static PollInfo MapPoll(Poll? poll, PollResults? results)
    {
        var voters = results?.results?.ToDictionary(r => r.option, r => r) ?? [];
        var total = results?.total_voters ?? 0;
        var options = (poll?.answers ?? []).OfType<PollAnswer>().Select(a =>
        {
            voters.TryGetValue(a.option, out var r);
            var count = r?.voters ?? 0;
            return new PollOption
            {
                Text = a.text?.text ?? "",
                Option = a.option,
                Voters = count,
                Chosen = r?.flags.HasFlag(PollAnswerVoters.Flags.chosen) == true,
                Correct = r?.flags.HasFlag(PollAnswerVoters.Flags.correct) == true,
                Percent = total > 0 ? (double)count / total : 0,
            };
        }).ToList();
        var flags = poll?.flags ?? 0;
        return new PollInfo
        {
            Id = poll?.id ?? 0,
            Question = poll?.question?.text ?? "Опрос",
            Options = options,
            TotalVoters = total,
            IsClosed = flags.HasFlag(Poll.Flags.closed),
            IsQuiz = flags.HasFlag(Poll.Flags.quiz),
            IsMultiple = flags.HasFlag(Poll.Flags.multiple_choice),
            IsPublic = flags.HasFlag(Poll.Flags.public_voters),
            Solution = string.IsNullOrEmpty(results?.solution) ? null : results!.solution,
        };
    }

    /// <summary>Голосует в опросе (пустой список — отозвать голос) и возвращает обновлённые результаты.</summary>
    public async Task<PollInfo?> VotePollAsync(PeerRef peer, MessageItem message, IEnumerable<PollOption> options)
    {
        var updates = await _client.Messages_SendVote(peer.Info.ToInputPeer(), message.Id, options.Select(o => o.Option).ToArray());
        updates.CollectUsersChats(_users, _chats);
        var fresh = await GetMessagesAsync(peer, message.Id);
        return fresh.FirstOrDefault()?.Poll;
    }

    // Волна голосового: 5 бит на значение, упакованы подряд.
    private static IReadOnlyList<byte>? DecodeWaveform(byte[]? packed)
    {
        if (packed is not { Length: > 0 }) return null;
        var count = packed.Length * 8 / 5;
        var result = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var bit = i * 5;
            var value = packed[bit / 8] | (bit / 8 + 1 < packed.Length ? packed[bit / 8 + 1] << 8 : 0);
            result[i] = (byte)((value >> (bit % 8)) & 31);
        }
        return result;
    }

    private static MessageItem Clone(MessageItem i, MediaKind kind, Photo? photo = null, Document? doc = null,
        int w = 0, int h = 0, string? title = null, string? emoji = null, double duration = 0, IReadOnlyList<byte>? waveform = null) => new()
    {
        Id = i.Id, PeerKey = i.PeerKey, FromKey = i.FromKey, From = i.From, DateUtc = i.DateUtc, Text = i.Text,
        IsOut = i.IsOut, EditDateUtc = i.EditDateUtc, ReplyToId = i.ReplyToId, ThreadTopId = i.ThreadTopId, TopicId = i.TopicId, ForwardedFrom = i.ForwardedFrom,
        PostAuthor = i.PostAuthor, Views = i.Views, GroupedId = i.GroupedId, CustomEmoji = i.CustomEmoji,
        InlineButtons = i.InlineButtons, ReplyKeyboard = i.ReplyKeyboard, KeyboardPlaceholder = i.KeyboardPlaceholder, Comments = i.Comments, Reactions = i.Reactions,
        Media = kind, Photo = photo, Document = doc, MediaWidth = w, MediaHeight = h,
        MediaTitle = title, MediaSize = doc?.size ?? 0, StickerEmoji = emoji, DurationSeconds = duration, Waveform = waveform,
    };

    private string ServiceText(MessageService s, PeerRef? from)
    {
        var isOut = s.flags.HasFlag(MessageService.Flags.out_);
        var who = isOut ? "Вы" : from?.DisplayTitle ?? "Кто-то";
        string User(long id) => _users.TryGetValue(id, out var u) ? PeerOf(u).DisplayTitle : "пользователь";
        string Users(long[] ids) => string.Join(", ", ids.Select(User));
        string Peer(Peer? p) => p is null ? "" : PeerOf(p)?.DisplayTitle ?? "чат";
        string Money(long amount, string? currency) => $"{amount / 100m:0.##} {currency}";
        string Dur(int sec) => sec >= 3600 ? $"{sec / 3600} ч {sec % 3600 / 60} мин" : sec >= 60 ? $"{sec / 60} мин {sec % 60} с" : $"{sec} с";

        return s.action switch
        {
            MessageActionChatCreate c => $"{who} создал(а) группу «{c.title}»",
            MessageActionChannelCreate c => $"Канал «{c.title}» создан",
            MessageActionChatEditTitle t => $"{who} изменил(а) название на «{t.title}»",
            MessageActionChatEditPhoto => $"{who} обновил(а) фото группы",
            MessageActionChatDeletePhoto => $"{who} удалил(а) фото группы",
            MessageActionChatAddUser a => a.users.Length == 1 && from?.Id == a.users[0] ? $"{who} вступил(а) в группу" : $"{who} добавил(а) {Users(a.users)}",
            MessageActionChatDeleteUser d => from?.Id == d.user_id ? $"{who} покинул(а) группу" : $"{who} удалил(а) {User(d.user_id)}",
            MessageActionChatJoinedByLink l => $"{who} вступил(а) по ссылке-приглашению от {User(l.inviter_id)}",
            MessageActionChatJoinedByRequest => $"{who} принят(а) в группу по заявке",
            MessageActionChatJoinedViaCommunity => $"{who} вступил(а) через сообщество",
            MessageActionChatMigrateTo => "Группа преобразована в супергруппу",
            MessageActionChannelMigrateFrom m => $"Супергруппа создана из группы «{m.title}»",
            MessageActionPinMessage => $"{who} закрепил(а) сообщение",
            MessageActionHistoryClear => "История очищена",
            MessageActionPhoneCall call => (call.flags.HasFlag(MessageActionPhoneCall.Flags.video) ? "📹 " : "📞 ") +
                (call.reason is PhoneCallDiscardReasonMissed ? (isOut ? "Отменённый звонок" : "Пропущенный звонок")
                 : call.reason is PhoneCallDiscardReasonBusy ? "Звонок: занято"
                 : (isOut ? "Исходящий звонок" : "Входящий звонок") + (call.duration > 0 ? $" · {Dur(call.duration)}" : "")),
            MessageActionConferenceCall cc => "📞 Групповой звонок" + (cc.duration > 0 ? $" · {Dur(cc.duration)}" : ""),
            MessageActionGroupCall g => g.duration > 0 ? $"Видеочат завершён · {Dur(g.duration)}" : $"{who} начал(а) видеочат",
            MessageActionGroupCallScheduled g => $"Видеочат запланирован на {g.schedule_date.ToLocalTime():d MMM, HH:mm}",
            MessageActionInviteToGroupCall i => $"{who} пригласил(а) {Users(i.users)} в видеочат",
            MessageActionScreenshotTaken => $"{who} сделал(а) скриншот",
            MessageActionContactSignUp => $"{who} теперь в Telegram",
            MessageActionCustomAction c => c.message,
            MessageActionGameScore g => $"{who} набрал(а) {g.score} очк. в игре",
            MessageActionPaymentSent p => $"Оплачено {Money(p.total_amount, p.currency)}",
            MessageActionPaymentSentMe p => $"Получен платёж {Money(p.total_amount, p.currency)}",
            MessageActionPaymentRefunded p => $"Возврат {Money(p.total_amount, p.currency)}",
            MessageActionBotAllowed b => b.domain is { Length: > 0 } dmn ? $"Вы разрешили боту писать вам после входа на {dmn}" : "Вы разрешили боту писать вам",
            MessageActionSecureValuesSent or MessageActionSecureValuesSentMe => "Переданы данные Telegram Passport",
            MessageActionGeoProximityReached g => $"{Peer(g.from_id)} рядом с {Peer(g.to_id)} ({g.distance} м)",
            MessageActionSetMessagesTTL t => t.period > 0 ? $"{who} включил(а) автоудаление сообщений через {Dur(t.period)}" : $"{who} отключил(а) автоудаление сообщений",
            MessageActionSetChatTheme => $"{who} изменил(а) тему чата",
            MessageActionSetChatWallPaper => $"{who} изменил(а) обои чата",
            MessageActionWebViewDataSent w => $"Данные из «{w.text}» отправлены боту",
            MessageActionGiftPremium g => $"🎁 {who} подарил(а) Telegram Premium на {Math.Max(1, g.days / 30)} мес.",
            MessageActionGiftCode g => $"🎁 Подарочный код Premium на {Math.Max(1, g.days / 30)} мес.",
            MessageActionGiftStars g => $"⭐ {who} подарил(а) {g.stars} звёзд",
            MessageActionGiftTon g => $"💎 {who} подарил(а) {g.crypto_amount / 1_000_000_000m:0.##} TON",
            MessageActionPrizeStars p => $"⭐ Выигрыш в розыгрыше: {p.stars} звёзд",
            MessageActionGiveawayLaunch => $"🎉 {who} запустил(а) розыгрыш",
            MessageActionGiveawayResults r => $"🎉 Итоги розыгрыша: победителей — {r.winners_count}",
            MessageActionBoostApply b => $"{who} усилил(а) группу" + (b.boosts > 1 ? $" ×{b.boosts}" : ""),
            MessageActionStarGift g => $"🎁 {who} отправил(а) подарок «{g.gift?.Title ?? "Подарок"}»" + (g.convert_stars > 0 ? $" · {g.convert_stars} ⭐" : ""),
            MessageActionStarGiftUnique u => $"🎁 Уникальный подарок «{u.gift?.Title}»",
            MessageActionStarGiftPurchaseOffer => "🎁 Предложение о покупке подарка",
            MessageActionStarGiftPurchaseOfferDeclined => "🎁 Предложение о покупке подарка отклонено",
            MessageActionTopicCreate t => $"{who} создал(а) тему «{t.title}»",
            MessageActionTopicEdit t => t.title is { Length: > 0 } tt ? $"{who} переименовал(а) тему в «{tt}»" : $"{who} изменил(а) тему",
            MessageActionSuggestProfilePhoto => $"{who} предлагает новое фото профиля",
            MessageActionSuggestBirthday => $"{who} предлагает указать день рождения",
            MessageActionRequestedPeer or MessageActionRequestedPeerSentMe => $"{who} поделился(ась) чатом с ботом",
            MessageActionPaidMessagesPrice p => p.stars > 0 ? $"Платные сообщения: {p.stars} ⭐ за сообщение" : "Платные сообщения отключены",
            MessageActionPaidMessagesRefunded r => $"Возвращено {r.stars} ⭐ за {r.count} сообщ.",
            MessageActionNoForwardsToggle t => t.new_value ? $"{who} запретил(а) пересылку" : $"{who} разрешил(а) пересылку",
            MessageActionTodoAppendTasks => $"{who} добавил(а) задачи в список",
            MessageActionTodoCompletions => $"{who} отметил(а) задачи",
            MessageActionPollAppendAnswer => $"{who} добавил(а) вариант в опрос",
            MessageActionChangeCreator c => $"Новый владелец: {User(c.new_creator_id)}",
            MessageActionManagedBotCreated b => $"Создан бот {User(b.bot_id)}",
            { } other => Humanize(other.GetType().Name),
            _ => "Служебное сообщение",
        };
    }

    // Неизвестное действие: «MessageActionSomethingNew» → «Something new».
    private static string Humanize(string typeName)
    {
        var name = typeName.Replace("MessageAction", "");
        var words = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        return words.Length == 0 ? "Служебное сообщение" : char.ToUpper(words[0]) + words[1..];
    }
}
