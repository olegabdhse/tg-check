using System.Text;
using TL;

namespace TgCheck.Core.Services;

/// <summary>
/// Разметка при отправке — как в Telegram Desktop: ```язык\nкод``` — блок кода, `код`, **жирный**, __курсив__,
/// ~~зачёркнутый~~, ||спойлер||. Значки убираются из текста, вместо них — оформление (entities).
/// Непарные значки остаются как есть.
/// </summary>
public static class MarkdownEntities
{
    private static readonly (string Mark, Func<int, int, MessageEntity> Make)[] Pairs =
    [
        ("**", (o, l) => new MessageEntityBold { offset = o, length = l }),
        ("__", (o, l) => new MessageEntityItalic { offset = o, length = l }),
        ("~~", (o, l) => new MessageEntityStrike { offset = o, length = l }),
        ("||", (o, l) => new MessageEntitySpoiler { offset = o, length = l }),
    ];

    // Языки блоков кода (```shell): первая строка — язык, только если она из этого списка, иначе это часть кода.
    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell", "sh", "bash", "zsh", "powershell", "ps1", "pwsh", "cmd", "bat", "batch", "console",
        "c", "cpp", "c++", "h", "hpp", "cs", "csharp", "c#", "fs", "fsharp", "vb", "java", "kotlin", "kt", "scala", "groovy",
        "go", "golang", "rust", "rs", "swift", "objc", "objective-c", "dart", "zig", "nim",
        "python", "py", "ruby", "rb", "php", "perl", "pl", "lua", "r", "julia", "haskell", "hs", "elixir", "erlang", "clojure", "lisp",
        "js", "javascript", "jsx", "ts", "typescript", "tsx", "node", "json", "json5", "yaml", "yml", "toml", "ini", "xml", "html", "htm",
        "css", "scss", "sass", "less", "vue", "svelte", "graphql", "gql", "sql", "mysql", "postgresql", "psql", "sqlite", "plsql",
        "markdown", "md", "tex", "latex", "dockerfile", "docker", "makefile", "make", "cmake", "nginx", "apache", "diff", "patch",
        "asm", "nasm", "x86asm", "arm", "wasm", "solidity", "sol", "vim", "regex", "proto", "protobuf", "xaml", "razor", "text", "txt", "plaintext", "log",
    };

    /// <summary>Текст без значков разметки и оформление (null — оформления нет).</summary>
    public static (string Text, MessageEntity[]? Entities) Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOfAny(['`', '*', '_', '~', '|']) < 0) return (text, null);
        var sb = new StringBuilder(text.Length);
        var entities = new List<MessageEntity>();
        ParseInto(text, sb, entities, allowBlocks: true);
        return (sb.ToString(), entities.Count > 0 ? [.. entities.OrderBy(e => e.offset)] : null);
    }

    private static void ParseInto(string text, StringBuilder sb, List<MessageEntity> entities, bool allowBlocks)
    {
        var i = 0;
        while (i < text.Length)
        {
            // ```язык\nкод``` — блок кода (внутри разметка не разбирается)
            if (allowBlocks && string.CompareOrdinal(text, i, "```", 0, 3) == 0)
            {
                var close = text.IndexOf("```", i + 3, StringComparison.Ordinal);
                if (close > i + 3)
                {
                    var inner = text[(i + 3)..close];
                    string? language = null;
                    var nl = inner.IndexOf('\n');
                    // Первая строка — язык, если это известный язык (```shell, ```cs).
                    if (nl > 0 && Languages.Contains(inner[..nl].Trim()))
                    {
                        language = inner[..nl].Trim();
                        inner = inner[(nl + 1)..];
                    }
                    var code = inner.Trim('\n', '\r');
                    if (code.Length > 0)
                    {
                        // Язык не указан — определяем по содержимому (чтобы и в других клиентах был подписан).
                        entities.Add(new MessageEntityPre { offset = sb.Length, length = code.Length, language = language ?? CodeLanguage.Detect(code) ?? "" });
                        sb.Append(code);
                        i = close + 3;
                        continue;
                    }
                }
            }
            // `код` — в пределах строки
            if (text[i] == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1 && text.IndexOf('\n', i + 1, close - i - 1) < 0)
                {
                    var code = text[(i + 1)..close];
                    entities.Add(new MessageEntityCode { offset = sb.Length, length = code.Length });
                    sb.Append(code);
                    i = close + 1;
                    continue;
                }
            }
            // **жирный**, __курсив__, ~~зачёркнутый~~, ||спойлер|| — внутри может быть другая разметка
            var matched = false;
            foreach (var (mark, make) in Pairs)
            {
                if (string.CompareOrdinal(text, i, mark, 0, 2) != 0) continue;
                var close = text.IndexOf(mark, i + 2, StringComparison.Ordinal);
                if (close <= i + 2) continue;
                var inner = text[(i + 2)..close];
                if (char.IsWhiteSpace(inner[0]) || char.IsWhiteSpace(inner[^1])) continue;
                var start = sb.Length;
                ParseInto(inner, sb, entities, allowBlocks: false);
                if (sb.Length > start) entities.Add(make(start, sb.Length - start));
                i = close + 2;
                matched = true;
                break;
            }
            if (matched) continue;
            sb.Append(text[i]);
            i++;
        }
    }
}
