using TL;

namespace TgCheck.Core.Models;

public enum GiftKind { Regular, Unique }

/// <summary>Редкость атрибута уникального подарка: либо процент, либо именованный уровень.</summary>
public sealed record GiftRarity(double? Percent, string? Tier);

public sealed record GiftAttribute(string Name, GiftRarity? Rarity);

/// <summary>Подарок из профиля пользователя (обычный или уникальный/NFT).</summary>
public sealed class GiftItem
{
    public required GiftKind Kind { get; init; }
    public long GiftId { get; init; }
    public string? Title { get; init; }

    public DateTime DateUtc { get; init; }
    public long? FromId { get; init; }
    public string? FromName { get; init; }
    public string? Message { get; init; }

    /// <summary>Подарок скрыт с профиля.</summary>
    public bool IsHidden { get; init; }
    /// <summary>Отправитель скрыл своё имя для других.</summary>
    public bool IsNameHidden { get; init; }
    public bool IsPinned { get; init; }
    public bool IsRefunded { get; init; }

    // ---- обычный подарок ----
    /// <summary>Цена подарка в звёздах.</summary>
    public long Stars { get; init; }
    /// <summary>Сколько звёзд дадут при продаже (конвертации).</summary>
    public long ConvertStars { get; init; }
    /// <summary>Можно продать прямо сейчас (есть цена продажи и срок ещё не истёк).</summary>
    public bool CanConvert { get; init; }
    /// <summary>До какого момента подарок можно продать (null — продать нельзя в принципе).</summary>
    public DateTime? ConvertDeadlineUtc { get; init; }
    /// <summary>Подарок можно было продать, но срок уже истёк.</summary>
    public bool IsConvertExpired => ConvertDeadlineUtc is { } d && d <= DateTime.UtcNow;
    public bool IsLimited { get; init; }
    public bool IsSoldOut { get; init; }
    public int? AvailabilityRemains { get; init; }
    public int? AvailabilityTotal { get; init; }
    public bool CanUpgrade { get; init; }
    public long UpgradeStars { get; init; }

    // ---- уникальный подарок ----
    public int? Number { get; init; }
    public string? Slug { get; init; }
    public GiftAttribute? Model { get; init; }
    public GiftAttribute? Backdrop { get; init; }
    public GiftAttribute? Pattern { get; init; }
    public int? Issued { get; init; }
    public long? ValueAmount { get; init; }
    public string? ValueCurrency { get; init; }
    public DateTime? CanTransferAtUtc { get; init; }
    public DateTime? CanResellAtUtc { get; init; }
    public DateTime? CanExportAtUtc { get; init; }
    public long TransferStars { get; init; }
    public string? OriginalSender { get; init; }
    public string? OriginalMessage { get; init; }

    /// <summary>Цвета фона (RGB): у уникальных — backdrop, у новых обычных — background.</summary>
    public int? CenterColor { get; init; }
    public int? EdgeColor { get; init; }
    public int? PatternColor { get; init; }
    public int? TextColor { get; init; }

    public string? NftUrl => Slug is null ? null : $"https://t.me/nft/{Slug}";

    /// <summary>У отправителя есть аватар (его можно получить через MediaService.GetSenderAvatarAsync).</summary>
    public bool HasSenderPhoto => FromPeer switch
    {
        User u => u.photo is not null,
        Channel c => c.photo is ChatPhoto,
        Chat c => c.photo is ChatPhoto,
        _ => false,
    };

    // ---- внутренние данные для бэкенда ----
    internal IPeerInfo? FromPeer { get; init; }

    /// <summary>Отправитель (для открытия его профиля), null — анонимно/скрыт.</summary>
    public PeerRef? Sender { get; init; }
    internal InputSavedStarGift? InputRef { get; init; }
    internal DocumentBase? Sticker { get; init; }
    internal DocumentBase? PatternDocument { get; init; }

    internal string ImageKey(int size) =>
        $"{Sticker?.ID ?? GiftId}_{PatternDocument?.ID ?? 0}_{CenterColor:x}_{EdgeColor:x}_{PatternColor:x}_{size}";
}
