using System.Text.RegularExpressions;
using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

public enum TgLinkKind { Peer, Invite, StickerSet }

/// <summary>Ссылка Telegram, разобранная для открытия внутри приложения.</summary>
public sealed record TgLink(TgLinkKind Kind)
{
    /// <summary>Чат, канал, пользователь или бот (Peer) — и сообщение в нём, если ссылка на сообщение.</summary>
    public PeerRef? Peer { get; init; }
    public int? MessageId { get; init; }
    /// <summary>Приглашение, в котором ещё не состоим: хеш, название, участники.</summary>
    public string? InviteHash { get; init; }
    public string? InviteTitle { get; init; }
    public int InviteMembers { get; init; }
    public bool InviteIsChannel { get; init; }
    /// <summary>Набор стикеров.</summary>
    public (StickerSetInfo Info, IReadOnlyList<StickerItem> Stickers, bool Installed)? StickerPack { get; init; }
}

// Ссылки t.me / telegram.me / tg:// — открываются внутри приложения, как в официальном клиенте:
// пользователь, бот, канал, группа, сообщение (t.me/канал/123, t.me/c/id/123), приглашение (t.me/+…), набор стикеров.
public sealed partial class TelegramService
{
    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?:t\.me|telegram\.me|telegram\.dog)/(?<path>[^?#]*)(?:\?(?<query>[^#]*))?", RegexOptions.IgnoreCase)]
    private static partial Regex TmeRx();

    [GeneratedRegex(@"^tg://(?<action>\w+)\?(?<query>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TgSchemeRx();

    /// <summary>Похоже ли на ссылку Telegram (её стоит открывать внутри приложения).</summary>
    public static bool IsTelegramLink(string url) => TmeRx().IsMatch(url.Trim()) || TgSchemeRx().IsMatch(url.Trim());

    /// <summary>Разобрать ссылку. null — это не ссылка Telegram или открыть её внутри нельзя (тогда — в браузере).</summary>
    public async Task<TgLink?> ParseLinkAsync(string url)
    {
        url = url.Trim();
        string? username = null, invite = null, stickers = null;
        long? channelId = null;
        int? messageId = null;

        if (TmeRx().Match(url) is { Success: true } tme)
        {
            var parts = tme.Groups["path"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            if (parts[0].StartsWith('+')) invite = parts[0][1..];
            else if (parts[0].Equals("joinchat", StringComparison.OrdinalIgnoreCase) && parts.Length > 1) invite = parts[1];
            else if (parts[0].Equals("addstickers", StringComparison.OrdinalIgnoreCase) && parts.Length > 1) stickers = parts[1];
            else if (parts[0].Equals("c", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 && long.TryParse(parts[1], out var cid))
            {
                channelId = cid;
                if (parts.Length > 2 && int.TryParse(parts[^1], out var cm)) messageId = cm;
            }
            else if (parts[0].Equals("s", StringComparison.OrdinalIgnoreCase) && parts.Length > 1) username = parts[1]; // t.me/s/канал — веб-лента
            else
            {
                username = parts[0];
                if (parts.Length > 1 && int.TryParse(parts[^1], out var m)) messageId = m;
            }
        }
        else if (TgSchemeRx().Match(url) is { Success: true } tg)
        {
            var q = System.Web.HttpUtility.ParseQueryString(tg.Groups["query"].Value);
            switch (tg.Groups["action"].Value.ToLowerInvariant())
            {
                case "resolve":
                    username = q["domain"];
                    if (int.TryParse(q["post"], out var post)) messageId = post;
                    break;
                case "join": invite = q["invite"]; break;
                case "addstickers": stickers = q["set"]; break;
                case "privatepost":
                    if (long.TryParse(q["channel"], out var c)) channelId = c;
                    if (int.TryParse(q["post"], out var p)) messageId = p;
                    break;
                default: return null;
            }
        }
        else return null;

        if (invite is { Length: > 0 }) return await ParseInviteAsync(invite);
        if (stickers is { Length: > 0 })
        {
            var res = await _client.Messages_GetStickerSet(new InputStickerSetShortName { short_name = stickers }, 0);
            var set = res.set;
            var info = new StickerSetInfo { Id = set.id, AccessHash = set.access_hash, Title = set.title, Count = set.count };
            return new TgLink(TgLinkKind.StickerSet)
            {
                StickerPack = (info, res.documents.OfType<Document>().Select(ToSticker).ToList(), set.flags.HasFlag(StickerSet.Flags.has_installed_date)),
            };
        }
        if (channelId is { } id)
        {
            // Приватная ссылка на сообщение: открывается, только если канал/группа уже есть у нас.
            if (!_chats.TryGetValue(id, out var chat) || IsIgnored(-1_000_000_000_000 - id)) return null;
            return new TgLink(TgLinkKind.Peer) { Peer = PeerOf(chat), MessageId = messageId };
        }
        if (username is { Length: > 0 })
        {
            try { return new TgLink(TgLinkKind.Peer) { Peer = await ResolveAsync("@" + username), MessageId = messageId }; }
            catch (PeerNotFoundException) { return null; }
        }
        return null;
    }

    private async Task<TgLink?> ParseInviteAsync(string hash)
    {
        try
        {
            switch (await _client.Messages_CheckChatInvite(hash))
            {
                case ChatInviteAlready already:
                    _chats[already.chat.ID] = already.chat;
                    return new TgLink(TgLinkKind.Peer) { Peer = PeerOf(already.chat) };
                case ChatInvitePeek peek:
                    _chats[peek.chat.ID] = peek.chat;
                    return new TgLink(TgLinkKind.Peer) { Peer = PeerOf(peek.chat) };
                case ChatInvite inv:
                    return new TgLink(TgLinkKind.Invite)
                    {
                        InviteHash = hash, InviteTitle = inv.title, InviteMembers = inv.participants_count,
                        InviteIsChannel = inv.flags.HasFlag(ChatInvite.Flags.broadcast),
                    };
            }
        }
        catch (RpcException ex) when (ex.Message.StartsWith("INVITE_HASH")) { throw new PeerNotFoundException("Ссылка-приглашение недействительна или устарела."); }
        return null;
    }

    /// <summary>Вступить по приглашению — открытый после этого чат.</summary>
    public async Task<PeerRef?> JoinInviteAsync(string hash)
    {
        // Вступили — ответ с обновлениями (в них и сам чат); вариант «открыть мини-приложение» здесь не поддерживаем.
        if (await _client.Messages_ImportChatInvite(hash) is not Messages_ChatInviteJoinResultOk { updates: { } updates }) return null;
        updates.CollectUsersChats(_users, _chats);
        return updates.Chats.Values.FirstOrDefault() is { } chat ? PeerOf(chat) : null;
    }
}
