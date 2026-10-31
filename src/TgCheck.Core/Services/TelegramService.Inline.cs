using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

// Инлайн-боты: «@бот запрос» в поле ввода — бот отвечает списком результатов, выбранный отправляется в чат
// (сообщение помечается «с помощью @бот»).
public sealed partial class TelegramService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, InlineBot?> _inlineBots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Бот по @имени, если он умеет инлайн-режим (null — нет такого или не инлайн-бот).</summary>
    public async Task<InlineBot?> ResolveInlineBotAsync(string username)
    {
        if (_inlineBots.TryGetValue(username, out var known)) return known;
        InlineBot? bot = null;
        try
        {
            var resolved = await _client.Contacts_ResolveUsername(username);
            foreach (var (id, user) in resolved.users) _users[id] = user;
            if (resolved.User is { } u && u.IsBot && u.flags.HasFlag(User.Flags.has_bot_inline_placeholder) && !IsIgnored(u.id))
                bot = new InlineBot(PeerOf(u), u.bot_inline_placeholder ?? "");
        }
        catch (RpcException) { /* нет такого имени */ }
        _inlineBots[username] = bot;
        return bot;
    }

    /// <summary>Недавние инлайн-боты (как в Telegram — по частоте использования), до limit штук.</summary>
    public async Task<IReadOnlyList<InlineBot>> GetRecentInlineBotsAsync(int limit = 5)
    {
        try
        {
            if (await _client.Contacts_GetTopPeers(bots_inline: true, limit: limit) is not Contacts_TopPeers top) return [];
            top.CollectUsersChats(_users, _chats);
            return top.categories.SelectMany(c => c.peers)
                .Select(p => p.peer is PeerUser pu && top.users.TryGetValue(pu.user_id, out var u) ? u : null)
                .OfType<User>().Where(u => u.IsBot && !IsIgnored(u.id))
                .Select(u => new InlineBot(PeerOf(u), u.bot_inline_placeholder ?? ""))
                .Take(limit).ToList();
        }
        catch (Exception ex)
        {
            Logging.Write("inline top: " + ex.Message);
            return [];
        }
    }

    /// <summary>Результаты инлайн-бота на запрос (offset — следующая страница).</summary>
    public async Task<InlineResults> GetInlineResultsAsync(PeerRef chat, InlineBot bot, string query, string offset = "")
    {
        var res = await _client.Messages_GetInlineBotResults((User)bot.Peer.Info, chat.Info.ToInputPeer(), query, offset);
        foreach (var (id, user) in res.users) _users[id] = user;
        var items = res.results.Select(r => r switch
        {
            BotInlineMediaResult m => new InlineResult
            {
                Id = m.id, Type = m.type, Title = m.title, Description = m.description,
                Photo = m.photo as Photo, Document = m.document as Document,
            },
            BotInlineResult a => new InlineResult
            {
                Id = a.id, Type = a.type, Title = a.title, Description = a.description,
                ThumbUrl = a.thumb?.Url ?? (a.type == "photo" ? a.content?.Url : null),
            },
            _ => null,
        }).OfType<InlineResult>().ToList();
        return new InlineResults(res.query_id, items, res.next_offset, res.flags.HasFlag(Messages_BotResults.Flags.gallery));
    }

    /// <summary>Отправить выбранный результат инлайн-бота в чат.</summary>
    public async Task SendInlineResultAsync(PeerRef chat, long queryId, string resultId, int? replyToId = null)
    {
        EnsureNotIgnored(chat);
        await _client.Messages_SendInlineBotResult(chat.Info.ToInputPeer(), WTelegram.Helpers.RandomLong(), queryId, resultId,
            reply_to: replyToId is { } r ? new InputReplyToMessage { reply_to_msg_id = r } : null);
    }
}
