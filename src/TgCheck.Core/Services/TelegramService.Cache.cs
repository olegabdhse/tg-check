using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

// Очистка кэша из настроек: удаляется всё, кроме списка чатов, своего профиля, эмодзи (все), недавних и избранных
// стикеров и нескольких последних личных чатов (их история, профиль, аватар и медиа).
public sealed partial class TelegramService
{
    /// <summary>Сколько места занимает кэш (байты).</summary>
    public Task<long> GetCacheSizeAsync() => Task.Run(() =>
    {
        try
        {
            var root = Path.GetDirectoryName(_cache.Folder)!;
            return Directory.Exists(root)
                ? new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0L;
        }
        catch { return 0L; }
    });

    /// <summary>
    /// Утка с веником для окна очистки (как в Telegram): стикер с 🧹 из официального набора UtyaDuck,
    /// если там такого нет — любой стикер с этим эмодзи.
    /// </summary>
    public async Task<StickerItem?> GetCleanupStickerAsync()
    {
        try
        {
            if (await _client.Messages_GetStickerSet(new InputStickerSetShortName { short_name = "UtyaDuck" }, 0) is { } set)
            {
                var ids = set.packs.Where(p => p.emoticon.Contains("🧹")).SelectMany(p => p.documents).ToHashSet();
                if (set.documents.OfType<Document>().FirstOrDefault(d => ids.Contains(d.id)) is { } duck) return ToSticker(duck);
            }
        }
        catch (Exception ex) { Logging.Write("cleanup sticker: " + ex.Message); }
        try
        {
            if (await _client.Messages_GetStickers("🧹", 0) is Messages_Stickers found && found.stickers.OfType<Document>().FirstOrDefault() is { } any)
                return ToSticker(any);
        }
        catch (Exception ex) { Logging.Write("cleanup sticker: " + ex.Message); }
        return null;
    }

    /// <summary>Очистить кэш, оставив нужное (см. выше). keepChats — последние личные чаты. Возвращает освобождённые байты.</summary>
    public async Task<long> ClearCacheAsync(IReadOnlyCollection<PeerRef> keepChats, IProgress<double>? progress = null, IEnumerable<long>? alsoKeepDocs = null)
    {
        var keepDocs = new HashSet<long>(alsoKeepDocs ?? []);
        var keepPhotos = new HashSet<long>();
        try
        {
            foreach (var s in await GetRecentStickersAsync()) keepDocs.Add(s.MediaId);
            foreach (var s in await GetFavedStickersAsync()) keepDocs.Add(s.MediaId);
        }
        catch (Exception ex) { Logging.Write("clear cache: стикеры — " + ex.Message); }

        return await Task.Run(() =>
        {
            // Эмодзи: набор анимированных эмодзи Telegram и все премиум-эмодзи, что уже встречались.
            if (_cache.Load<Messages_StickerSet>("tg_emoji") is { } set)
                foreach (var d in set.documents.OfType<Document>()) keepDocs.Add(d.id);
            var folder = _cache.Folder;
            var chatFiles = Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
            foreach (var f in chatFiles)
                if (Path.GetFileNameWithoutExtension(f) is var n && n.StartsWith("emoji_") && long.TryParse(n[6..], out var id)) keepDocs.Add(id);

            // Сохраняемые чаты: их сообщения из кэша истории — фото и файлы остаются.
            var keepKeys = keepChats.Select(p => p.Key).ToHashSet();
            var keepPeers = keepChats.Select(p => p.Id).Append(_client.UserId).ToHashSet();
            foreach (var peer in keepChats)
            {
                if (_cache.Load<Messages_MessagesBase>($"history_{peer.Key}") is not { } history) continue;
                foreach (var m in history.Messages.OfType<Message>())
                    switch (m.media)
                    {
                        case MessageMediaPhoto { photo: Photo p }: keepPhotos.Add(p.id); break;
                        case MessageMediaDocument { document: Document d }: keepDocs.Add(d.id); break;
                        case MessageMediaWebPage { webpage: WebPage { photo: Photo wp } }: keepPhotos.Add(wp.id); break;
                    }
            }

            long freed = 0;
            foreach (var f in chatFiles)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var drop = name == "stories"
                    || (name.StartsWith("history_") && !(long.TryParse(name[8..], out var hk) && keepKeys.Contains(hk)))
                    || (name.StartsWith("user_full_") && !(long.TryParse(name[10..], out var uk) && keepKeys.Contains(uk)));
                if (!drop) continue;
                try
                {
                    var size = new FileInfo(f).Length;
                    File.Delete(f);
                    freed += size;
                }
                catch { /* занят — останется */ }
            }
            freed += Media.ClearCacheExcept(keepDocs, keepPhotos, keepPeers, progress);
            Logging.Write($"clear cache: освобождено {freed / 1024 / 1024} МБ, оставлено чатов {keepChats.Count}, документов {keepDocs.Count}");
            return freed;
        });
    }
}
