namespace TgCheck.Core.Models;

public enum Presence { Unknown, Online, Offline, Recently, LastWeek, LastMonth }

public sealed record Birthday(int Day, int Month, int? Year);

/// <summary>Вся информация о профиле текущего аккаунта.</summary>
public sealed class ProfileInfo
{
    /// <summary>Чей профиль (для открытия чата, загрузки подарков и фото).</summary>
    public PeerRef? Peer { get; init; }
    /// <summary>Музыка в профиле (закреплённая песня); null — нет.</summary>
    public MessageItem? Music { get; init; }
    public bool IsSelf { get; init; } = true;
    public PeerKind Kind { get; init; } = PeerKind.User;
    /// <summary>Участников/подписчиков (для групп и каналов).</summary>
    public int? MembersCount { get; init; }
    public int? OnlineCount { get; init; }
    public int? AdminsCount { get; init; }
    public string? InviteLink { get; init; }
    public string? LinkedChatTitle { get; init; }
    public bool IsSupergroup { get; init; }
    /// <summary>Описание бота (то, что показывается до /start).</summary>
    public string? BotDescription { get; init; }
    /// <summary>Команды бота: «/start — описание».</summary>
    public IReadOnlyList<string> BotCommands { get; init; } = [];

    public long Id { get; init; }
    public string FirstName { get; init; } = "";
    public string? LastName { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(LastName) ? FirstName : $"{FirstName} {LastName}";

    /// <summary>Имя без невидимых символов; null — если имя «пустое» (например, из одних U+2060).</summary>
    public string? VisibleName => Names.Visible(DisplayName);

    public string? Username { get; init; }
    public IReadOnlyList<string> ExtraUsernames { get; init; } = [];
    public string? Phone { get; init; }
    public string? Bio { get; init; }
    public Birthday? Birthday { get; init; }
    public string? LangCode { get; init; }

    public bool IsPremium { get; init; }
    public bool IsVerified { get; init; }
    public bool IsScam { get; init; }
    public bool IsFake { get; init; }

    public Presence Presence { get; init; }
    public DateTime? LastSeenUtc { get; init; }

    public long StarsBalance { get; init; }
    public decimal? TonBalance { get; init; }

    public int? GiftsCount { get; init; }

    public int? RatingLevel { get; init; }
    public long? RatingStars { get; init; }
    public long? RatingNextLevelStars { get; init; }

    public string? PersonalChannelTitle { get; init; }
    public string? PersonalChannelUsername { get; init; }

    /// <summary>Коллекционный подарок, установленный как эмодзи-статус.</summary>
    public string? CollectibleStatusTitle { get; init; }
    public DateTime? EmojiStatusUntilUtc { get; init; }

    public string? BusinessAddress { get; init; }

    /// <summary>Аватар (jpeg/png), null — если фото нет.</summary>
    public byte[]? Avatar { get; init; }
}
