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
                PhotoId = u.photo?.photo_id ?? 0;
                IsPremium = u.flags.HasFlag(User.Flags.premium);
                IsVerified = u.flags.HasFlag(User.Flags.verified);
                break;
            case Channel c:
                Kind = c.IsChannel ? PeerKind.Channel : PeerKind.Group;
                MembersCount = c.participants_count;
                CanWrite = !c.IsChannel || c.flags.HasFlag(Channel.Flags.creator) || c.admin_rights is not null;
                CanDeleteOthers = c.flags.HasFlag(Channel.Flags.creator) || c.admin_rights?.flags.HasFlag(ChatAdminRights.Flags.delete_messages) == true;
                IsBasicGroup = false;
                Title = c.title ?? "";
                HasPhoto = c.photo is ChatPhoto;
                PhotoId = (c.photo as ChatPhoto)?.photo_id ?? 0;
                IsVerified = c.flags.HasFlag(Channel.Flags.verified);
                break;
            case ChatBase c:
                Kind = PeerKind.Group;
                MembersCount = (c as Chat)?.participants_count ?? 0;
                IsBasicGroup = true;
                CanDeleteOthers = c is Chat bc && (bc.flags.HasFlag(Chat.Flags.creator) || bc.admin_rights is not null);
                Title = c.Title ?? "";
                HasPhoto = (c as Chat)?.photo is ChatPhoto;
                PhotoId = ((c as Chat)?.photo as ChatPhoto)?.photo_id ?? 0;
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
    /// <summary>Id текущего фото (сменилось — аватар нужно перекачать).</summary>
    public long PhotoId { get; }
    public bool IsSelf { get; }
    public bool IsPremium { get; }
    public bool IsVerified { get; }
    public bool IsUser => Kind is PeerKind.User or PeerKind.Bot;
    /// <summary>Премиум-эмодзи статус рядом с именем (id кастомного эмодзи).</summary>
    public long? EmojiStatusId { get; }
    public int MembersCount { get; }
    /// <summary>Можно ли писать (в каналах — только админам).</summary>
    public bool CanWrite { get; } = true;
    /// <summary>Можно удалять чужие сообщения: в личке и с ботом — всегда, в группах и каналах — создателю или админу с правом удаления.</summary>
    public bool CanDeleteOthers { get; } = true;
    /// <summary>Удаление можно выбрать «только у меня» или «у всех» (личка, бот, обычная группа); в супергруппах и каналах — всегда у всех.</summary>
    public bool CanRevoke => Info is not Channel;
    /// <summary>Обычная (не супер-) группа: id сообщений в ней глобальные, как в личке.</summary>
    public bool IsBasicGroup { get; }
    /// <summary>Группа с темами (форум): вместо одной ленты — список тем.</summary>
    public bool IsForum => Info is Channel { flags: var f } && f.HasFlag(Channel.Flags.forum);

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

/// <summary>Оформление участка текста (как в Telegram): жирный, курсив, код, блок кода с языком, ссылка под текстом…</summary>
public enum TextStyleKind { Bold, Italic, Underline, Strike, Code, Pre, TextUrl, Spoiler, Quote }

/// <summary>Участок текста с оформлением: позиция в UTF-16, длина; Data — язык блока кода или адрес ссылки.</summary>
public sealed record TextStyleSpan(TextStyleKind Kind, int Offset, int Length, string? Data = null);

public enum MediaKind { None, Photo, Sticker, Video, Gif, Voice, VideoNote, Audio, Document, Poll, Contact, Location, WebPage, Other }

/// <summary>Блок «сложного сообщения» Telegram (статья, коллаж, живые фото, музыка): по порядку, как в посте.</summary>
public enum RichBlockKind { Heading, Paragraph, Quote, Code, Divider, Media, Album, Slideshow }

public sealed class RichBlock
{
    public required RichBlockKind Kind { get; init; }
    public string? Text { get; init; }
    /// <summary>Одно медиа (Media) или несколько (Album: коллаж, слайд-шоу).</summary>
    public IReadOnlyList<MessageItem> Items { get; init; } = [];
}

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
    /// <summary>Голосовое/кружок ещё не прослушано (у своих — собеседником). Меняется, когда послушают.</summary>
    public bool MediaUnread { get; set; }
    /// <summary>Сложное сообщение (статья, коллаж, музыка) — блоки по порядку; null — обычное.</summary>
    public IReadOnlyList<RichBlock>? Rich { get; set; }
    public int? ReplyToId { get; init; }
    /// <summary>Корень ветки (комментарии к посту), если сообщение — ответ внутри ветки.</summary>
    public int? ThreadTopId { get; init; }
    /// <summary>Тема форума, к которой относится сообщение (null — тема «Общее» или не форум).</summary>
    public int? TopicId { get; init; }
    public string? ForwardedFrom { get; init; }
    public string? PostAuthor { get; init; }
    public int Views { get; init; }

    /// <summary>Служебное сообщение («X вступил в группу» и т.п.).</summary>
    public string? ServiceText { get; init; }
    /// <summary>Подарок (служебное сообщение «подарил / передал»): карточка как в Telegram.</summary>
    public GiftCard? Gift { get; init; }

    public MediaKind Media { get; init; }
    /// <summary>Подпись медиа: имя файла, «Голосовое · 0:12», вопрос опроса и т.п.</summary>
    public string? MediaTitle { get; init; }
    /// <summary>Песня: название и исполнитель (из тегов файла).</summary>
    public string? SongTitle { get; init; }
    public string? SongPerformer { get; init; }
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
    public IReadOnlyList<ReactionInfo> Reactions { get; init; } = [];

    /// <summary>Геопозиция (точка, место, трансляция).</summary>
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? VenueAddress { get; init; }
    internal long GeoAccessHash { get; init; }

    /// <summary>Премиум (кастомные) эмодзи в тексте: позиция в UTF-16 и id документа.</summary>
    public IReadOnlyList<CustomEmojiSpan> CustomEmoji { get; init; } = [];
    /// <summary>Оформление текста (жирный, код, блоки кода…).</summary>
    public IReadOnlyList<TextStyleSpan> Formatting { get; init; } = [];
    /// <summary>Опрос/викторина (обновляется при голосовании).</summary>
    public PollInfo? Poll { get; set; }

    /// <summary>Короткий текст для списка чатов.</summary>
    public string Preview => ServiceText ?? (Rich is { Count: > 0 } && string.IsNullOrEmpty(Text) ? Services.TelegramService.RichPreview(Rich) : Media switch
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

/// <summary>Карточка подарка в чате (как в Telegram): стикер, заголовок, от кого, сообщение, цена.</summary>
public sealed class GiftCard
{
    public StickerItem? Sticker { get; init; }
    /// <summary>«Подарок от …», «Вы отправили подарок», «… передал(а) вам подарок».</summary>
    public required string Caption { get; init; }
    /// <summary>Название подарка (у уникального — с номером).</summary>
    public string? Title { get; init; }
    /// <summary>Сообщение от отправителя.</summary>
    public string? Message { get; init; }
    /// <summary>Пояснение мелким шрифтом (обмен на звёзды и т.п.).</summary>
    public string? Note { get; init; }
    public long Stars { get; init; }
    public bool IsUnique { get; init; }
    /// <summary>Фон уникального подарка (цвета узора), RGB.</summary>
    public int? CenterColor { get; init; }
    public int? EdgeColor { get; init; }
    /// <summary>Узор уникального подарка (символ вокруг модели) и его цвет.</summary>
    public TL.Document? Pattern { get; init; }
    public int? PatternColor { get; init; }
}
