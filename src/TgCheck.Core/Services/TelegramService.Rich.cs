using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

/// <summary>
/// «Сложные сообщения» Telegram (rich_message): статьи с заголовками и абзацами, фото и видео внутри текста,
/// коллажи и слайд-шоу, музыка. Внутри — блоки как у Instant View и общие списки фото/документов,
/// на которые блоки ссылаются по id. Разбор устойчив к новым видам блоков: всё, где есть текст, — абзац,
/// вложенные блоки разбираются рекурсивно.
/// </summary>
public sealed partial class TelegramService
{
    private List<RichBlock> MapRich(MessageItem owner, RichMessage rich)
    {
        var photos = (rich.photos ?? []).OfType<Photo>().ToDictionary(p => p.id);
        var docs = (rich.documents ?? []).OfType<Document>().ToDictionary(d => d.id);
        var result = new List<RichBlock>();
        foreach (var block in rich.blocks ?? []) AddBlock(block, result, owner, photos, docs);
        return result;
    }

    private void AddBlock(PageBlock block, List<RichBlock> result, MessageItem owner,
        Dictionary<long, Photo> photos, Dictionary<long, Document> docs)
    {
        switch (block)
        {
            case PageBlockPhoto p when photos.TryGetValue(p.photo_id, out var photo):
                result.Add(new RichBlock { Kind = RichBlockKind.Media, Items = [WithMedia(owner, new MessageMediaPhoto { photo = photo })] });
                AddCaption(p.caption, result);
                return;
            case PageBlockVideo v when docs.TryGetValue(v.video_id, out var video):
                result.Add(new RichBlock { Kind = RichBlockKind.Media, Items = [WithMedia(owner, new MessageMediaDocument { document = video })] });
                AddCaption(v.caption, result);
                return;
            case PageBlockAudio a when docs.TryGetValue(a.audio_id, out var audio):
                result.Add(new RichBlock { Kind = RichBlockKind.Media, Items = [WithMedia(owner, new MessageMediaDocument { document = audio })] });
                AddCaption(a.caption, result);
                return;
            case PageBlockSlideshow s:
                AddGallery(s.items, s.caption, result, owner, photos, docs, slideshow: true); // одним окном со стрелками и точками

                return;
            case PageBlockCollage c:
                AddGallery(c.items, c.caption, result, owner, photos, docs);
                return;
            case PageBlockDivider:
                result.Add(new RichBlock { Kind = RichBlockKind.Divider });
                return;
            case PageBlockPreformatted pre:
                AddText(RichKind.Code, Plain(pre.text), result);
                return;
            case PageBlockBlockquote q:
                AddText(RichKind.Quote, Plain(q.text), result);
                return;
            case PageBlockPullquote pq:
                AddText(RichKind.Quote, Plain(pq.text), result);
                return;
            case PageBlockList list:
                AddText(RichKind.Paragraph, string.Join("\n", list.items.Select(i => "•  " + ListItemText(i))), result);
                return;
            case PageBlockOrderedList ol:
                AddText(RichKind.Paragraph, string.Join("\n", ol.items.Select((i, n) => $"{n + 1}.  " + ListItemText(i))), result);
                return;
        }

        // Остальное (заголовки разных уровней, абзацы и новые виды блоков) — по полям: text — текст, blocks/items — вложенные.
        var type = block.GetType();
        var name = type.Name;
        if (type.GetField("text")?.GetValue(block) is RichText text)
        {
            var heading = name.Contains("Title") || name.Contains("Header") || name.Contains("Heading") || name.Contains("Kicker");
            AddText(heading ? RichKind.Heading : RichKind.Paragraph, Plain(text), result);
        }
        foreach (var field in new[] { "blocks", "items" })
            if (type.GetField(field)?.GetValue(block) is System.Collections.IEnumerable children)
                foreach (var child in children.OfType<PageBlock>()) AddBlock(child, result, owner, photos, docs);
        if (type.GetField("caption")?.GetValue(block) is PageCaption caption) AddCaption(caption, result);
    }

    private void AddGallery(PageBlock[] items, PageCaption? caption, List<RichBlock> result, MessageItem owner,
        Dictionary<long, Photo> photos, Dictionary<long, Document> docs, bool slideshow = false)
    {
        var media = new List<MessageItem>();
        foreach (var item in items)
        {
            if (item is PageBlockPhoto p && photos.TryGetValue(p.photo_id, out var photo)) media.Add(WithMedia(owner, new MessageMediaPhoto { photo = photo }));
            else if (item is PageBlockVideo v && docs.TryGetValue(v.video_id, out var video)) media.Add(WithMedia(owner, new MessageMediaDocument { document = video }));
        }
        if (media.Count == 1) result.Add(new RichBlock { Kind = RichBlockKind.Media, Items = media });
        else if (media.Count > 1) result.Add(new RichBlock { Kind = slideshow ? RichBlockKind.Slideshow : RichBlockKind.Album, Items = media });
        AddCaption(caption, result);
    }

    private enum RichKind { Heading, Paragraph, Quote, Code }

    private static void AddText(RichKind kind, string text, List<RichBlock> result)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        result.Add(new RichBlock
        {
            Kind = kind switch { RichKind.Heading => RichBlockKind.Heading, RichKind.Quote => RichBlockKind.Quote, RichKind.Code => RichBlockKind.Code, _ => RichBlockKind.Paragraph },
            Text = text.Trim(),
        });
    }

    private static void AddCaption(PageCaption? caption, List<RichBlock> result)
    {
        if (caption is null) return;
        AddText(RichKind.Paragraph, Plain(caption.text), result);
    }

    // Пункт списка (обычного или нумерованного): текст или вложенные блоки.
    private static string ListItemText(object item) => item.GetType().GetField("text")?.GetValue(item) is RichText text ? Plain(text) : item switch
    {
        _ => item.GetType().GetField("blocks")?.GetValue(item) is PageBlock[] blocks
            ? string.Join(" ", blocks.Select(b => b.GetType().GetField("text")?.GetValue(b) is RichText rt ? Plain(rt) : ""))
            : "",
    };

    /// <summary>Текст из RichText (жирный/курсив/ссылки и новые виды — по полям text/texts).</summary>
    internal static string Plain(RichText? text)
    {
        switch (text)
        {
            case null: return "";
            case TextPlain p: return p.text ?? "";
            case TextConcat c: return string.Concat(c.texts.Select(Plain));
        }
        var type = text.GetType();
        if (type.GetField("text")?.GetValue(text) is { } inner)
            return inner switch { string s => s, RichText rt => Plain(rt), _ => "" };
        if (type.GetField("texts")?.GetValue(text) is RichText[] many) return string.Concat(many.Select(Plain));
        return "";
    }

    /// <summary>Короткий текст сложного сообщения — для списка чатов и уведомлений.</summary>
    public static string RichPreview(IReadOnlyList<RichBlock> blocks) =>
        blocks.FirstOrDefault(b => b.Text is { Length: > 0 })?.Text?.Split('\n')[0]
        ?? (blocks.Any(b => b.Kind is RichBlockKind.Album) ? "Альбом" : blocks.Any(b => b.Kind is RichBlockKind.Media) ? "Медиа" : "Пост");
}
