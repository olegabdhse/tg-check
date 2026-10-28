using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace TgCheck.App.Helpers;

/// <summary>
/// Цветные эмодзи. WPF рисует Segoe UI Emoji монохромно, поэтому эмодзи рендерятся через Skia
/// (DirectWrite + HarfBuzz для составных эмодзи и флагов) в картинки и кэшируются.
/// </summary>
public static class EmojiImages
{
    private const int Px = 64;
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();
    private static readonly SKTypeface? Face = SKTypeface.FromFamilyName("Segoe UI Emoji");
    private static readonly object RenderLock = new();

    public static ImageSource? Get(string emoji) => Cache.GetOrAdd(emoji, Render);

    private static ImageSource? Render(string emoji)
    {
        if (Face is null) return null;
        try
        {
            lock (RenderLock)
            {
                using var bmp = new SKBitmap(new SKImageInfo(Px, Px, SKColorType.Bgra8888, SKAlphaType.Premul));
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(SKColors.Transparent);
                using var font = new SKFont(Face, Px * 0.82f);
                using var paint = new SKPaint { IsAntialias = true };
                using var shaper = new SKShaper(Face);
                var shaped = shaper.Shape(emoji, font);
                var width = shaped.Width;
                font.GetFontMetrics(out var m);
                var x = (Px - width) / 2f;
                var y = Px / 2f - (m.Ascent + m.Descent) / 2f;
                canvas.DrawShapedText(shaper, emoji, x, y, SKTextAlign.Left, font, paint);
                var src = BitmapSource.Create(Px, Px, 96, 96, PixelFormats.Pbgra32, null, bmp.Bytes, bmp.RowBytes);
                src.Freeze();
                return src;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Является ли текстовый элемент (графема) эмодзи.</summary>
    public static bool IsEmoji(string element)
    {
        if (element.Length == 0) return false;
        if (element.Contains('️') || element.Contains('‍') || element.Contains('⃣')) return element.Length > 1;
        var r = Rune.GetRuneAt(element, 0).Value;
        return r is >= 0x1F000 and <= 0x1FAFF
            or >= 0x2600 and <= 0x27BF
            or >= 0x2B00 and <= 0x2BFF
            or >= 0x231A and <= 0x23FF
            or 0x203C or 0x2049 or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x24C2;
    }

    /// <summary>Разбивает текст на куски: (текст, false) и (эмодзи, true).</summary>
    public static IEnumerable<(string Text, bool IsEmoji)> Split(string text)
    {
        var sb = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            if (IsEmoji(element))
            {
                if (sb.Length > 0) { yield return (sb.ToString(), false); sb.Clear(); }
                yield return (element, true);
            }
            else sb.Append(element);
        }
        if (sb.Length > 0) yield return (sb.ToString(), false);
    }
}

/// <summary>Категории эмодзи для панели выбора.</summary>
public static class EmojiData
{
    public sealed record Category(string Icon, string Title, IReadOnlyList<string> Items);

    private static IReadOnlyList<string> L(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static IReadOnlyList<Category> Categories { get; } =
    [
        new("😀", "Смайлики", L("😀 😃 😄 😁 😆 😅 🤣 😂 🙂 🙃 🫠 😉 😊 😇 🥰 😍 🤩 😘 😗 ☺️ 😚 😙 🥲 😋 😛 😜 🤪 😝 🤑 🤗 🤭 🫢 🫣 🤫 🤔 🫡 🤐 🤨 😐 😑 😶 🫥 😏 😒 🙄 😬 🤥 😌 😔 😪 🤤 😴 😷 🤒 🤕 🤢 🤮 🤧 🥵 🥶 🥴 😵 🤯 🤠 🥳 🥸 😎 🤓 🧐 😕 🫤 😟 🙁 ☹️ 😮 😯 😲 😳 🥺 🥹 😦 😧 😨 😰 😥 😢 😭 😱 😖 😣 😞 😓 😩 😫 🥱 😤 😡 😠 🤬 😈 👿 💀 ☠️ 💩 🤡 👹 👺 👻 👽 👾 🤖 😺 😸 😹 😻 😼 😽 🙀 😿 😾")),
        new("👍", "Жесты и люди", L("👋 🤚 🖐️ ✋ 🖖 🫱 🫲 👌 🤌 🤏 ✌️ 🤞 🫰 🤟 🤘 🤙 👈 👉 👆 🖕 👇 ☝️ 🫵 👍 👎 ✊ 👊 🤛 🤜 👏 🙌 🫶 👐 🤲 🤝 🙏 ✍️ 💅 🤳 💪 🦾 🧠 👀 👁️ 👅 👄 🫦 👶 🧒 👦 👧 🧑 👱 👨 🧔 👩 🧓 👴 👵 🙍 🙎 🙅 🙆 💁 🙋 🧏 🙇 🤦 🤷 👮 🕵️ 💂 👷 🤴 👸 👳 🧕 🤵 👰 🤰 🤱 👼 🎅 🤶 🦸 🦹 🧙 🧚 🧛 🧜 🧝 🧞 🧟 💆 💇 🚶 🧍 🧎 🏃 💃 🕺 👯 🧖 🧗 🏇 ⛷️ 🏂 🏌️ 🏄 🚣 🏊 ⛹️ 🏋️ 🚴 🚵 🤸 🤼 🤽 🤾 🤹 🧘 💑 👪")),
        new("❤️", "Сердца и символы", L("❤️ 🧡 💛 💚 💙 💜 🖤 🤍 🤎 💔 ❤️‍🔥 ❤️‍🩹 ❣️ 💕 💞 💓 💗 💖 💘 💝 💟 💯 💢 💥 💫 💦 💨 🕳️ 💬 👁️‍🗨️ 🗨️ 🗯️ 💭 💤 ✅ ☑️ ✔️ ❌ ❎ ➕ ➖ ➗ ✖️ ♾️ ‼️ ⁉️ ❓ ❔ ❕ ❗ 〰️ ⚠️ 🚫 ⛔ 📛 🔞 ♻️ 🔱 ⭕ 🆗 🆕 🆒 🆓 🆙 🔝 🔥 ✨ ⭐ 🌟 ⚡ 🎉 🎊 🎈 🎁 🏆 🥇 🥈 🥉 🔔 🔕 🎵 🎶 💲 💰 💎 🔒 🔓 🔑 🗝️ 🔍 💡 🕐 ⏰ ⏳ ⌛ 📍 🏁 🚩 🏳️ 🏴")),
        new("🐶", "Животные и природа", L("🐶 🐱 🐭 🐹 🐰 🦊 🐻 🐼 🐻‍❄️ 🐨 🐯 🦁 🐮 🐷 🐽 🐸 🐵 🙈 🙉 🙊 🐒 🐔 🐧 🐦 🐤 🐣 🐥 🦆 🦅 🦉 🦇 🐺 🐗 🐴 🦄 🐝 🪱 🐛 🦋 🐌 🐞 🐜 🪰 🪲 🪳 🦟 🦗 🕷️ 🦂 🐢 🐍 🦎 🦖 🦕 🐙 🦑 🦐 🦞 🦀 🐡 🐠 🐟 🐬 🐳 🐋 🦈 🐊 🐅 🐆 🦓 🦍 🦧 🐘 🦛 🦏 🐪 🐫 🦒 🦘 🐃 🐂 🐄 🐎 🐖 🐏 🐑 🦙 🐐 🦌 🐕 🐩 🐈 🐓 🦃 🦚 🦜 🦢 🦩 🕊️ 🐇 🦝 🦨 🦡 🦫 🦦 🦥 🐁 🐀 🐿️ 🦔 🌵 🎄 🌲 🌳 🌴 🌱 🌿 ☘️ 🍀 🍃 🍂 🍁 🍄 🌾 💐 🌷 🌹 🥀 🌺 🌸 🌼 🌻 🌞 🌝 🌚 🌙 🌎 🪐 ⭐ ☀️ 🌤️ ⛅ 🌧️ ⛈️ 🌩️ ❄️ ☃️ ⛄ 🌈 🌊 💧")),
        new("🍔", "Еда и напитки", L("🍏 🍎 🍐 🍊 🍋 🍌 🍉 🍇 🍓 🫐 🍈 🍒 🍑 🥭 🍍 🥥 🥝 🍅 🍆 🥑 🥦 🥬 🥒 🌶️ 🫑 🌽 🥕 🧄 🧅 🥔 🍠 🥐 🥯 🍞 🥖 🥨 🧀 🥚 🍳 🧈 🥞 🧇 🥓 🥩 🍗 🍖 🌭 🍔 🍟 🍕 🫓 🥪 🥙 🧆 🌮 🌯 🫔 🥗 🥘 🫕 🥫 🍝 🍜 🍲 🍛 🍣 🍱 🥟 🦪 🍤 🍙 🍚 🍘 🍥 🥠 🥮 🍢 🍡 🍧 🍨 🍦 🥧 🧁 🍰 🎂 🍮 🍭 🍬 🍫 🍿 🍩 🍪 🌰 🥜 🍯 🥛 🍼 🫖 ☕ 🍵 🧃 🥤 🧋 🍶 🍺 🍻 🥂 🍷 🥃 🍸 🍹 🧉 🍾 🧊")),
        new("⚽", "Занятия", L("⚽ 🏀 🏈 ⚾ 🥎 🎾 🏐 🏉 🥏 🎱 🪀 🏓 🏸 🏒 🏑 🥍 🏏 🪃 🥅 ⛳ 🪁 🏹 🎣 🤿 🥊 🥋 🎽 🛹 🛼 🛷 ⛸️ 🥌 🎿 🎯 🎮 🕹️ 🎲 🧩 ♟️ 🎭 🎨 🎬 🎤 🎧 🎼 🎹 🥁 🎷 🎺 🎸 🪕 🎻 🎳 🏅 🎖️ 🎗️ 🎟️ 🎫")),
        new("🚗", "Путешествия", L("🚗 🚕 🚙 🚌 🚎 🏎️ 🚓 🚑 🚒 🚐 🛻 🚚 🚛 🚜 🏍️ 🛵 🚲 🛴 🚨 🚔 🚍 🚘 🚖 🚡 🚠 🚟 🚃 🚋 🚞 🚝 🚄 🚅 🚈 🚂 🚆 🚇 🚊 🚉 ✈️ 🛫 🛬 🛩️ 💺 🛰️ 🚀 🛸 🚁 🛶 ⛵ 🚤 🛥️ 🛳️ ⛴️ 🚢 ⚓ 🗺️ 🗿 🗽 🗼 🏰 🏯 🏟️ 🎡 🎢 🎠 ⛲ ⛱️ 🏖️ 🏝️ 🏜️ 🌋 ⛰️ 🏔️ 🗻 🏕️ ⛺ 🏠 🏡 🏘️ 🏚️ 🏗️ 🏭 🏢 🏬 🏣 🏤 🏥 🏦 🏨 🏪 🏫 🏩 💒 🏛️ ⛪ 🕌 🕍 🛕 🌁 🌃 🏙️ 🌄 🌅 🌆 🌇 🌉 🎆 🎇")),
        new("💡", "Предметы", L("⌚ 📱 💻 ⌨️ 🖥️ 🖨️ 🖱️ 💽 💾 💿 📀 📷 📸 📹 🎥 📞 ☎️ 📺 📻 🎙️ ⏱️ ⏲️ 🔋 🔌 💡 🔦 🕯️ 🧯 🛢️ 💸 💵 💴 💶 💷 🪙 💳 🧾 ⚖️ 🧰 🔧 🔨 ⚒️ 🛠️ ⛏️ 🔩 ⚙️ 🧱 ⛓️ 🧲 🔫 💣 🧨 🪓 🔪 🗡️ ⚔️ 🛡️ 🚬 ⚰️ 🔮 📿 💈 ⚗️ 🔭 🔬 💊 💉 🩸 🧬 🦠 🧫 🧪 🌡️ 🧹 🧺 🧻 🚽 🚰 🚿 🛁 🧼 🪥 🧽 🛎️ 🔑 🚪 🪑 🛋️ 🛏️ 🧸 🖼️ 🛍️ 🛒 🎁 🎈 🎀 🪄 📦 📫 📮 📜 📃 📄 📑 📊 📈 📉 🗒️ 📆 📅 🗑️ 📇 🗃️ 📋 📁 📂 📰 📓 📔 📒 📕 📗 📘 📙 📚 📖 🔖 🔗 📎 📐 📏 📌 ✂️ 🖊️ 🖋️ ✒️ 🖌️ 🖍️ 📝 ✏️ 🔍 🔎")),
        new("🏁", "Флаги", L("🏁 🚩 🎌 🏴 🏳️ 🏳️‍🌈 🏴‍☠️ 🇷🇺 🇺🇦 🇧🇾 🇰🇿 🇺🇿 🇰🇬 🇹🇯 🇹🇲 🇦🇿 🇦🇲 🇬🇪 🇲🇩 🇱🇹 🇱🇻 🇪🇪 🇺🇸 🇬🇧 🇩🇪 🇫🇷 🇮🇹 🇪🇸 🇵🇹 🇵🇱 🇨🇿 🇹🇷 🇮🇱 🇦🇪 🇸🇦 🇪🇬 🇮🇳 🇨🇳 🇯🇵 🇰🇷 🇻🇳 🇹🇭 🇮🇩 🇧🇷 🇦🇷 🇲🇽 🇨🇦 🇦🇺 🇳🇱 🇧🇪 🇨🇭 🇦🇹 🇸🇪 🇳🇴 🇫🇮 🇩🇰")),
    ];
}
