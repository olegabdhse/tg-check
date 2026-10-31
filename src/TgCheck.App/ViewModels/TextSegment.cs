using TgCheck.Core.Models;

namespace TgCheck.App.ViewModels;

/// <summary>
/// Кусок текста сообщения: обычный текст (со своим оформлением и эмодзи) или блок кода с языком.
/// Блоки кода показываются отдельными элементами во всю ширину пузыря, как в Telegram.
/// </summary>
public sealed record TextSegment(string Text, bool IsCode, string? Language,
    IReadOnlyList<TextStyleSpan> Formatting, IReadOnlyList<CustomEmojiSpan> Emoji)
{
    // Не указан — определяем по содержимому (как подпись; сам блок от этого не меняется).
    public string LanguageText => !string.IsNullOrWhiteSpace(Language) ? Language.Trim() : TgCheck.Core.Services.CodeLanguage.Detect(Text) ?? "код";

    public static IReadOnlyList<TextSegment> Split(string text, IReadOnlyList<TextStyleSpan> formatting, IReadOnlyList<CustomEmojiSpan> emoji)
    {
        var blocks = formatting.Where(f => f.Kind == TextStyleKind.Pre && f.Offset >= 0 && f.Offset + f.Length <= text.Length)
            .OrderBy(f => f.Offset).ToList();
        var result = new List<TextSegment>();
        var pos = 0;
        foreach (var block in blocks)
        {
            if (block.Offset < pos) continue; // вложенный или пересекающийся блок
            AddText(result, text, pos, block.Offset, formatting, emoji);
            result.Add(new TextSegment(text.Substring(block.Offset, block.Length).Trim('\n'), true, block.Data, [], []));
            pos = block.Offset + block.Length;
        }
        AddText(result, text, pos, text.Length, formatting, emoji);
        return result;
    }

    // Текст между блоками: переносы строк у краёв блока не нужны (блок и так на отдельной строке).
    private static void AddText(List<TextSegment> result, string text, int start, int end,
        IReadOnlyList<TextStyleSpan> formatting, IReadOnlyList<CustomEmojiSpan> emoji)
    {
        while (start < end && text[start] == '\n') start++;
        while (end > start && text[end - 1] == '\n') end--;
        if (end <= start || string.IsNullOrWhiteSpace(text[start..end])) return;
        var styles = formatting
            .Where(f => f.Kind != TextStyleKind.Pre && f.Offset < end && f.Offset + f.Length > start)
            .Select(f =>
            {
                var s = Math.Max(f.Offset, start);
                var e = Math.Min(f.Offset + f.Length, end);
                return f with { Offset = s - start, Length = e - s };
            }).ToList();
        var emo = emoji.Where(c => c.Offset >= start && c.Offset + c.Length <= end)
            .Select(c => c with { Offset = c.Offset - start }).ToList();
        result.Add(new TextSegment(text[start..end], false, null, styles, emo));
    }
}
