using System.Collections.Concurrent;
using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

/// <summary>Чат, где хранятся удалённые или правленые сообщения.</summary>
public sealed record VaultChat(long PeerKey, string Title, PeerKind Kind, int Deleted, int Edited);

/// <summary>Прежний текст сообщения.</summary>
public sealed record MessageEdit(DateTime AtUtc, string Text);

// Удалённые сообщения, история правок и одноразовые медиа.
public sealed partial class TelegramService
{
    private const long MaxSavedFile = 50 * 1024 * 1024;
    private const int MaxFilesPerDeletion = 20;
    private const long ChannelKeyBase = -1_000_000_000_000;

    private MessageVault? _vaultStore;
    private readonly object _vaultLock = new();
    // Удалённое отсюда — желание самого пользователя: такие сообщения не сохраняются.
    private readonly ConcurrentDictionary<int, byte> _deletedHere = new();
    private int _offlineQueued;

    private MessageVault Vault
    {
        get
        {
            lock (_vaultLock) return _vaultStore ??= new MessageVault(Path.Combine(_env.DataDirectory, "vault.db"));
        }
    }

    public bool WasDeletedHere(int id) => _deletedHere.ContainsKey(id);

    // Хранилище вспомогательное: его сбой не должен мешать выходу из аккаунта, очистке истории и кэша.
    private void TryVault(Action<MessageVault> action)
    {
        try { action(Vault); }
        catch (Exception ex) { Logging.Write("vault: " + ex.Message); }
    }

    private void RememberSeen(IEnumerable<MessageBase> messages)
    {
        try
        {
            foreach (var m in messages.OfType<Message>()) Vault.Seen(m);
        }
        catch (Exception ex) { Logging.Write("vault: " + ex.Message); }
    }

    // Новое из обновлений: запоминаем; одноразовое скачиваем сразу, пока сервер его отдаёт.
    private void OnNewMessage(MessageBase m)
    {
        if (m is not Message message) return;
        RememberSeen([message]);
        if (MessageVault.IsViewOnce(message) && !message.flags.HasFlag(Message.Flags.out_)) _ = SaveFileAsync(MapMessage(message));
    }

    /// <summary>
    /// Отправка в режиме призрака: отложенным сообщением на несколько секунд вперёд. В «Избранном» отложенное стало бы напоминанием.
    /// Сервер отказал в отложенной отправке (неверное время на компьютере и т. п.) — отправляем обычным способом.
    /// </summary>
    /// <returns>Delayed — сообщение ещё не вышло: в ленту оно придёт обычным обновлением, а Result — его отложенная копия.</returns>
    private static async Task<(T Result, bool Delayed)> SendMaybeDelayedAsync<T>(PeerRef peer, Func<DateTime, Task<T>> send, bool isFile = false)
    {
        if (!GhostMode.DelaysNow || peer.IsSelf) return (await send(default), false);
        try { return (await send(DateTime.UtcNow + (isFile ? GhostMode.FileDelay : GhostMode.MessageDelay)), true); }
        catch (RpcException ex) when (ex.Message.Contains("SCHEDULE", StringComparison.Ordinal))
        {
            Logging.Write("ghost: отложенная отправка отклонена: " + ex.Message);
            return (await send(default), false);
        }
    }

    /// <summary>
    /// Своё действие (сообщение, реакция, правка) сервер сам считает появлением в сети и держит статус, пока его не снимут.
    /// Призрак снимает его сразу и ещё раз чуть позже: первый запрос может обогнать саму отправку.
    /// </summary>
    private async Task GoOfflineAfterActionAsync()
    {
        if (!GhostMode.CoversOnlineNow || Interlocked.Exchange(ref _offlineQueued, 1) == 1) return;
        await Task.Delay(300).ConfigureAwait(false);
        Volatile.Write(ref _offlineQueued, 0);
        await SetOnlineAsync(false).ConfigureAwait(false);
        await Task.Delay(3000).ConfigureAwait(false);
        if (GhostMode.CoversOnlineNow) await SetOnlineAsync(false).ConfigureAwait(false);
    }

    private void OnDeleted(long? peerKey, int[] ids)
    {
        var foreign = ids.Where(id => !_deletedHere.ContainsKey(id)).ToList();
        if (foreign.Count > 0) _ = NoteDeletedAsync(peerKey, foreign);
    }

    /// <summary>Сверка с сервером показала, что сообщений больше нет (удалили, пока программа была закрыта или без связи).</summary>
    public void MarkGone(PeerRef peer, IEnumerable<int> ids)
    {
        var foreign = ids.Where(id => !_deletedHere.ContainsKey(id)).ToList();
        if (foreign.Count > 0) _ = NoteDeletedAsync(peer.Key, foreign);
    }

    // Файл обычно ещё можно скачать: забираем его сразу, потом сервер может уже не отдать.
    private async Task NoteDeletedAsync(long? peerKey, IReadOnlyCollection<int> ids)
    {
        try
        {
            var found = await Vault.MarkDeletedAsync(peerKey, ids).ConfigureAwait(false);
            foreach (var message in found.Take(MaxFilesPerDeletion)) _ = SaveFileAsync(MapMessage(message));
        }
        catch (Exception ex) { Logging.Write("vault: " + ex.Message); }
    }

    private async Task SaveFileAsync(MessageItem item)
    {
        try
        {
            if (item.Photo is not null) await Media.GetFullPhotoAsync(item).ConfigureAwait(false);
            else if (item.Document is { } doc && doc.size <= MaxSavedFile) Media.Prefetch(item, doc.size);
        }
        catch (Exception ex) { Logging.Write($"vault file {item.Id}: {ex.Message}"); }
    }

    // Одноразовое, файл которого сервер уже убрал: показываем сохранённую версию.
    private MessageBase WithSavedMedia(MessageBase m)
    {
        if (m is not Message message || !MessageVault.IsExpired(message)) return m;
        try { return Vault.Find(PeerRef.KeyOf(message.peer_id), message.id) is { } saved && !MessageVault.IsExpired(saved) ? saved : m; }
        catch (Exception ex)
        {
            Logging.Write("vault: " + ex.Message);
            return m;
        }
    }

    /// <summary>Вставить в страницу истории удалённые сообщения её диапазона и отметить правленые.</summary>
    private List<MessageItem> Weave(PeerRef peer, List<MessageItem> page, int minId, int maxId)
    {
        try
        {
            foreach (var stored in Vault.Deleted(peer.Key, minId, maxId))
            {
                var item = MapMessage(stored.Message);
                item.DeletedAtUtc = stored.DeletedAtUtc;
                page.RemoveAll(m => m.Id == item.Id);
                page.Add(item);
            }
            page.Sort((a, b) => a.Id.CompareTo(b.Id));
            var edited = Vault.EditedIds(peer.Key, minId, maxId);
            foreach (var m in page) m.HasEditHistory = edited.Contains(m.Id);
        }
        catch (Exception ex) { Logging.Write("vault: " + ex.Message); }
        return page;
    }

    // Страница дошла до начала чата — диапазон продолжается вниз до самого начала.
    private List<MessageItem> WeavePage(PeerRef peer, List<MessageItem> page, int offsetId, int limit) => Weave(peer, page,
        page.Count < limit || page.Count == 0 ? 0 : page.Min(m => m.Id),
        offsetId == 0 ? int.MaxValue : offsetId - 1);

    /// <summary>Удалённые сообщения чата (сначала удалённые последними).</summary>
    public Task<IReadOnlyList<MessageItem>> GetDeletedMessagesAsync(PeerRef peer, int limit = 500) => Task.Run(() =>
    {
        IReadOnlyList<MessageItem> result = Vault.DeletedLatest(peer.Key, limit).Select(s =>
        {
            var item = MapMessage(s.Message);
            item.DeletedAtUtc = s.DeletedAtUtc;
            return item;
        }).ToList();
        return result;
    });

    /// <summary>Прежние тексты сообщения: сначала последняя правка.</summary>
    public async Task<IReadOnlyList<MessageEdit>> GetEditHistoryAsync(PeerRef peer, int id)
    {
        await Vault.WhenWritten().ConfigureAwait(false);
        return Vault.Edits(peer.Key, id).Select(e => new MessageEdit(e.AtUtc, e.Text)).ToList();
    }

    /// <summary>Чаты с удалёнными и правками — для окна очистки.</summary>
    public Task<IReadOnlyList<VaultChat>> GetVaultChatsAsync() => Task.Run<IReadOnlyList<VaultChat>>(async () =>
    {
        await Vault.WhenWritten().ConfigureAwait(false);
        return Vault.Counts().Select(c =>
        {
            var peer = PeerOf(PeerFromKey(c.PeerKey));
            return new VaultChat(c.PeerKey, peer?.DisplayTitle ?? "Неизвестный чат", peer?.Kind ?? PeerKind.Group, c.Deleted, c.Edited);
        }).OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    public Task ClearVaultAsync(IReadOnlyCollection<long> peers) => Vault.ClearAsync(peers);

    private static Peer PeerFromKey(long key) =>
        key > 0 ? new PeerUser { user_id = key }
        : key <= ChannelKeyBase ? new PeerChannel { channel_id = ChannelKeyBase - key }
        : new PeerChat { chat_id = -key };
}
