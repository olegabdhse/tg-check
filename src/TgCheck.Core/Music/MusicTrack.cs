using System.Text.Json.Serialization;
using TgCheck.Core.Models;

namespace TgCheck.Core.Music;

/// <summary>Откуда трек: песня из чатов Telegram, найденная музыкальным ботом Telegram, Deezer (только 30 с превью; Audius — старые записи истории).</summary>
public enum MusicSource { Telegram, Audius, Deezer, Bot }

/// <summary>Трек раздела «Музыка» — из любого источника; сохраняется в историю прослушанного.</summary>
public sealed class MusicTrack
{
    public MusicSource Source { get; init; }
    /// <summary>Id в своём источнике (у песни Telegram — id файла).</summary>
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string? Album { get; init; }
    /// <summary>Обложка для плиток и списков (~250–480 px).</summary>
    public string? CoverUrl { get; set; }
    /// <summary>Крупная обложка для плеера.</summary>
    public string? CoverBigUrl { get; set; }
    /// <summary>Запасные серверы обложки (у Audius основной сервер иногда не отвечает).</summary>
    public string[]? CoverMirrors { get; init; }
    public double Duration { get; init; }
    public string? Genre { get; init; }
    /// <summary>Исполнитель в Deezer — для «похожих исполнителей».</summary>
    public long ArtistId { get; init; }

    // Песня Telegram: где она лежит (сообщение; 0 — музыка профиля) — чтобы сыграть её снова после перезапуска.
    public long PeerKey { get; init; }
    public int MessageId { get; init; }
    // Найдено ботом: каким (чтобы найти снова после перезапуска) и ссылка на файл, если бот отдал её, а не файл Telegram.
    public string? BotName { get; init; }
    public string? StreamUrl { get; set; }

    /// <summary>Само сообщение с песней (пока программа открыта).</summary>
    [JsonIgnore] public MessageItem? Song { get; set; }
    /// <summary>Найден в этот запуск (ссылки ботов на файлы временные — трек из истории ищется у бота заново).</summary>
    [JsonIgnore] public bool Live { get; set; }
    /// <summary>Только 30-секундный фрагмент (Deezer, если полной версии не нашлось).</summary>
    [JsonIgnore] public bool IsPreview => Source == MusicSource.Deezer;

    /// <summary>Ключ проигрывателя.</summary>
    [JsonIgnore] public string Key => $"music:{Source}:{Id}";

    /// <summary>Один и тот же трек из разных источников («исполнитель — название» без регистра и лишнего).</summary>
    [JsonIgnore] public string Match => Normalize(Artist) + "|" + Normalize(Title);

    public static string Normalize(string s)
    {
        s = s.ToLowerInvariant();
        // «(feat. …)», «[Official Video]», «- Remastered» — не часть названия.
        var cut = s.IndexOfAny(['(', '[']);
        if (cut > 0) s = s[..cut];
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// Одна и та же песня, даже если она из разных каналов и теги записаны по-разному: «Artist - Title» в названии,
    /// «(feat. …)», «Artist, Other» или «Artist &amp; Other» в исполнителе.
    /// </summary>
    public static bool SameSong(MusicTrack a, MusicTrack b)
    {
        if (a.Key == b.Key) return true;
        // По словам (регистр и знаки не важны): названия совпали на 60% и больше — смотрим на исполнителя;
        // исполнители совпали на 50% и больше (или у одной из песен он не указан) — это та же песня.
        var ta = Words(CoreTitleText(a));
        var tb = Words(CoreTitleText(b));
        if (ta.Count == 0 || tb.Count == 0) return false;
        if ((double)ta.Intersect(tb).Count() / Math.Max(ta.Count, tb.Count) < 0.6) return false;
        var aa = Words(Feat.Replace(a.Artist.Length > 0 ? a.Artist : MainArtist(a), ""));
        var ab = Words(Feat.Replace(b.Artist.Length > 0 ? b.Artist : MainArtist(b), ""));
        if (aa.Count == 0 || ab.Count == 0) return true;
        // Исполнители — по меньшему списку: «Slo» и «Slo, Twilight, Tazzy» — тот же исполнитель.
        return (double)aa.Intersect(ab).Count() / Math.Min(aa.Count, ab.Count) >= 0.5;
    }

    /// <summary>Без повторов одной и той же песни (первая встреченная остаётся).</summary>
    public static List<MusicTrack> Distinct(IEnumerable<MusicTrack> tracks)
    {
        var list = new List<MusicTrack>();
        foreach (var t in tracks)
            if (!list.Any(x => SameSong(x, t))) list.Add(t);
        return list;
    }

    private static readonly System.Text.RegularExpressions.Regex Feat =
        new(@"\s(feat\.?|ft\.?|featuring|при уч\.?)\s.*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // « - », « – », « — » между исполнителем и названием.
    private static int Dash(string s)
    {
        foreach (var d in new[] { " - ", " – ", " — " })
            if (s.IndexOf(d, StringComparison.Ordinal) is > 0 and var i) return i;
        return -1;
    }

    // Название без «Исполнитель - » в начале и без «feat. …».
    private static string CoreTitleText(MusicTrack t)
    {
        var title = t.Title;
        var dash = Dash(title);
        if (dash > 0 && (t.Artist.Length == 0 || Normalize(title[..dash]).Contains(Normalize(MainArtist(t))))) title = title[(dash + 3)..];
        var cut = title.IndexOfAny(['(', '[']); // «(slowed)», «[Official Audio]»
        if (cut > 0) title = title[..cut];
        return Version.Replace(Feat.Replace(title, ""), "");
    }

    // Слова без регистра и знаков: «Did I Tell U?» → {did, i, tell, u}.
    private static HashSet<string> Words(string s) =>
        System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{N}]+").Select(m => m.Value).ToHashSet();

    // Хвосты версий без скобок: «- sped up», «slowed + reverb», «nightcore», «remastered»… — та же песня.
    private static readonly System.Text.RegularExpressions.Regex Version = new(
        @"\s+([-–—|/+]\s*)?\b(slowed|sped\s*up|speed\s*up|reverb|nightcore|bass\s*boost(ed)?|8d|remaster(ed)?|tik\s*tok|instrumental|acapella|radio\s*edit|extended|version|mix)\b.*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Привести в порядок исполнителя и название от бота: «Исполнитель – Песня» целиком в названии, повтор
    /// «Песня, Песня», исполнитель вместо названия. Пустое название — null (такую запись лучше не показывать).
    /// </summary>
    public static (string Artist, string Title)? Tidy(string? artist, string? title)
    {
        artist = (artist ?? "").Trim();
        title = (title ?? "").Trim();
        var dash = Dash(title);
        if (dash > 0 && (artist.Length == 0 || Normalize(title[..dash]).Contains(Normalize(artist)) || Normalize(artist).Contains(Normalize(title[..dash]))))
        {
            if (artist.Length == 0) artist = title[..dash].Trim();
            title = title[(dash + 3)..].Trim();
        }
        // «Life Letters, Life Letters» → «Life Letters».
        var parts = title.Split(", ");
        if (parts.Length > 1 && parts.All(p => Normalize(p) == Normalize(parts[0]))) title = parts[0];
        parts = artist.Split(", ");
        if (parts.Length > 1 && parts.All(p => Normalize(p) == Normalize(parts[0]))) artist = parts[0];
        // Название — это тот же исполнитель (бот не знает названия) — непонятно, что за песня.
        if (Normalize(title).Length == 0 || Normalize(title) == Normalize(artist)) return null;
        return (artist, title);
    }

    // Первый исполнитель: «A, B», «A & B», «A feat. B», «A x B» → «A»; пустой исполнитель — из «A - Title» в названии.
    public static string MainArtist(MusicTrack t)
    {
        var artist = t.Artist;
        if (artist.Length == 0)
        {
            var dash = Dash(t.Title);
            if (dash > 0) artist = t.Title[..dash];
        }
        artist = Feat.Replace(artist, "");
        var cut = artist.IndexOfAny([',', '&', ';', '/']);
        if (cut > 0) artist = artist[..cut];
        var x = artist.IndexOf(" x ", StringComparison.OrdinalIgnoreCase);
        return x > 0 ? artist[..x] : artist;
    }

    /// <summary>Песня Telegram (из чата или профиля).</summary>
    public static MusicTrack FromTelegram(MessageItem m)
    {
        // Без тегов в названии — имя файла: «Artist - Song.mp3» → без расширения; «<unknown>» — исполнителя нет.
        var title = AudioExtension.Replace(m.SongTitle ?? m.MediaTitle ?? "Аудио", "");
        var performer = m.SongPerformer is { } p && !p.Contains("unknown", StringComparison.OrdinalIgnoreCase) ? p : null;
        var (artist, cleanTitle) = Tidy(performer, title) ?? (performer ?? "", title);
        return FromTelegram(m, artist, cleanTitle);
    }

    private static readonly System.Text.RegularExpressions.Regex AudioExtension =
        new(@"\.(mp3|m4a|ogg|oga|opus|wav|flac|aac|wma)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Похоже ли на песню: от минуты до 15 минут (короче — звуки и отрывки, длиннее — записи эфиров, подкасты, миксы),
    /// и название — не имя файла вида «Технология_AMD_3D_VCache» (так подписаны лекции, озвучки, голосовые-файлы).
    /// </summary>
    public static bool IsSong(MusicTrack t)
    {
        if (t.Duration is > 0 and (< 60 or > 900)) return false;
        if (t.Title.Count(c => c == '_') >= 2 && !t.Title.Contains(' ')) return false;
        return Normalize(t.Title).Length > 0;
    }

    private static MusicTrack FromTelegram(MessageItem m, string artist, string title) => new()
    {
        Source = MusicSource.Telegram,
        Id = m.MediaId.ToString(),
        Title = title,
        Artist = artist,
        Duration = m.DurationSeconds,
        PeerKey = m.PeerKey,
        MessageId = m.Id,
        Song = m,
    };
}

/// <summary>Запись истории: трек, когда слушали последний раз и сколько раз.</summary>
public sealed class MusicHistoryEntry
{
    public MusicTrack Track { get; init; } = new();
    public DateTime LastPlayedUtc { get; set; }
    public int Plays { get; set; }
}
