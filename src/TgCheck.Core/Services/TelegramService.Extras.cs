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
            if (await _client.Stories_GetAllStories(state, next: state is not null) is not Stories_AllStories all) break;
            foreach (var (k, v) in all.users) _users[k] = v;
            foreach (var (k, v) in all.chats) _chats[k] = v;
            foreach (var ps in all.peer_stories)
            {
                if (IsIgnored(ps.peer) || PeerOf(ps.peer) is not { } peer) continue;
                var stories = ps.stories.OfType<StoryItem>().Select(MapStory).OfType<StoryInfo>().OrderBy(s => s.Id).ToList();
                if (stories.Count > 0) result.Add(new StoryPeer { Peer = peer, Stories = stories, MaxReadId = ps.max_read_id });
            }
            if (!all.flags.HasFlag(Stories_AllStories.Flags.has_more)) break;
            state = all.state;
        }
        return result.OrderByDescending(p => p.Peer.IsSelf).ThenByDescending(p => p.HasUnread).ToList();
    }

    private static StoryInfo? MapStory(StoryItem s) => s.media switch
    {
        MessageMediaPhoto { photo: Photo p } => new StoryInfo { Id = s.id, DateUtc = s.date, Caption = s.caption, Photo = p },
        MessageMediaDocument { document: Document d } => new StoryInfo
        {
            Id = s.id, DateUtc = s.date, Caption = s.caption, Document = d,
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

    public async Task<IReadOnlyList<StickerItem>> GetRecentStickersAsync()
    {
        var res = await _client.Messages_GetRecentStickers(0);
        return (res as Messages_RecentStickers)?.stickers.OfType<Document>().Select(ToSticker).ToList() ?? [];
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

    // ===================================================================== Файлы

    private static readonly HashSet<string> PhotoExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".webm", ".mkv" };

    /// <summary>
    /// Отправляет файлы. Фото/видео (если не asFiles) уходят альбомом до 10 штук, остальное — документами;
    /// подпись прикрепляется к первому сообщению.
    /// </summary>
    public async Task SendFilesAsync(PeerRef peer, IReadOnlyList<string> paths, string? caption, int? replyToId,
        bool asFiles, IProgress<(int Index, double Fraction)>? progress = null)
    {
        EnsureNotIgnored(peer);
        var input = peer.Info.ToInputPeer();
        var medias = new List<InputMedia>();
        for (var i = 0; i < paths.Count; i++)
        {
            var index = i;
            var path = paths[i];
            var file = await _client.UploadFileAsync(path, (done, total) => progress?.Report((index, total > 0 ? (double)done / total : 0)));
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

        var first = true;
        foreach (var chunk in medias.Chunk(10))
        {
            var text = first ? caption ?? "" : "";
            if (chunk.Length == 1) await _client.SendMessageAsync(input, text, chunk[0], first ? replyToId ?? 0 : 0);
            else await _client.SendAlbumAsync(input, chunk, text, first ? replyToId ?? 0 : 0);
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
            var full = await _client.Users_GetFullUser(user);
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

    /// <summary>Отправить текст кнопки клавиатуры бота (как обычное сообщение).</summary>
    public Task<MessageItem> SendKeyboardButtonAsync(PeerRef peer, BotButton button) => SendTextAsync(peer, button.Text);

    // ===================================================================== Комментарии к постам каналов

    /// <summary>Обсуждение поста: группа обсуждения, id исходного сообщения в ней и комментарии (по порядку).</summary>
    public async Task<(PeerRef Discussion, int TopId, IReadOnlyList<MessageItem> Comments)> GetCommentsAsync(PeerRef channel, int postId)
    {
        var discussion = await _client.Messages_GetDiscussionMessage(channel.Info.ToInputPeer(), postId);
        foreach (var (k, v) in discussion.users) _users[k] = v;
        foreach (var (k, v) in discussion.chats) _chats[k] = v;
        var top = discussion.messages.OfType<Message>().MinBy(m => m.id) ?? throw new PeerNotFoundException("Обсуждение недоступно.");
        var group = PeerOf(top.peer_id) ?? throw new PeerNotFoundException("Обсуждение недоступно.");
        var replies = await _client.Messages_GetReplies(channel.Info.ToInputPeer(), postId, limit: 100);
        var items = await Task.Run(() => MapHistory(replies));
        return (group, top.id, items);
    }

    public async Task<MessageItem> SendCommentAsync(PeerRef discussion, int topId, string text) =>
        MapMessage(await _client.SendMessageAsync(discussion.Info.ToInputPeer(), text, reply_to_msg_id: topId));

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
            _customEmojiQueue.Add(id);
            if (!_customEmojiScheduled)
            {
                _customEmojiScheduled = true;
                _ = FlushCustomEmojiAsync();
            }
            return tcs.Task;
        }
    }

    private async Task FlushCustomEmojiAsync()
    {
        await Task.Delay(40);
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
            var docs = (await _client.Messages_GetCustomEmojiDocuments(batch)).OfType<Document>().ToDictionary(d => d.id);
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
