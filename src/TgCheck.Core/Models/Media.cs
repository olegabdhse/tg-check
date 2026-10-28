using TL;

namespace TgCheck.Core.Models;

public enum StickerFormat { Static, Animated, Video }

/// <summary>Что-то с фото или файлом Telegram (сообщение, стикер, GIF, история).</summary>
public abstract class MediaHolder
{
    internal Photo? Photo { get; init; }
    internal Document? Document { get; init; }

    public bool CanDownload => Document is not null || Photo is not null;
    public long MediaId => Photo?.id ?? Document?.id ?? 0;
    public string? MimeType => Document?.mime_type;
    public double DurationSeconds { get; init; }
    /// <summary>Волна голосового: 0..31 на столбик.</summary>
    public IReadOnlyList<byte>? Waveform { get; init; }

    public StickerFormat StickerFormat => Document?.mime_type switch
    {
        "application/x-tgsticker" => StickerFormat.Animated,
        "video/webm" => StickerFormat.Video,
        _ => StickerFormat.Static,
    };
}

/// <summary>Стикер из набора / недавних.</summary>
public sealed class StickerItem : MediaHolder
{
    public string? Emoji { get; init; }
}

/// <summary>Сохранённая GIF.</summary>
public sealed class GifItem : MediaHolder
{
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed class StickerSetInfo
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public int Count { get; init; }
    internal long AccessHash { get; init; }
}

public sealed class PollOption
{
    public string Text { get; init; } = "";
    internal string Option { get; init; } = "";
    public int Voters { get; init; }
    public bool Chosen { get; init; }
    public bool Correct { get; init; }
    public double Percent { get; init; }
}

public sealed class PollInfo
{
    public long Id { get; init; }
    public string Question { get; init; } = "";
    public IReadOnlyList<PollOption> Options { get; init; } = [];
    public int TotalVoters { get; init; }
    public bool IsClosed { get; init; }
    public bool IsQuiz { get; init; }
    public bool IsMultiple { get; init; }
    public bool IsPublic { get; init; }
    public string? Solution { get; init; }
    public bool HasVoted => Options.Any(o => o.Chosen);

    /// <summary>В обновлениях Telegram у вариантов может не быть текста — дополнить из прежней версии опроса.</summary>
    public PollInfo WithTextsFrom(PollInfo old)
    {
        var texts = old.Options.ToDictionary(o => o.Option, o => o.Text);
        return new PollInfo
        {
            Id = Id, TotalVoters = TotalVoters, IsClosed = IsClosed || old.IsClosed, IsQuiz = old.IsQuiz,
            IsMultiple = old.IsMultiple, IsPublic = old.IsPublic, Question = Question.Length > 0 && Question != "Опрос" ? Question : old.Question,
            Solution = Solution ?? old.Solution,
            Options = Options.Select(o => new PollOption
            {
                Text = o.Text.Length > 0 ? o.Text : texts.GetValueOrDefault(o.Option, ""), Option = o.Option,
                Voters = o.Voters, Chosen = o.Chosen, Correct = o.Correct, Percent = o.Percent,
            }).ToList(),
        };
    }
    public bool ShowResults => HasVoted || IsClosed;
}

/// <summary>Одна история.</summary>
public sealed class StoryInfo : MediaHolder
{
    public int Id { get; init; }
    public DateTime DateUtc { get; init; }
    public string? Caption { get; init; }
    public bool IsVideo => Document is not null;
}

/// <summary>Истории одного пользователя/канала.</summary>
public sealed class StoryPeer
{
    public required PeerRef Peer { get; init; }
    public IReadOnlyList<StoryInfo> Stories { get; init; } = [];
    public int MaxReadId { get; set; }
    public bool HasUnread => Stories.Any(s => s.Id > MaxReadId);
}

public enum SharedMediaKind { Media, Files, Voice, Music }

public enum BotButtonKind { Callback, Url, WebApp, SimpleWebApp, Copy, Profile, SwitchInline, Text, Other }

/// <summary>Кнопка под сообщением (инлайн) или кнопка клавиатуры бота.</summary>
public sealed class BotButton
{
    public required string Text { get; init; }
    public BotButtonKind Kind { get; init; }
    public string? Url { get; init; }
    public string? CopyText { get; init; }
    public long UserId { get; init; }
    internal byte[]? Data { get; init; }
}

/// <summary>Кнопка меню бота / главное мини-приложение.</summary>
public sealed record BotMenu(string Text, string? Url, bool HasMainApp);

/// <summary>Ответ бота на нажатие кнопки.</summary>
public sealed record BotCallbackResult(string? Message, string? Url, bool IsAlert);

/// <summary>Реакция под сообщением: обычный эмодзи, премиум-эмодзи (CustomEmojiId) или звёзды.</summary>
public sealed record ReactionInfo(string? Emoji, long CustomEmojiId, int Count, bool Chosen, bool IsPaid = false)
{
    public string Key => CustomEmojiId != 0 ? "c" + CustomEmojiId : IsPaid ? "paid" : Emoji ?? "";
}
