using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;
using TgCheck.Core.Models;

namespace TgCheck.App.ViewModels;

public sealed record DetailRow(string Label, string Value);

public sealed partial class GiftViewModel : ObservableObject
{
    private const int ImageSize = 256;
    private IReadOnlyList<DetailRow>? _details;

    public GiftViewModel(GiftItem item)
    {
        Item = item;

        if (item.CenterColor is { } c && item.EdgeColor is { } e)
        {
            var brush = new RadialGradientBrush(Images.Rgb(c), Images.Rgb(e)) { RadiusX = 0.75, RadiusY = 0.75 };
            brush.Freeze();
            Backdrop = brush;
        }
    }

    public GiftItem Item { get; }

    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ImageSource? _senderAvatar;

    public bool HasSenderPhoto => Item.HasSenderPhoto;

    public bool IsUnique => Item.Kind == GiftKind.Unique;
    public bool CanConvert => Item.CanConvert;
    public long ConvertStars => Item.ConvertStars;
    public Brush? Backdrop { get; }

    public string Title => Item.Title ?? (IsUnique ? "Уникальный подарок" : "Подарок");

    public string? NumberText => Item.Number is { } n ? $"#{Fmt.N(n)}" : null;

    public string? Badge =>
        IsUnique ? NumberText :
        Item.IsLimited ? "лимит" :
        null;

    /// <summary>Строка под названием в карточке.</summary>
    public string Subtitle =>
        IsUnique ? Item.Model?.Name ?? "Коллекционный" :
        Item.CanConvert ? $"Продажа: {Fmt.N(Item.ConvertStars)}" :
        Item.Stars > 0 ? $"Цена: {Fmt.N(Item.Stars)}" : "";

    public bool ShowStarInSubtitle => !IsUnique && (Item.CanConvert || Item.Stars > 0);

    public string FromText => Item.FromName ?? (Item.FromId is null ? "Анонимно" : "Скрыт");

    public string DateText => Fmt.DateShort(Item.DateUtc);

    public string SellButtonText => $"Продать за {Fmt.Stars(Item.ConvertStars)}";

    public IReadOnlyList<DetailRow> Details => _details ??= BuildDetails();

    private IReadOnlyList<DetailRow> BuildDetails()
    {
        var rows = new List<DetailRow>
        {
            new("От кого", FromText),
            new("Получен", Fmt.Date(Item.DateUtc)),
        };

        if (IsUnique)
        {
            if (Item.Model is { } m) rows.Add(new("Модель", WithRarity(m)));
            if (Item.Backdrop is { } b) rows.Add(new("Фон", WithRarity(b)));
            if (Item.Pattern is { } p) rows.Add(new("Узор", WithRarity(p)));
            if (Item.Issued is { } issued)
                rows.Add(new("Выпущено", Item.AvailabilityTotal is > 0 ? $"{Fmt.N(issued)} из {Fmt.N(Item.AvailabilityTotal.Value)}" : Fmt.N(issued)));
            if (Item.ValueAmount is { } v && Item.ValueCurrency is { } cur)
                rows.Add(new("Оценка", $"≈ {Fmt.N(v / 100m)} {cur}"));
            if (Item.OriginalSender is { } os) rows.Add(new("Изначально от", os));
            if (Item.CanTransferAtUtc is { } t && t > DateTime.UtcNow) rows.Add(new("Передача доступна", Fmt.Date(t)));
            if (Item.CanResellAtUtc is { } r && r > DateTime.UtcNow) rows.Add(new("Перепродажа доступна", Fmt.Date(r)));
            if (Item.TransferStars > 0) rows.Add(new("Стоимость передачи", Fmt.Stars(Item.TransferStars)));
            if (Item.Slug is { } slug) rows.Add(new("Ссылка", $"t.me/nft/{slug}"));
        }
        else
        {
            if (Item.Stars > 0) rows.Add(new("Цена", Fmt.Stars(Item.Stars)));
            rows.Add(new("Можно продать",
                Item.CanConvert ? $"за {Fmt.Stars(Item.ConvertStars)}" :
                Item.IsConvertExpired ? "нет — срок продажи истёк" :
                Item.IsRefunded ? "нет (возврат)" : "нет"));
            if (Item.ConvertDeadlineUtc is { } d)
                rows.Add(new(Item.IsConvertExpired ? "Срок продажи истёк" : "Продать до", Fmt.Date(d)));
            if (Item.IsLimited && Item.AvailabilityTotal is { } total)
                rows.Add(new("Лимитированный", $"осталось {Fmt.N(Item.AvailabilityRemains ?? 0)} из {Fmt.N(total)}"));
            if (Item.CanUpgrade) rows.Add(new("Улучшение", Item.UpgradeStars > 0 ? $"оплачено ({Fmt.Stars(Item.UpgradeStars)})" : "доступно"));
        }

        rows.Add(new("В профиле", Item.IsHidden ? "скрыт" : Item.IsPinned ? "показан, закреплён" : "показан"));
        if (Item.IsNameHidden) rows.Add(new("Отправитель", "скрыл имя для других"));
        return rows;
    }

    private static string WithRarity(GiftAttribute a) => a.Rarity switch
    {
        { Percent: { } p } => $"{a.Name} · {p.ToString("0.#", Fmt.Ru)}%",
        { Tier: { } t } => $"{a.Name} · {t}",
        _ => a.Name,
    };

    public async Task LoadImageAsync(MediaService media)
    {
        if (Image is not null) return;
        var png = await media.GetGiftImageAsync(Item, ImageSize).ConfigureAwait(false);
        Image = Images.FromBytes(png);
        if (Item.HasSenderPhoto && SenderAvatar is null)
            SenderAvatar = Images.FromBytes(await media.GetSenderAvatarAsync(Item).ConfigureAwait(false), 64);
    }
}
