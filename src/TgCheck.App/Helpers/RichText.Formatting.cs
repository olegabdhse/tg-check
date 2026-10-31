using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TgCheck.Core.Models;

namespace TgCheck.App.Helpers;

// Оформление текста как в Telegram: жирный, курсив, подчёркнутый, зачёркнутый, `код`, блоки кода с языком
// и кнопкой «копировать», ссылки под текстом, скрытый текст (спойлер), цитаты.
public static partial class RichText
{
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Brush CodeBrush = Frozen(Color.FromRgb(0x8F, 0xD3, 0xFF));
    private static readonly Brush QuoteBrush = Frozen(Color.FromRgb(0xCF, 0xE3, 0xF5));
    private static readonly Brush SpoilerBrush = Frozen(Color.FromArgb(0x66, 0x9A, 0xA8, 0xB6));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static void BuildFormatted(TextBlock tb, string text, List<TextStyleSpan> styles, double size)
    {
        // Блоки кода в сообщениях рисует отдельный элемент (TextSegment); здесь они — просто моноширинный текст, без вложенных окон.
        styles = styles.Select(s => s.Kind == TextStyleKind.Pre ? s with { Kind = TextStyleKind.Code } : s).ToList();
        var emoji = GetCustomEmoji(tb)?.Where(c => c.Offset >= 0 && c.Offset + c.Length <= text.Length).ToList() ?? [];
        // Границы участков: начало и конец каждого оформления и кастомного эмодзи.
        var cuts = new SortedSet<int> { 0, text.Length };
        foreach (var s in styles) { cuts.Add(s.Offset); cuts.Add(s.Offset + s.Length); }
        foreach (var e in emoji) { cuts.Add(e.Offset); cuts.Add(e.Offset + e.Length); }

        var pos = 0;
        while (pos < text.Length)
        {
            if (emoji.FirstOrDefault(e => e.Offset == pos) is { } em)
            {
                var sticker = new TgCheck.App.Controls.StickerView { Once = true, Animate = _animate, CustomEmojiId = em.DocumentId, Width = size, Height = size, ToolTip = text.Substring(em.Offset, em.Length) };
                tb.Inlines.Add(new InlineUIContainer(sticker) { BaselineAlignment = BaselineAlignment.Center });
                pos = em.Offset + em.Length;
                continue;
            }
            var next = cuts.GetViewBetween(pos + 1, text.Length).Min;
            var active = styles.Where(s => s.Offset <= pos && s.Offset + s.Length >= next).ToList();
            AddStyled(tb.Inlines, text[pos..next], active, size);
            pos = next;
        }
    }

    private static void AddStyled(InlineCollection inlines, string text, List<TextStyleSpan> active, double size)
    {
        if (active.Count == 0) { AddLinked(inlines, text, size); return; }
        var url = active.FirstOrDefault(s => s.Kind == TextStyleKind.TextUrl)?.Data;
        Span span;
        if (url is not null)
        {
            var link = new Hyperlink { ToolTip = url };
            if (Application.Current.TryFindResource("LinkStyle") is Style style) link.Style = style;
            var target = url.Contains("://") || url.StartsWith("tg:", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
            link.Click += (_, _) => Shell.Open(target);
            span = link;
        }
        else span = new Span();

        var decorations = new TextDecorationCollection();
        foreach (var s in active)
        {
            switch (s.Kind)
            {
                case TextStyleKind.Bold: span.FontWeight = FontWeights.SemiBold; break;
                case TextStyleKind.Italic: span.FontStyle = FontStyles.Italic; break;
                case TextStyleKind.Underline: decorations.Add(TextDecorations.Underline); break;
                case TextStyleKind.Strike: decorations.Add(TextDecorations.Strikethrough); break;
                case TextStyleKind.Code:
                    span.FontFamily = CodeFont;
                    span.Foreground = CodeBrush;
                    break;
                case TextStyleKind.Quote:
                    span.FontStyle = FontStyles.Italic;
                    span.Foreground = QuoteBrush;
                    break;
            }
        }
        if (decorations.Count > 0) span.TextDecorations = decorations;

        if (active.Any(s => s.Kind == TextStyleKind.Code) || url is not null) span.Inlines.Add(new Run(text)); // в коде и ссылке — как есть
        else AddLinked(span.Inlines, text, size);

        // Спойлер: текст скрыт плашкой, по нажатию открывается.
        if (active.Any(s => s.Kind == TextStyleKind.Spoiler))
        {
            var original = span.Foreground;
            var hidden = true;
            span.Background = SpoilerBrush;
            span.Foreground = Brushes.Transparent;
            span.Cursor = System.Windows.Input.Cursors.Hand;
            span.ToolTip = "Скрытый текст — нажмите, чтобы показать";
            span.MouseLeftButtonDown += (_, e) =>
            {
                if (!hidden) return;
                hidden = false;
                span.Background = null;
                if (original is null) span.ClearValue(TextElement.ForegroundProperty); else span.Foreground = original;
                span.Cursor = null;
                span.ToolTip = null;
                e.Handled = true;
            };
        }
        inlines.Add(span);
    }
}
