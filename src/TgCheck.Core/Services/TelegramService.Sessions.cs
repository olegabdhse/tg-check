using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

// Активные сеансы: где выполнен вход в аккаунт, завершить один или все, кроме этого.
public sealed partial class TelegramService
{
    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync()
    {
        var res = await _client.Account_GetAuthorizations().ConfigureAwait(false);
        return res.authorizations.Select(a => new SessionInfo
        {
            Hash = a.hash,
            IsCurrent = a.flags.HasFlag(Authorization.Flags.current),
            IsOfficialApp = a.flags.HasFlag(Authorization.Flags.official_app),
            IsPending = a.flags.HasFlag(Authorization.Flags.password_pending),
            Device = a.device_model ?? "",
            Platform = a.platform ?? "",
            SystemVersion = a.system_version ?? "",
            AppName = a.app_name ?? "",
            AppVersion = a.app_version ?? "",
            Ip = a.ip ?? "",
            Country = a.country ?? "",
            Region = a.region ?? "",
            CreatedUtc = a.date_created,
            ActiveUtc = a.date_active,
        }).OrderByDescending(s => s.IsCurrent).ThenByDescending(s => s.ActiveUtc).ToList();
    }

    /// <summary>Завершить сеанс на другом устройстве.</summary>
    public Task TerminateSessionAsync(long hash) => _client.Account_ResetAuthorization(hash);

    /// <summary>Завершить все сеансы, кроме этого.</summary>
    public Task TerminateOtherSessionsAsync() => _client.Auth_ResetAuthorizations();
}

// Ответы на истории: сама история (для миниатюры над текстом ответа).
public sealed partial class TelegramService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long, int), Task<StoryInfo?>> _replyStories = new();

    /// <summary>История, на которую ответили (null — истекла, удалена или скрыта).</summary>
    public Task<StoryInfo?> GetStoryAsync(long peerKey, int storyId) => _replyStories.GetOrAdd((peerKey, storyId), _ => Task.Run(async () =>
    {
        InputPeer? input = null;
        if (peerKey == _client.UserId) input = InputPeer.Self;
        else if (peerKey > 0 && _users.TryGetValue(peerKey, out var u)) input = u;
        else if (peerKey < -1_000_000_000_000 && _chats.TryGetValue(-1_000_000_000_000 - peerKey, out var ch)) input = ch;
        else if (peerKey < 0 && _chats.TryGetValue(-peerKey, out var c)) input = c;
        if (input is null) return null;
        try
        {
            var res = await _client.Stories_GetStoriesByID(input, storyId).ConfigureAwait(false);
            await Task.Yield();
            return res.stories.OfType<StoryItem>().Select(MapStory).OfType<StoryInfo>().FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logging.Write($"reply story: {ex.Message}");
            _replyStories.TryRemove((peerKey, storyId), out Task<StoryInfo?>? _); // нет связи — спросим в другой раз
            return null;
        }
    }));
}

// Непросмотренные реакции на ваши сообщения: какие сообщения (по порядку) и отметка «просмотрено».
public sealed partial class TelegramService
{
    /// <summary>Сообщения с непросмотренными реакциями, от старых к новым.</summary>
    public async Task<IReadOnlyList<int>> GetUnreadReactionIdsAsync(PeerRef peer)
    {
        var res = await _client.Messages_GetUnreadReactions(peer.Info.ToInputPeer(), 0, 0, 100, 0, 0).ConfigureAwait(false);
        await Task.Yield();
        res.CollectUsersChats(_users, _chats);
        return res.Messages.Select(m => m.ID).OrderBy(id => id).ToList();
    }

    /// <summary>Реакции на сообщение просмотрены (у сообщения — как прочитанное содержимое).</summary>
    public Task MarkReactionSeenAsync(PeerRef peer, int messageId) => ReadContentsAsync(peer, messageId);

    /// <summary>Все реакции в чате просмотрены.</summary>
    public async Task ReadAllReactionsAsync(PeerRef peer)
    {
        try { await _client.Messages_ReadReactions(peer.Info.ToInputPeer()).ConfigureAwait(false); }
        catch (Exception ex) { Logging.Write("read reactions: " + ex.Message); }
    }
}
