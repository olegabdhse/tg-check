using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

// Свежие ссылки на файлы: Telegram выдаёт их на время, и у сообщений из кэша (или давно открытого профиля)
// они устаревают — тогда файл не качается (FILE_REFERENCE_EXPIRED). Берём сообщение или профиль заново.
public sealed partial class TelegramService
{
    private async Task<Document?> RefreshDocumentAsync(MediaHolder holder)
    {
        // История: запросить её заново (истории из кэша — с устаревшими ссылками на видео).
        if (holder is StoryInfo { Owner: { } owner } story)
        {
            var stories = await _client.Stories_GetStoriesByID(owner.Info.ToInputPeer(), story.Id).ConfigureAwait(false);
            await Task.Yield();
            return stories.stories.OfType<StoryItem>().Select(s => s.media).OfType<MessageMediaDocument>()
                .Select(m => m.document).OfType<Document>().FirstOrDefault(d => d.id == story.MediaId);
        }
        if (holder is not MessageItem item) return null;
        var mediaId = item.MediaId;

        // Музыка профиля (не сообщение): профиль заново — в нём песня со свежей ссылкой.
        if (item.Id == 0)
        {
            // Профиль только что загружен (минуты назад) — свежая ссылка уже есть, лишний запрос не нужен.
            if (_freshFull.TryGetValue(item.PeerKey, out var recent) && DateTime.UtcNow - recent.At < TimeSpan.FromMinutes(20)
                && recent.Full.full_user.saved_music is Document recentSong && recentSong.id == mediaId
                && !recentSong.file_reference.AsSpan().SequenceEqual(item.FileReference))
                return recentSong;
            if (!_users.TryGetValue(item.PeerKey, out var user)) return null;
            var full = await _client.Users_GetFullUser(user).ConfigureAwait(false);
            await Task.Yield();
            full.CollectUsersChats(_users, _chats);
            return full.full_user.saved_music is Document song && song.id == mediaId ? song : null;
        }

        // Сообщение заново.
        var fresh = await RefetchMessageAsync(item.PeerKey, item.Id);
        return fresh?.media switch
        {
            MessageMediaDocument { document: Document d } when d.id == mediaId => d,
            MessageMediaWebPage { webpage: WebPage { document: Document wd } } when wd.id == mediaId => wd,
            _ => null,
        };
    }

    /// <summary>Свежая ссылка на фото сообщения (превью и полное фото из кэша истории).</summary>
    private async Task<Photo?> RefreshPhotoAsync(MediaHolder holder)
    {
        // Фото-история из кэша списка историй — запросить историю заново.
        if (holder is StoryInfo { Owner: { } owner } story)
        {
            var stories = await _client.Stories_GetStoriesByID(owner.Info.ToInputPeer(), story.Id).ConfigureAwait(false);
            await Task.Yield();
            return stories.stories.OfType<StoryItem>().Select(s => s.media).OfType<MessageMediaPhoto>()
                .Select(m => m.photo).OfType<Photo>().FirstOrDefault(p => p.id == story.MediaId);
        }
        if (holder is not MessageItem { Id: > 0 } item) return null;
        var mediaId = item.MediaId;
        var fresh = await RefetchMessageAsync(item.PeerKey, item.Id);
        return fresh?.media switch
        {
            MessageMediaPhoto { photo: Photo p } when p.id == mediaId => p,
            MessageMediaWebPage { webpage: WebPage { photo: Photo wp } } when wp.id == mediaId => wp,
            _ => null,
        };
    }

    // Сообщения заново — пачкой по чату: после очистки кэша (или в давно открытом чате) устаревают ссылки
    // сразу у десятков превью; по запросу на каждое Telegram начал бы притормаживать.
    private readonly Dictionary<long, (HashSet<int> Ids, Task<Dictionary<int, Message>> Task)> _refetch = [];

    private async Task<Message?> RefetchMessageAsync(long peerKey, int id)
    {
        Task<Dictionary<int, Message>> batch;
        lock (_refetch)
        {
            if (_refetch.TryGetValue(peerKey, out var open) && open.Ids.Count < 100) { open.Ids.Add(id); batch = open.Task; }
            else
            {
                var ids = new HashSet<int> { id };
                batch = Task.Run(async () =>
                {
                    await Task.Delay(120); // собираем запросы, пришедшие следом
                    int[] snapshot;
                    lock (_refetch)
                    {
                        snapshot = [.. ids];
                        _refetch.Remove(peerKey);
                    }
                    var input = snapshot.Select(i => (InputMessage)new InputMessageID { id = i }).ToArray();
                    Messages_MessagesBase res;
                    if (peerKey < -1_000_000_000_000)
                    {
                        if (!_chats.TryGetValue(-1_000_000_000_000 - peerKey, out var chat) || chat is not Channel channel) return [];
                        res = await _client.Channels_GetMessages(channel, input).ConfigureAwait(false);
                    }
                    else res = await _client.Messages_GetMessages(input).ConfigureAwait(false);
                    await Task.Yield();
                    res.CollectUsersChats(_users, _chats);
                    return res.Messages.OfType<Message>().ToDictionary(m => m.id);
                });
                _refetch[peerKey] = (ids, batch);
            }
        }
        try { return (await batch).GetValueOrDefault(id); }
        catch (Exception ex)
        {
            Logging.Write("refetch messages: " + ex.Message);
            return null;
        }
    }
}
