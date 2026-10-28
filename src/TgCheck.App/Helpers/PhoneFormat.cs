using System.Globalization;
using System.Text;

namespace TgCheck.App.Helpers;

/// <summary>Страна: код и маска национального номера (X — цифра).</summary>
public sealed record Country(string Iso, string Name, string Code, string Mask)
{
    public string CodeText => "+" + Code;
}

/// <summary>Справочник кодов стран и форматирование номера «на лету».</summary>
public static class PhoneFormat
{
    public static IReadOnlyList<Country> Countries { get; } =
    [
        new("RU", "Россия", "7", "XXX XXX-XX-XX"),
        new("KZ", "Казахстан", "7", "XXX XXX-XX-XX"),
        new("UA", "Украина", "380", "XX XXX XX XX"),
        new("BY", "Беларусь", "375", "XX XXX-XX-XX"),
        new("UZ", "Узбекистан", "998", "XX XXX XX XX"),
        new("KG", "Кыргызстан", "996", "XXX XXX XXX"),
        new("TJ", "Таджикистан", "992", "XX XXX XXXX"),
        new("TM", "Туркменистан", "993", "XX XXXXXX"),
        new("AZ", "Азербайджан", "994", "XX XXX XX XX"),
        new("AM", "Армения", "374", "XX XXX XXX"),
        new("GE", "Грузия", "995", "XXX XXX XXX"),
        new("MD", "Молдова", "373", "XX XXX XXX"),
        new("LT", "Литва", "370", "XXX XXXXX"),
        new("LV", "Латвия", "371", "XXX XXXXX"),
        new("EE", "Эстония", "372", "XXXX XXXX"),
        new("MN", "Монголия", "976", "XX XX XXXX"),
        new("US", "США", "1", "XXX XXX XXXX"),
        new("CA", "Канада", "1", "XXX XXX XXXX"),
        new("GB", "Великобритания", "44", "XXXX XXXXXX"),
        new("DE", "Германия", "49", "XXX XXXXXXXX"),
        new("FR", "Франция", "33", "X XX XX XX XX"),
        new("IT", "Италия", "39", "XXX XXX XXXX"),
        new("ES", "Испания", "34", "XXX XXX XXX"),
        new("PT", "Португалия", "351", "XXX XXX XXX"),
        new("NL", "Нидерланды", "31", "X XXXXXXXX"),
        new("BE", "Бельгия", "32", "XXX XX XX XX"),
        new("AT", "Австрия", "43", "XXX XXXXXXX"),
        new("CH", "Швейцария", "41", "XX XXX XX XX"),
        new("PL", "Польша", "48", "XXX XXX XXX"),
        new("CZ", "Чехия", "420", "XXX XXX XXX"),
        new("SK", "Словакия", "421", "XXX XXX XXX"),
        new("HU", "Венгрия", "36", "XX XXX XXXX"),
        new("RO", "Румыния", "40", "XXX XXX XXX"),
        new("BG", "Болгария", "359", "XX XXX XXXX"),
        new("RS", "Сербия", "381", "XX XXX XXXX"),
        new("ME", "Черногория", "382", "XX XXX XXX"),
        new("GR", "Греция", "30", "XXX XXX XXXX"),
        new("CY", "Кипр", "357", "XX XXXXXX"),
        new("FI", "Финляндия", "358", "XX XXX XX XX"),
        new("SE", "Швеция", "46", "XX XXX XX XX"),
        new("NO", "Норвегия", "47", "XXX XX XXX"),
        new("DK", "Дания", "45", "XX XX XX XX"),
        new("TR", "Турция", "90", "XXX XXX XXXX"),
        new("IL", "Израиль", "972", "XX XXX XXXX"),
        new("AE", "ОАЭ", "971", "XX XXX XXXX"),
        new("SA", "Саудовская Аравия", "966", "XX XXX XXXX"),
        new("QA", "Катар", "974", "XX XX XXXX"),
        new("EG", "Египет", "20", "XX XXXX XXXX"),
        new("IR", "Иран", "98", "XXX XXX XXXX"),
        new("IQ", "Ирак", "964", "XXX XXX XXXX"),
        new("AF", "Афганистан", "93", "XX XXX XXXX"),
        new("IN", "Индия", "91", "XXXXX XXXXX"),
        new("PK", "Пакистан", "92", "XXX XXXXXXX"),
        new("CN", "Китай", "86", "XXX XXXX XXXX"),
        new("JP", "Япония", "81", "XX XXXX XXXX"),
        new("KR", "Южная Корея", "82", "XX XXXX XXXX"),
        new("VN", "Вьетнам", "84", "XX XXX XX XX"),
        new("TH", "Таиланд", "66", "X XXXX XXXX"),
        new("MY", "Малайзия", "60", "XX XXXX XXXX"),
        new("ID", "Индонезия", "62", "XXX XXXX XXXX"),
        new("PH", "Филиппины", "63", "XXX XXX XXXX"),
        new("AU", "Австралия", "61", "XXX XXX XXX"),
        new("BR", "Бразилия", "55", "XX XXXXX XXXX"),
        new("AR", "Аргентина", "54", "XX XXXX XXXX"),
        new("MX", "Мексика", "52", "XX XXXX XXXX"),
        new("NG", "Нигерия", "234", "XXX XXX XXXX"),
        new("ZA", "ЮАР", "27", "XX XXX XXXX"),
    ];

    private const int MaxCodeLength = 4;
    private const int MaxNumberLength = 15;

    public static string Digits(string? s) => new((s ?? "").Where(char.IsAsciiDigit).ToArray());

    /// <summary>Страна по коду. Для +7 номер на 6/7 — Казахстан; явный выбор пользователя сохраняется.</summary>
    public static Country? Detect(string code, string nationalDigits, Country? preferred)
    {
        if (preferred is not null && preferred.Code == code) return preferred;
        if (code == "7" && nationalDigits.Length > 0 && nationalDigits[0] is '6' or '7')
            return Countries.First(c => c.Iso == "KZ");
        return Countries.FirstOrDefault(c => c.Code == code);
    }

    /// <summary>Есть ли более длинные коды, начинающиеся с этих цифр (тогда ещё рано переходить к номеру).</summary>
    public static bool IsCodeComplete(string code) =>
        Countries.Any(c => c.Code == code) && !Countries.Any(c => c.Code.Length > code.Length && c.Code.StartsWith(code, StringComparison.Ordinal));

    public static bool IsCodePrefix(string code) => Countries.Any(c => c.Code.StartsWith(code, StringComparison.Ordinal));

    /// <summary>Делит «код+номер» на код страны (самое длинное совпадение) и остаток.</summary>
    public static (string Code, string Tail) Split(string digits)
    {
        for (var len = Math.Min(MaxCodeLength, digits.Length); len > 0; len--)
        {
            var code = digits[..len];
            if (Countries.Any(c => c.Code == code)) return (code, digits[len..]);
        }
        return (digits[..Math.Min(MaxCodeLength, digits.Length)], digits[Math.Min(MaxCodeLength, digits.Length)..]);
    }

    /// <summary>Форматирует цифры номера по маске страны; без маски — группами по 3.</summary>
    public static string Format(string digits, string? mask)
    {
        if (digits.Length > MaxNumberLength) digits = digits[..MaxNumberLength];
        var sb = new StringBuilder();
        if (mask is null)
        {
            for (var i = 0; i < digits.Length; i++)
            {
                if (i > 0 && i % 3 == 0) sb.Append(' ');
                sb.Append(digits[i]);
            }
            return sb.ToString();
        }

        var d = 0;
        foreach (var m in mask)
        {
            if (d >= digits.Length) break;
            sb.Append(m == 'X' ? digits[d++] : m);
        }
        // Цифры сверх маски — просто дописываем, Telegram сам проверит номер.
        if (d < digits.Length) sb.Append(digits[d..]);
        return sb.ToString();
    }

    /// <summary>Оставшаяся (незаполненная) часть маски — показывается серым после введённых цифр.</summary>
    public static string MaskRest(string digits, string? mask)
    {
        if (mask is null) return digits.Length == 0 ? "000 000 0000" : "";
        var total = mask.Count(c => c == 'X');
        if (digits.Length >= total) return "";
        var d = 0;
        var i = 0;
        for (; i < mask.Length && d < digits.Length; i++)
            if (mask[i] == 'X') d++;
        return mask[i..].Replace('X', '0');
    }

    public static Country? ByRegion()
    {
        try
        {
            var iso = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return Countries.FirstOrDefault(c => c.Iso == iso);
        }
        catch
        {
            return null;
        }
    }
}
