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
            if (resolved.User is { } u && u.IsBot && u.flags.HasFlag(User.Flags.has_bot_inline_placeholder))
                bot = new InlineBot(PeerOf(u), u.bot_inline_placeholder ?? "");
            else
                Logging.Write($"inline bot @{username}: " + (resolved.User is not { } x ? "не пользователь" : !x.IsBot ? "не бот"
                    : !x.flags.HasFlag(User.Flags.has_bot_inline_placeholder) ? "без инлайн-режима — пропускается" : "скрыт"));
        }
        catch (RpcException ex) { Logging.Write($"inline bot @{username}: {ex.Message}"); }
        _inlineBots[username] = bot;
        return bot;
    }

    // ---- @упоминания в тексте: есть ли такое имя в Telegram (тогда оно — ссылка). Сначала среди известных чатов
    // и людей (без сети); иначе один запрос на имя за запуск, по одному и с паузой (Telegram строго ограничивает
    // поиск по имени). Упёрлись в ограничение — до конца запуска незнакомые имена остаются обычным текстом.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<bool>> _mentions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _mentionGate = new(1);
    private volatile bool _mentionFlood;

    /// <summary>Есть ли в Telegram пользователь, бот, канал или группа с таким @именем (скрытые — нет).</summary>
    public Task<bool> UsernameExistsAsync(string username) => _mentions.GetOrAdd(username, name => Task.Run(async () =>
    {
        bool Known(string n) =>
            _users.Values.Any(u => (string.Equals(u.MainUsername, n, StringComparison.OrdinalIgnoreCase)
                                                        || u.usernames?.Any(x => string.Equals(x.username, n, StringComparison.OrdinalIgnoreCase)) == true))
            || _chats.Values.Any(c => c is Channel ch && (string.Equals(ch.MainUsername, n, StringComparison.OrdinalIgnoreCase)
                                                         || ch.usernames?.Any(x => string.Equals(x.username, n, StringComparison.OrdinalIgnoreCase)) == true));
        if (Known(name)) return true;
        if (_mentionFlood) return false;
        await _mentionGate.WaitAsync();
        try
        {
            if (Known(name)) return true;
            var resolved = await _client.Contacts_ResolveUsername(name);
            foreach (var (k, v) in resolved.users) _users[k] = v;
            foreach (var (k, v) in resolved.chats) _chats[k] = v;
            return resolved.UserOrChat is not null;
        }
        catch (RpcException ex)
        {
            if (ex.Code == 420) _mentionFlood = true; // FLOOD_WAIT — больше не спрашиваем
            return false;
        }
        catch (Exception) { _mentions.TryRemove(name, out _); return false; } // нет связи — спросим в другой раз
        finally
        {
            _ = Task.Delay(400).ContinueWith(_ => _mentionGate.Release(), TaskScheduler.Default);
        }
    }));

    /// <summary>Недавние инлайн-боты (как в Telegram — по частоте использования), до limit штук.</summary>
    public async Task<IReadOnlyList<InlineBot>> GetRecentInlineBotsAsync(int limit = 5)
    {
        try
        {
            if (await _client.Contacts_GetTopPeers(bots_inline: true, limit: limit) is not Contacts_TopPeers top) return [];
            top.CollectUsersChats(_users, _chats);
            return top.categories.SelectMany(c => c.peers)
                .Select(p => p.peer is PeerUser pu && top.users.TryGetValue(pu.user_id, out var u) ? u : null)
                .OfType<User>().Where(u => u.IsBot)
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
        await SendMaybeDelayedAsync(chat, at => _client.Messages_SendInlineBotResult(chat.Info.ToInputPeer(), WTelegram.Helpers.RandomLong(), queryId, resultId,
            reply_to: replyToId is { } r ? new InputReplyToMessage { reply_to_msg_id = r } : null, schedule_date: at == default ? null : at));
    }
}
