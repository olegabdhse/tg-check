using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.Helpers;

/// <summary>
/// Каталог реакций Telegram (иконки и анимации из messages.getAvailableReactions) — как в официальных клиентах.
/// Загружается один раз после входа; пока не загружен, реакции показываются обычными эмодзи.
/// </summary>
public static class ReactionCatalog
{
    private static readonly Dictionary<string, ReactionDef> ByEmoji = [];
    private static IReadOnlyList<ReactionDef> _all = [];

    /// <summary>Реакции без премиума в порядке Telegram (для панели выбора).</summary>
    public static IReadOnlyList<ReactionDef> Free => _all.Where(r => !r.IsPremium).ToList();

    public static bool IsLoaded => _all.Count > 0;
    public static event Action? Loaded;

    public static async Task LoadAsync(TelegramService tg)
    {
        if (IsLoaded) return;
        var defs = await tg.GetReactionDefsAsync();
        if (defs.Count == 0) return;
        _all = defs;
        lock (ByEmoji)
            foreach (var d in defs) ByEmoji[Key(d.Emoji)] = d;
        Loaded?.Invoke();
        _ = Task.Run(() => PrefetchAsync(tg.Media));
    }

    // Разрешённые реакции чатов: нет записи / null — любые.
    private static readonly Dictionary<long, IReadOnlyList<string>?> Allowed = [];

    public static void SetAllowed(long peerKey, IReadOnlyList<string>? emojis)
    {
        lock (Allowed) Allowed[peerKey] = emojis;
    }

    /// <summary>Реакции, которые можно поставить в этом чате (порядок — как в Telegram).</summary>
    public static IReadOnlyList<string> ChoicesFor(long peerKey)
    {
        IReadOnlyList<string>? allowed;
        lock (Allowed) allowed = Allowed.GetValueOrDefault(peerKey);
        var free = IsLoaded ? Free.Select(d => d.Emoji).ToList() : [.. TelegramService.QuickReactions];
        if (allowed is null) return free;
        // Сначала разрешённые из стандартного порядка, затем остальные разрешённые.
        var set = allowed.Select(Key).ToHashSet();
        return free.Where(e => set.Contains(Key(e))).Concat(allowed.Where(a => !free.Any(f => Key(f) == Key(a)))).ToList();
    }

    /// <summary>Иконки и анимации выбора качаются заранее в фоне — в меню и под сообщениями они уже готовы.</summary>
    private static async Task PrefetchAsync(Core.Media.MediaService media)
    {
        var free = Free;
        for (var i = 0; i < free.Count; i++)
        {
            var def = free[i];
            try
            {
                if (def.Icon is { } icon) await media.GetDocumentBytesAsync(icon);
            }
            catch { /* не критично — догрузится при показе */ }
        }
    }

    public static ReactionDef? Find(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return null;
        lock (ByEmoji) return ByEmoji.GetValueOrDefault(Key(emoji));
    }

    // «❤» и «❤️» — одна реакция: сравниваем без селектора варианта U+FE0F.
    private static string Key(string emoji) => emoji.Replace("️", "");
}
