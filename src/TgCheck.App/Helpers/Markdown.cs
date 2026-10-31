using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TgCheck.App.Helpers;

/// <summary>
/// Простой Markdown для описания релиза с GitHub: заголовки, списки, **жирный**, *курсив*, `код`, [ссылки](url),
/// разделители. Строится обычными TextBlock — в стиле приложения.
/// </summary>
public static partial class Markdown
{
    public static void Render(Panel target, string? markdown)
    {
        target.Children.Clear();
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            target.Children.Add(Text(string.Join(" ", paragraph), 14, FontWeights.Normal, new Thickness(0, 0, 0, 10)));
            paragraph.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { FlushParagraph(); continue; }

            if (HeadingRx().Match(trimmed) is { Success: true } h)
            {
                FlushParagraph();
                var level = h.Groups[1].Length;
                var size = level switch { 1 => 22, 2 => 18, _ => 15.5 };
                target.Children.Add(Text(h.Groups[2].Value, size, FontWeights.SemiBold,
                    new Thickness(0, target.Children.Count == 0 ? 0 : 10, 0, 8)));
                continue;
            }
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                target.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 6, 0, 12) });
                continue;
            }
            if (BulletRx().Match(line) is { Success: true } b)
            {
                FlushParagraph();
                var indent = b.Groups[1].Value.Replace("\t", "  ").Length / 2;
                target.Children.Add(ListItem("•", b.Groups[2].Value, indent));
                continue;
            }
            if (NumberRx().Match(line) is { Success: true } n)
            {
                FlushParagraph();
                var indent = n.Groups[1].Value.Length / 2;
                target.Children.Add(ListItem(n.Groups[2].Value + ".", n.Groups[3].Value, indent));
                continue;
            }
            // Строка целиком **жирная** — как подзаголовок раздела (так написаны наши описания релизов).
            if (trimmed.StartsWith("**") && trimmed.EndsWith("**") && trimmed.Length > 4 && !trimmed[2..^2].Contains("**"))
            {
                FlushParagraph();
                target.Children.Add(Text(trimmed[2..^2], 15.5, FontWeights.SemiBold,
                    new Thickness(0, target.Children.Count == 0 ? 0 : 8, 0, 6), Res("AccentBrush")));
                continue;
            }
            paragraph.Add(trimmed);
        }
        FlushParagraph();
    }

    private static FrameworkElement ListItem(string marker, string text, int indent)
    {
        var grid = new Grid { Margin = new Thickness(4 + indent * 18, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var dot = new TextBlock { Text = marker, Foreground = Res("AccentBrush"), FontSize = 14, FontWeight = FontWeights.SemiBold };
        var body = Text(text, 14, FontWeights.Normal, new Thickness(0));
        Grid.SetColumn(body, 1);
        grid.Children.Add(dot);
        grid.Children.Add(body);
        return grid;
    }

    private static TextBlock Text(string text, double size, FontWeight weight, Thickness margin, Brush? color = null)
    {
        var tb = new TextBlock
        {
            FontSize = size, FontWeight = weight, Margin = margin, TextWrapping = TextWrapping.Wrap,
            Foreground = color ?? Res("TextBrush"), LineHeight = size * 1.4,
        };
        AddInlines(tb.Inlines, text);
        return tb;
    }

    // **жирный**, *курсив* / _курсив_, `код`, [текст](ссылка), голые https://…
    private static void AddInlines(InlineCollection inlines, string text)
    {
        var pos = 0;
        foreach (Match m in InlineRx().Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));
            if (m.Groups["bold"].Success) inlines.Add(new Bold(new Run(m.Groups["bold"].Value)));
            else if (m.Groups["italic"].Success) inlines.Add(new Italic(new Run(m.Groups["italic"].Value)));
            else if (m.Groups["italic2"].Success) inlines.Add(new Italic(new Run(m.Groups["italic2"].Value)));
            else if (m.Groups["code"].Success)
                inlines.Add(new Run(m.Groups["code"].Value) { FontFamily = new FontFamily("Consolas"), Background = Res("Surface2Brush") });
            else if (m.Groups["label"].Success) inlines.Add(Link(m.Groups["label"].Value, m.Groups["url"].Value));
            else inlines.Add(Link(m.Value, m.Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
    }

    private static Hyperlink Link(string label, string url)
    {
        var link = new Hyperlink(new Run(label)) { Foreground = Res("AccentBrush"), TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
        link.Click += (_, _) => Shell.Open(url);
        return link;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRx();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletRx();

    [GeneratedRegex(@"^(\s*)(\d+)[.)]\s+(.*)$")]
    private static partial Regex NumberRx();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|(?<!\w)_(?<italic2>[^_\s][^_]*?)_(?!\w)|`(?<code>[^`]+)`|\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\)|https?://[^\s)]+")]
    private static partial Regex InlineRx();
}
