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
        Messages_MessagesBase res;
        var key = item.PeerKey;
        if (key < -1_000_000_000_000)
        {
            if (!_chats.TryGetValue(-1_000_000_000_000 - key, out var chat) || chat is not Channel channel) return null;
            res = await _client.Channels_GetMessages(channel, new InputMessageID { id = item.Id }).ConfigureAwait(false);
        }
        else res = await _client.Messages_GetMessages(new InputMessageID { id = item.Id }).ConfigureAwait(false);
        await Task.Yield();
        res.CollectUsersChats(_users, _chats);
        foreach (var m in res.Messages.OfType<Message>())
            if (m.media is MessageMediaDocument { document: Document d } && d.id == mediaId) return d;
        return null;
    }
}
