using TL;

namespace TgCheck.Core.Models;

public enum PeerKind { User, Bot, Group, Channel }

/// <summary>Пользователь, группа или канал. Key уникален среди всех типов (как в Bot API).</summary>
public sealed class PeerRef
{
    internal PeerRef(IPeerInfo info, long selfId)
    {
        Info = info;
        Key = KeyOf(info);
        Id = info.ID;
        switch (info)
        {
            case User u:
                Kind = u.IsBot ? PeerKind.Bot : PeerKind.User;
                Title = Names.Join(u.first_name, u.last_name);
                IsSelf = u.id == selfId;
                HasPhoto = u.photo is not null;
                IsPremium = u.flags.HasFlag(User.Flags.premium);
                IsVerified = u.flags.HasFlag(User.Flags.verified);
                break;
            case Channel c:
                Kind = c.IsChannel ? PeerKind.Channel : PeerKind.Group;
                MembersCount = c.participants_count;
                CanWrite = !c.IsChannel || c.flags.HasFlag(Channel.Flags.creator) || c.admin_rights is not null;
                IsBasicGroup = false;
                Title = c.title ?? "";
                HasPhoto = c.photo is ChatPhoto;
                IsVerified = c.flags.HasFlag(Channel.Flags.verified);
                break;
            case ChatBase c:
                Kind = PeerKind.Group;
                MembersCount = (c as Chat)?.participants_count ?? 0;
                IsBasicGroup = true;
                Title = c.Title ?? "";
                HasPhoto = (c as Chat)?.photo is ChatPhoto;
                break;
        }
        Username = info.MainUsername;
        EmojiStatusId = info switch
        {
            User { emoji_status: { } es } => es.DocumentId,
            Channel { emoji_status: { } es } => es.DocumentId,
            _ => null,
        };
        if (EmojiStatusId == 0) EmojiStatusId = null;
    }

    public long Key { get; }
    public long Id { get; }
    public PeerKind Kind { get; }
    public string Title { get; } = "";
    public string? Username { get; }
    public bool HasPhoto { get; }
    public bool IsSelf { get; }
    public bool IsPremium { get; }
    public bool IsVerified { get; }
    public bool IsUser => Kind is PeerKind.User or PeerKind.Bot;
    /// <summary>Премиум-эмодзи статус рядом с именем (id кастомного эмодзи).</summary>
    public long? EmojiStatusId { get; }
    public int MembersCount { get; }
    /// <summary>Можно ли писать (в каналах — только админам).</summary>
    public bool CanWrite { get; } = true;
    /// <summary>Обычная (не супер-) группа: id сообщений в ней глобальные, как в личке.</summary>
    public bool IsBasicGroup { get; }

    /// <summary>Видимое имя: невидимые символы (ZWJ, U+2060 и т.п.) не считаются.</summary>
    public string DisplayTitle => IsSelf ? "Избранное" : Names.Visible(Title) ?? (Username is { } u ? "@" + u : "Без имени");

    internal IPeerInfo Info { get; }

    internal static long KeyOf(IPeerInfo p) => p switch
    {
        User u => u.id,
        Channel c => -1_000_000_000_000 - c.id,
        _ => -p.ID,
    };

    internal static long KeyOf(Peer p) => p switch
    {
        PeerUser u => u.user_id,
        PeerChannel c => -1_000_000_000_000 - c.channel_id,
        PeerChat c => -c.chat_id,
        _ => 0,
    };
}

/// <summary>Кастомное эмодзи внутри текста сообщения.</summary>
public sealed record CustomEmojiSpan(int Offset, int Length, long DocumentId);

public enum MediaKind { None, Photo, Sticker, Video, Gif, Voice, VideoNote, Audio, Document, Poll, Contact, Location, WebPage, Other }

/// <summary>Сообщение чата.</summary>
public sealed class MessageItem : MediaHolder
{
    public required int Id { get; init; }
    public required long PeerKey { get; init; }
    public long? FromKey { get; init; }
    public PeerRef? From { get; init; }
    public DateTime DateUtc { get; init; }
    public string Text { get; set; } = "";
    public bool IsOut { get; init; }
    public DateTime? EditDateUtc { get; set; }
    public int? ReplyToId { get; init; }
    public string? ForwardedFrom { get; init; }
    public string? PostAuthor { get; init; }
    public int Views { get; init; }

    /// <summary>Служебное сообщение («X вступил в группу» и т.п.).</summary>
    public string? ServiceText { get; init; }

    public MediaKind Media { get; init; }
    /// <summary>Подпись медиа: имя файла, «Голосовое · 0:12», вопрос опроса и т.п.</summary>
    public string? MediaTitle { get; init; }
    public long MediaSize { get; init; }
    public int MediaWidth { get; init; }
    public int MediaHeight { get; init; }
    public string? StickerEmoji { get; init; }

    /// <summary>Сообщения одного альбома (несколько фото/видео/файлов) имеют общий GroupedId.</summary>
    public long GroupedId { get; init; }
    /// <summary>Инлайн-кнопки под сообщением (строки кнопок).</summary>
    public IReadOnlyList<IReadOnlyList<BotButton>> InlineButtons { get; init; } = [];
    /// <summary>Клавиатура бота под полем ввода (если пришла с этим сообщением).</summary>
    public IReadOnlyList<IReadOnlyList<BotButton>>? ReplyKeyboard { get; init; }
    public string? KeyboardPlaceholder { get; init; }
    /// <summary>Комментарии к посту канала (null — комментарии выключены).</summary>
    public int? Comments { get; init; }

    /// <summary>Геопозиция (точка, место, трансляция).</summary>
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? VenueAddress { get; init; }
    internal long GeoAccessHash { get; init; }

    /// <summary>Премиум (кастомные) эмодзи в тексте: позиция в UTF-16 и id документа.</summary>
    public IReadOnlyList<CustomEmojiSpan> CustomEmoji { get; init; } = [];
    /// <summary>Опрос/викторина (обновляется при голосовании).</summary>
    public PollInfo? Poll { get; set; }

    /// <summary>Короткий текст для списка чатов.</summary>
    public string Preview => ServiceText ?? (Media switch
    {
        MediaKind.None or MediaKind.WebPage => Text,
        MediaKind.Photo => Prefix("🖼 Фото"),
        MediaKind.Sticker => $"{StickerEmoji} Стикер".Trim(),
        MediaKind.Video => Prefix("🎬 Видео"),
        MediaKind.Gif => Prefix("GIF"),
        MediaKind.Voice => "🎤 Голосовое сообщение",
        MediaKind.VideoNote => "⏺ Видеосообщение",
        MediaKind.Audio => Prefix("🎵 " + (MediaTitle ?? "Аудио")),
        MediaKind.Document => Prefix("📎 " + (MediaTitle ?? "Файл")),
        MediaKind.Poll => "📊 " + MediaTitle,
        MediaKind.Contact => "👤 Контакт",
        MediaKind.Location => "📍 Геопозиция",
        _ => Prefix("Сообщение"),
    }).ReplaceLineEndings(" ");

    private string Prefix(string kind) => string.IsNullOrEmpty(Text) ? kind : $"{kind}, {Text}";
}

/// <summary>Строка списка чатов.</summary>
public sealed class ChatSummary
{
    public required PeerRef Peer { get; init; }
    public MessageItem? Last { get; init; }
    public int UnreadCount { get; init; }
    public int UnreadMentions { get; init; }
    public int ReadInboxMaxId { get; init; }
    public int ReadOutboxMaxId { get; init; }
    public bool IsPinned { get; init; }
    public bool IsMuted { get; init; }
    public bool IsArchived { get; init; }
    public bool IsMarkedUnread { get; init; }
}

public static class Names
{
    public static string Join(params string?[] parts) =>
        string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>null, если в строке нет ни одного видимого символа.</summary>
    public static string? Visible(string? s) => s is not null && s.Any(IsVisible) ? s : null;

    private static bool IsVisible(char c)
    {
        if (c is 'ㅤ' or 'ᅟ' or 'ᅠ' or 'ﾠ' or '⠀') return false;
        return char.GetUnicodeCategory(c) switch
        {
            System.Globalization.UnicodeCategory.Format or
            System.Globalization.UnicodeCategory.SpaceSeparator or
            System.Globalization.UnicodeCategory.Control or
            System.Globalization.UnicodeCategory.NonSpacingMark or
            System.Globalization.UnicodeCategory.EnclosingMark or
            System.Globalization.UnicodeCategory.LineSeparator or
            System.Globalization.UnicodeCategory.ParagraphSeparator => false,
            _ => true,
        };
    }
}
