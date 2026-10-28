using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TgCheck.App.Helpers;

/// <summary>Текст сообщения: кликабельные ссылки и цветные эмодзи.</summary>
public static partial class RichText
{
    [GeneratedRegex(@"(https?://[^\s<>""]+|(?:www\.|t\.me/)[^\s<>""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(RichText), new PropertyMetadata(null, OnChanged));

    /// <summary>Размер эмодзи в пикселях (по умолчанию — чуть больше шрифта).</summary>
    public static readonly DependencyProperty EmojiSizeProperty = DependencyProperty.RegisterAttached(
        "EmojiSize", typeof(double), typeof(RichText), new PropertyMetadata(0d, OnChanged));

    /// <summary>Кастомные (премиум) эмодзи: участки текста, которые заменяются анимированными стикерами.</summary>
    public static readonly DependencyProperty CustomEmojiProperty = DependencyProperty.RegisterAttached(
        "CustomEmoji", typeof(IReadOnlyList<TgCheck.Core.Models.CustomEmojiSpan>), typeof(RichText), new PropertyMetadata(null, OnChanged));

    /// <summary>
    /// Пустое место в конце текста под время и галочки (как в Telegram): если на последней строке
    /// хватает места — время встаёт рядом с текстом, иначе переносится на новую строку справа.
    /// </summary>
    public static readonly DependencyProperty TrailingSpaceProperty = DependencyProperty.RegisterAttached(
        "TrailingSpace", typeof(double), typeof(RichText), new PropertyMetadata(0d, OnChanged));

    public static double GetTrailingSpace(DependencyObject d) => (double)d.GetValue(TrailingSpaceProperty);
    public static void SetTrailingSpace(DependencyObject d, double v) => d.SetValue(TrailingSpaceProperty, v);

    public static IReadOnlyList<TgCheck.Core.Models.CustomEmojiSpan>? GetCustomEmoji(DependencyObject d) => (IReadOnlyList<TgCheck.Core.Models.CustomEmojiSpan>?)d.GetValue(CustomEmojiProperty);
    public static void SetCustomEmoji(DependencyObject d, IReadOnlyList<TgCheck.Core.Models.CustomEmojiSpan>? v) => d.SetValue(CustomEmojiProperty, v);

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? v) => d.SetValue(TextProperty, v);
    public static double GetEmojiSize(DependencyObject d) => (double)d.GetValue(EmojiSizeProperty);
    public static void SetEmojiSize(DependencyObject d, double v) => d.SetValue(EmojiSizeProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        Build(tb);
        if (GetTrailingSpace(tb) is > 0 and var trailing)
            tb.Inlines.Add(new InlineUIContainer(new FrameworkElement { Width = trailing, Height = 1 }) { BaselineAlignment = BaselineAlignment.Bottom });
    }

    private static void Build(TextBlock tb)
    {
        tb.Inlines.Clear();
        var text = GetText(tb) ?? "";
        var size = GetEmojiSize(tb) is > 0 and var s ? s : tb.FontSize * 1.3;

        // Кастомные эмодзи режут текст на куски: между ними — обычная обработка (ссылки, эмодзи).
        var spans = GetCustomEmoji(tb)?.Where(c => c.Offset >= 0 && c.Offset + c.Length <= text.Length).OrderBy(c => c.Offset).ToList();
        if (spans is { Count: > 0 })
        {
            var cursor = 0;
            foreach (var span in spans)
            {
                if (span.Offset < cursor) continue;
                if (span.Offset > cursor) AddLinked(tb.Inlines, text[cursor..span.Offset], size);
                var sticker = new TgCheck.App.Controls.StickerView { CustomEmojiId = span.DocumentId, Width = size, Height = size, ToolTip = text.Substring(span.Offset, span.Length) };
                tb.Inlines.Add(new InlineUIContainer(sticker) { BaselineAlignment = BaselineAlignment.Center });
                cursor = span.Offset + span.Length;
            }
            if (cursor < text.Length) AddLinked(tb.Inlines, text[cursor..], size);
            return;
        }
        AddLinked(tb.Inlines, text, size);
    }

    private static void AddLinked(InlineCollection inlines, string text, double size)
    {
        var pos = 0;
        foreach (Match m in UrlRegex().Matches(text))
        {
            if (m.Index > pos) AddText(inlines, text[pos..m.Index], size);
            var url = m.Value.TrimEnd('.', ',', ')', '!', '?', ';', ':');
            var link = new Hyperlink(new Run(url)) { ToolTip = url };
            if (Application.Current.TryFindResource("LinkStyle") is Style style) link.Style = style;
            var target = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
            link.Click += (_, _) => Shell.Open(target);
            inlines.Add(link);
            pos = m.Index + url.Length;
        }
        if (pos < text.Length) AddText(inlines, text[pos..], size);
    }

    private static void AddText(InlineCollection inlines, string text, double emojiSize)
    {
        foreach (var (part, isEmoji) in EmojiImages.Split(text))
        {
            if (isEmoji && EmojiImages.Get(part) is { } img)
            {
                var image = new Image { Source = img, Width = emojiSize, Height = emojiSize, ToolTip = part };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
            }
            else
            {
                inlines.Add(new Run(part));
            }
        }
    }

    /// <summary>Сообщение из 1–3 эмодзи без текста — показывается крупно.</summary>
    public static bool IsEmojiOnly(string? text, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = EmojiImages.Split(text.Trim()).ToList();
        return parts.Count <= max && parts.All(p => p.IsEmoji || string.IsNullOrWhiteSpace(p.Text)) && parts.Any(p => p.IsEmoji);
    }
}
