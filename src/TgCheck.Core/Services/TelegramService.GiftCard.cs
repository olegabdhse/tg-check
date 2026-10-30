using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

// Подарки в чате: вместо строки «🎁 … отправил(а) подарок» — карточка как в Telegram (стикер подарка, от кого, сообщение, цена).
public sealed partial class TelegramService
{
    private static GiftCard? MapGiftCard(MessageService s, PeerRef? from)
    {
        var isOut = s.flags.HasFlag(MessageService.Flags.out_);
        var who = from?.DisplayTitle;
        var fromWho = who is null ? "Подарок" : $"Подарок от {who}"; // отправитель неизвестен (свой подарок, «Избранное») — просто «Подарок»
        switch (s.action)
        {
            case MessageActionStarGift g:
            {
                var gift = g.gift as StarGift;
                var hidden = g.flags.HasFlag(MessageActionStarGift.Flags.name_hidden);
                string? note = null;
                if (g.flags.HasFlag(MessageActionStarGift.Flags.converted)) note = $"Обменян на {g.convert_stars} ⭐";
                else if (g.flags.HasFlag(MessageActionStarGift.Flags.upgraded)) note = "Улучшен до уникального";
                else if (!isOut && g.convert_stars > 0)
                    note = $"Можно оставить в профиле или обменять на {g.convert_stars} ⭐";
                return new GiftCard
                {
                    Sticker = gift?.sticker is Document d ? ToSticker(d) : null,
                    Caption = isOut ? "Вы отправили подарок" : hidden ? "Подарок от скрытого отправителя" : fromWho,
                    Title = g.gift?.Title,
                    Message = g.message?.text is { Length: > 0 } text ? text : null,
                    Note = note,
                    Stars = gift?.stars ?? 0,
                };
            }
            case MessageActionStarGiftUnique u when u.gift is StarGiftUnique gift:
            {
                var model = gift.attributes?.OfType<StarGiftAttributeModel>().FirstOrDefault();
                var backdrop = gift.attributes?.OfType<StarGiftAttributeBackdrop>().FirstOrDefault();
                var caption = u.flags.HasFlag(MessageActionStarGiftUnique.Flags.upgrade)
                    ? (isOut || who is null ? "Подарок улучшен" : $"{who} улучшил(а) подарок")
                    : u.flags.HasFlag(MessageActionStarGiftUnique.Flags.transferred)
                        ? (isOut ? "Вы передали подарок" : who is null ? "Подарок передан вам" : $"{who} передал(а) вам подарок")
                        : isOut ? "Вы отправили подарок" : fromWho;
                return new GiftCard
                {
                    Sticker = model?.document is Document d ? ToSticker(d) : null,
                    Caption = caption,
                    Title = gift.num > 0 ? $"{gift.title} #{gift.num:N0}".Replace(' ', ' ') : gift.title,
                    Note = model is null ? null : $"Модель: {model.name}" + (backdrop is null ? "" : $" · Фон: {backdrop.name}"),
                    IsUnique = true,
                    CenterColor = backdrop?.center_color,
                    EdgeColor = backdrop?.edge_color,
                    Pattern = gift.attributes?.OfType<StarGiftAttributePattern>().FirstOrDefault()?.document as Document,
                    PatternColor = backdrop?.pattern_color,
                };
            }
            default:
                return null;
        }
    }
}
