using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Аватар пира: фото (подгружается лениво) или инициалы на цветном фоне.</summary>
public partial class PeerAvatarViewModel : ObservableObject
{
    private bool _requested;

    public PeerAvatarViewModel(PeerRef peer)
    {
        Peer = peer;
    }

    public PeerRef Peer { get; protected set; }
    public string Title => Peer.DisplayTitle;
    public string Initials => Peer.IsSelf ? "★" : AvatarColors.Initials(Title);
    public Brush AvatarBrush => AvatarColors.For(Peer.Id);

    [ObservableProperty] private ImageSource? _avatar;
    /// <summary>У пира есть истории (кольцо вокруг аватара).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStoryRing), nameof(StoryRing))] private bool _hasStories;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StoryRing))] private bool _storiesUnread;

    /// <summary>Рамка аватара: градиент — непросмотренные истории, серая — просмотренные, нет — нет историй.</summary>
    public Brush? StoryRing => !HasStories ? null : StoriesUnread ? AvatarColors.StoryUnread : AvatarColors.StorySeen;

    public bool HasStoryRing => HasStories;

    public async void EnsureAvatar(MediaService media)
    {
        if (_requested || !Peer.HasPhoto) return;
        _requested = true;
        var bytes = await media.GetPeerAvatarAsync(Peer).ConfigureAwait(false);
        Avatar = Images.FromBytes(bytes, 128);
    }
}

/// <summary>Строка в списке чатов.</summary>
public sealed partial class ChatItemViewModel : PeerAvatarViewModel
{
    public ChatItemViewModel(ChatSummary s, int order) : base(s.Peer)
    {
        Order = order;
        Update(s);
    }

    /// <summary>Обновить из свежих данных (кэш → сеть) без пересоздания строки.</summary>
    public void Update(ChatSummary s)
    {
        if (s.Last is not null && (Last is null || s.Last.Id >= Last.Id || s.Last.EditDateUtc != Last.EditDateUtc)) Last = s.Last;
        Unread = s.UnreadCount;
        Mentions = s.UnreadMentions;
        ReadOutboxMaxId = Math.Max(ReadOutboxMaxId, s.ReadOutboxMaxId);
        IsPinned = s.IsPinned;
        IsMuted = s.IsMuted;
        IsArchived = s.IsArchived;
        IsMarkedUnread = s.IsMarkedUnread;
    }

    /// <summary>Исходный порядок из Telegram (важен для закреплённых).</summary>
    public int Order { get; set; }

    public long Key => Peer.Key;
    public bool IsPersonal => Peer.Kind == PeerKind.User;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LastText), nameof(TimeText), nameof(ShowChecks), nameof(IsRead), nameof(SortDate))]
    private MessageItem? _last;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText), nameof(UnreadRing))]
    private int _unread;

    /// <summary>Рамка в «островке»: градиент — есть непрочитанные.</summary>
    public Brush UnreadRing => HasUnread ? AvatarColors.StoryUnread : AvatarColors.StorySeen;

    [ObservableProperty] private int _mentions;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsRead))] private int _readOutboxMaxId;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isArchived;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText))] private bool _isMarkedUnread;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LastText), nameof(IsTyping))] private string? _typingText;
    [ObservableProperty] private bool _isOnline;

    /// <summary>Когда вы последний раз писали в этот чат (для «островка»).</summary>
    public DateTime? LastOutgoingUtc { get; set; }

    partial void OnLastChanged(MessageItem? value)
    {
        if (value is { IsOut: true } m && (LastOutgoingUtc is null || m.DateUtc > LastOutgoingUtc)) LastOutgoingUtc = m.DateUtc;
    }

    public bool IsTyping => TypingText is not null;
    public bool HasUnread => Unread > 0 || IsMarkedUnread;
    public string UnreadText => Unread > 999 ? $"{Unread / 1000}K" : Unread > 0 ? Unread.ToString(Fmt.Ru) : "";
    public DateTime SortDate => Last?.DateUtc ?? DateTime.MinValue;

    public string LastText
    {
        get
        {
            if (TypingText is { } t) return t;
            if (Last is null) return "";
            var prefix = Last.IsOut && Last.ServiceText is null ? "Вы: "
                : Peer.Kind == PeerKind.Group && Last.From is { } f && Last.ServiceText is null ? f.DisplayTitle + ": "
                : "";
            return prefix + Last.Preview;
        }
    }

    public string TimeText => Last is null ? "" : Fmt.ChatTime(Last.DateUtc);
    public bool ShowChecks => Last is { IsOut: true, ServiceText: null } && !Peer.IsSelf && Peer.Kind != PeerKind.Channel;
    public bool IsRead => Last is not null && ReadOutboxMaxId >= Last.Id;
}

/// <summary>Результат глобального поиска.</summary>
public sealed class PeerResultViewModel(PeerRef peer) : PeerAvatarViewModel(peer)
{
    public string Subtitle => Peer.Username is { } u ? "@" + u : Peer.Kind switch
    {
        PeerKind.Channel => "канал",
        PeerKind.Group => "группа",
        PeerKind.Bot => "бот",
        _ => "пользователь",
    };
}

/// <summary>Кружок в ленте историй.</summary>
public sealed partial class StoryPeerViewModel : PeerAvatarViewModel
{
    public StoryPeerViewModel(StoryPeer stories) : base(stories.Peer)
    {
        Stories = stories;
        HasStories = true;
        StoriesUnread = stories.HasUnread;
    }

    public StoryPeer Stories { get; }
    public string ShortTitle => Peer.IsSelf ? "Вы" : Title.Split(' ')[0];

    public void MarkRead(int maxId)
    {
        Stories.MaxReadId = Math.Max(Stories.MaxReadId, maxId);
        StoriesUnread = Stories.HasUnread;
    }
}

public sealed class DateSeparatorViewModel(DateTime localDate)
{
    public DateTime Date { get; } = localDate;
    public string Text { get; } = Fmt.DayHeader(localDate);
}

public enum MediaLayout { None, PhotoVideo, Gif, Sticker, Round, Voice, Poll, File, VisualAlbum, FileAlbum, Map }

/// <summary>Кнопка под сообщением бота/канала.</summary>
/// <summary>Быстрая реакция в меню сообщения.</summary>
public sealed record QuickReactionItem(MessageViewModel Message, string Emoji);

/// <summary>Плашка реакции под сообщением.</summary>
public sealed class ReactionViewModel(MessageViewModel message, ReactionInfo info)
{
    public MessageViewModel Message { get; } = message;
    public ReactionInfo Info { get; } = info;
    public string? Emoji => Info.CustomEmojiId == 0 ? Info.Emoji : null;
    public long CustomEmojiId => Info.CustomEmojiId;
    public bool IsCustom => Info.CustomEmojiId != 0;
    public string CountText => Fmt.N(Info.Count);
    public bool Chosen => Info.Chosen;
}

public sealed class InlineButtonViewModel(MessageViewModel message, BotButton button)
{
    public MessageViewModel Message { get; } = message;
    public BotButton Button { get; } = button;
    public string Text => Button.Text;
    public string? Icon => Button.Kind switch
    {
        BotButtonKind.Url => "\uE8A7",
        BotButtonKind.WebApp or BotButtonKind.SimpleWebApp => "\uE7C4",
        BotButtonKind.Copy => "\uE8C8",
        _ => null,
    };
}

/// <summary>Вариант ответа в опросе.</summary>
public sealed partial class PollOptionViewModel(PollOption option, PollInfo poll) : ObservableObject
{
    public PollOption Option { get; } = option;
    public string Text => Option.Text;
    public bool ShowResults => poll.ShowResults;
    public double Percent => Option.Percent;
    public string PercentText => $"{Math.Round(Option.Percent * 100)}%";
    public bool Chosen => Option.Chosen;
    public bool Correct => poll.IsQuiz && Option.Correct;
    public bool Wrong => poll.IsQuiz && Option.Chosen && !Option.Correct;
    public bool IsMultiple => poll.IsMultiple && !poll.ShowResults;

    /// <summary>Отмечен для голосования (множественный выбор).</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Сообщение в открытом чате (или альбом из нескольких).</summary>
public sealed partial class MessageViewModel : ObservableObject
{
    private const double MaxMediaWidth = 360, MaxMediaHeight = 400;
    private bool _previewRequested;

    public MessageViewModel(MessageItem item, PeerRef chat)
    {
        Chat = chat;
        _item = item;
        _text = item.Text;
        _isEdited = item.EditDateUtc is not null;
        RebuildPoll();
    }

    public PeerRef Chat { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceDurationText))] private MessageItem _item;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasText), nameof(IsJumboEmoji), nameof(TextEmojiSize))] private string _text;
    [ObservableProperty] private bool _isEdited;
    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private ImageSource? _senderAvatar;
    [ObservableProperty] private string? _replySender;
    [ObservableProperty] private string? _replyText;
    [ObservableProperty] private Brush? _replyBrush;
    [ObservableProperty] private double _downloadProgress = -1;

    // ---- голосовые / аудио
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PlayIcon))] private bool _isPlaying;
    [ObservableProperty] private double _playProgress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceDurationText))] private string? _positionText;
    [ObservableProperty] private bool _isLoadingAudio;

    public string PlayIcon => IsPlaying ? "" : "";
    public string VoiceDurationText => PositionText ?? Fmt.Duration(Item.DurationSeconds);
    public string AudioKey => $"{Chat.Key}:{Item.Id}";

    /// <summary>Столбики волны голосового (высоты 3..26 px).</summary>
    public IReadOnlyList<double> WaveBars { get; private set; } = [];

    /// <summary>Первое сообщение в серии подряд от одного отправителя.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSenderName))] private bool _isFirstInGroup = true;
    /// <summary>Последнее в серии — у него показывается аватар (в группах).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSenderAvatar))] private bool _isLastInGroup = true;

    // ---- альбом
    /// <summary>Части альбома (сообщения с одинаковым GroupedId). Пусто — обычное сообщение.</summary>
    public ObservableCollection<MessageViewModel> Album { get; } = [];
    public bool IsAlbum => Album.Count > 1;
    public bool IsVisualAlbum => IsAlbum && Album.All(a => a.IsVisualMedia);
    public bool IsFileAlbum => IsAlbum && !IsVisualAlbum;
    public int AlbumColumns => Album.Count switch { <= 1 => 1, 2 => 2, 4 => 2, _ => 3 };
    public double AlbumCellWidth => Math.Floor((MaxMediaWidth - AlbumColumns * 3) / AlbumColumns);
    public double AlbumCellHeight => Album.Count == 2 ? 200 : AlbumCellWidth;
    public IEnumerable<int> AllIds => IsAlbum ? Album.Select(a => a.Id) : [Id];

    public void AddToAlbum(MessageViewModel part, MediaService media)
    {
        if (Album.Count == 0)
        {
            var first = Clone();
            first.EnsurePreview(media);
            Album.Add(first);
        }
        Album.Add(part);
        part.EnsurePreview(media);
        var ordered = Album.OrderBy(a => a.Id).ToList();
        Album.Clear();
        foreach (var a in ordered) Album.Add(a);
        // Подпись альбома — текст из той части, где он есть.
        if (Album.FirstOrDefault(a => a.HasText) is { } captioned) Text = captioned.Text;
        NotifyAlbum();
    }

    public void RemoveFromAlbum(int id)
    {
        if (Album.FirstOrDefault(a => a.Id == id) is { } part) Album.Remove(part);
        NotifyAlbum();
    }

    private void NotifyAlbum()
    {
        foreach (var name in new[] { nameof(Layout), nameof(TextMaxWidth), nameof(IsAlbum), nameof(IsVisualAlbum), nameof(IsFileAlbum), nameof(AlbumColumns),
                     nameof(AlbumCellWidth), nameof(AlbumCellHeight), nameof(IsVisualMedia), nameof(IsFileRow), nameof(IsBubbleless),
                     nameof(IsPhoto), nameof(IsVideo), nameof(IsGif), nameof(IsVoice), nameof(IsRound), nameof(IsSticker) })
            OnPropertyChanged(name);
    }

    // Первая часть альбома — отдельная модель со своей загрузкой превью (иначе картинка оставалась у оригинала).
    private MessageViewModel Clone() => new(Item, Chat);

    // ---- опрос
    public ObservableCollection<PollOptionViewModel> PollOptions { get; } = [];
    [ObservableProperty] private bool _isVoting;
    public PollInfo? Poll => Item.Poll;
    public bool IsPoll => Item.Poll is not null;
    public string PollKind => Poll is null ? "" :
        (Poll.IsClosed ? "Итоги " : "") + (Poll.IsQuiz ? "Викторина" : Poll.IsPublic ? "Опрос" : "Анонимный опрос");
    public string PollVotersText => Poll is null ? "" : Poll.TotalVoters == 0 ? "Пока никто не голосовал"
        : $"{Fmt.N(Poll.TotalVoters)} {Fmt.Plural(Poll.TotalVoters, "голос", "голоса", "голосов")}";
    public bool CanRetractVote => Poll is { HasVoted: true, IsClosed: false, IsQuiz: false };
    public bool ShowVoteButton => Poll is { IsMultiple: true, ShowResults: false };

    public void SetPoll(PollInfo poll)
    {
        if (Item.Poll is { } old) poll = poll.WithTextsFrom(old);
        Item.Poll = poll;
        RebuildPoll();
    }

    private void RebuildPoll()
    {
        PollOptions.Clear();
        if (Item.Poll is not { } poll) return;
        foreach (var o in poll.Options) PollOptions.Add(new PollOptionViewModel(o, poll));
        foreach (var name in new[] { nameof(Poll), nameof(IsPoll), nameof(PollKind), nameof(PollVotersText), nameof(CanRetractVote), nameof(ShowVoteButton) })
            OnPropertyChanged(name);
    }

    /// <summary>Какой кусок шаблона нужен — строится только он (тяжёлые части для других типов не создаются).</summary>
    public MediaLayout Layout =>
        IsVisualAlbum ? MediaLayout.VisualAlbum :
        IsFileAlbum ? MediaLayout.FileAlbum :
        Item.Poll is not null ? MediaLayout.Poll :
        Item.Media switch
        {
            MediaKind.Photo or MediaKind.Video => MediaLayout.PhotoVideo,
            MediaKind.Gif => MediaLayout.Gif,
            MediaKind.Sticker => MediaLayout.Sticker,
            MediaKind.VideoNote => MediaLayout.Round,
            MediaKind.Voice or MediaKind.Audio => MediaLayout.Voice,
            MediaKind.Location when Item.Latitude is not null => MediaLayout.Map,
            MediaKind.Document or MediaKind.Contact or MediaKind.Location or MediaKind.Other => MediaLayout.File,
            _ => MediaLayout.None,
        };

    /// <summary>Подпись к фото/альбому не шире самого медиа (как в Telegram).</summary>
    public double TextMaxWidth => IsVisualAlbum ? MaxMediaWidth - 12
        : Item.Media is MediaKind.Photo or MediaKind.Video or MediaKind.Gif ? Math.Max(140, MediaWidth - 12) : 496;

    // ---- кнопки ботов и комментарии
    private IReadOnlyList<IReadOnlyList<InlineButtonViewModel>>? _inlineRows;
    public IReadOnlyList<IReadOnlyList<InlineButtonViewModel>> InlineRows =>
        _inlineRows ??= Item.InlineButtons.Select(r => (IReadOnlyList<InlineButtonViewModel>)r.Select(b => new InlineButtonViewModel(this, b)).ToList()).ToList();
    public bool HasInlineButtons => Item.InlineButtons.Count > 0;
    public bool HasComments => Item.Comments is not null;

    // ---- реакции
    private ObservableCollection<ReactionViewModel>? _reactions;
    public ObservableCollection<ReactionViewModel> Reactions => _reactions ??= new(Item.Reactions.Select(r => new ReactionViewModel(this, r)));
    public bool HasReactions => Reactions.Count > 0;
    public IReadOnlyList<QuickReactionItem> QuickReactionItems => _quick ??= TelegramService.QuickReactions.Take(8).Select(e => new QuickReactionItem(this, e)).ToList();
    private IReadOnlyList<QuickReactionItem>? _quick;
    public IReadOnlyList<ReactionInfo> ReactionInfos => Reactions.Select(r => r.Info).ToList();

    public void SetReactions(IReadOnlyList<ReactionInfo> reactions)
    {
        Reactions.Clear();
        foreach (var r in reactions) Reactions.Add(new ReactionViewModel(this, r));
        OnPropertyChanged(nameof(HasReactions));
    }
    public string CommentsText => Item.Comments is not { } n || n == 0 ? "Оставить комментарий"
        : $"{Fmt.N(n)} {Fmt.Plural(n, "комментарий", "комментария", "комментариев")}";
    /// <summary>Подсветка после перехода к сообщению (закреп, ответ).</summary>
    [ObservableProperty] private bool _isHighlighted;

    public int Id => Item.Id;
    public bool IsOut => Item.IsOut;
    public bool IsService => Item.ServiceText is not null;
    public bool HasText => !string.IsNullOrEmpty(Text);
    public bool IsJumboEmoji => !IsAlbum && Item.Media is MediaKind.None or MediaKind.WebPage && RichText.IsEmojiOnly(Text) && !HasReply && !IsForwarded;
    public double TextEmojiSize => IsJumboEmoji ? 56 : 20;
    public bool IsGroupChat => Chat.Kind == PeerKind.Group;
    public bool ShowSenderName => IsGroupChat && !IsOut && IsFirstInGroup && Item.From is not null && !IsSticker && !IsRound;
    public bool ShowSenderAvatar => IsGroupChat && !IsOut && IsLastInGroup;
    public bool ReserveAvatarSpace => IsGroupChat && !IsOut;
    public string SenderName => Item.From?.DisplayTitle ?? Chat.DisplayTitle;
    public Brush SenderBrush => AvatarColors.NameFor(Item.From?.Id ?? Chat.Id);
    public Brush SenderAvatarBrush => AvatarColors.For(Item.From?.Id ?? Chat.Id);
    public string SenderInitials => AvatarColors.Initials(SenderName);
    public string TimeText => Item.DateUtc.ToLocalTime().ToString("HH:mm");
    public string FullTime => Fmt.Date(Item.DateUtc) + (Item.EditDateUtc is { } e ? $"\nизменено {Fmt.Date(e)}" : "");
    public bool ShowChecks => IsOut && !Chat.IsSelf && Chat.Kind != PeerKind.Channel;
    public bool HasReply => Item.ReplyToId is not null;
    public bool IsForwarded => Item.ForwardedFrom is not null;
    public string? ViewsText => Item.Views > 0 ? (Item.Views >= 1000 ? $"{Item.Views / 1000.0:0.#}K" : Item.Views.ToString()) : null;

    // ---- медиа
    public bool IsPhoto => !IsAlbum && Item.Media is MediaKind.Photo;
    public bool IsVideo => !IsAlbum && Item.Media is MediaKind.Video;
    public bool IsGif => !IsAlbum && Item.Media is MediaKind.Gif;
    public bool IsSticker => !IsAlbum && Item.Media is MediaKind.Sticker;
    public bool IsRound => !IsAlbum && Item.Media is MediaKind.VideoNote;
    public bool IsVoice => !IsAlbum && Item.Media is MediaKind.Voice;
    public bool IsAudio => !IsAlbum && Item.Media is MediaKind.Audio;
    public bool IsVisualMedia => Item.Media is MediaKind.Photo or MediaKind.Video or MediaKind.Gif;
    public bool IsPhotoOrVideo => !IsAlbum && Item.Media is MediaKind.Photo or MediaKind.Video;
    public bool IsFileRow => !IsAlbum && Item.Media is MediaKind.Document or MediaKind.Contact or MediaKind.Location or MediaKind.Other;
    public bool CanDownload => Item.CanDownload && !IsSticker;
    public string DurationText => Fmt.Duration(Item.DurationSeconds);
    public string FileIcon => Item.Media switch
    {
        MediaKind.Audio => "",
        MediaKind.Contact => "",
        MediaKind.Location => "",
        MediaKind.Other => "",
        _ => "",
    };
    public string? FileTitle => Item.MediaTitle;
    public string? FileSubtitle => Item.Media is MediaKind.Audio ? Fmt.Duration(Item.DurationSeconds)
        : Item.MediaSize > 0 ? Fmt.FileSize(Item.MediaSize) : null;
    public bool IsBubbleless => IsSticker || IsRound || IsJumboEmoji ||
        ((IsVisualMedia || IsVisualAlbum) && !HasText && !HasReply && !IsForwarded);

    public double MediaWidth => FitSize().W;
    public double MediaHeight => FitSize().H;

    private (double W, double H) FitSize()
    {
        if (IsSticker) return (190, 190);
        if (IsRound) return (220, 220);
        double w = Item.MediaWidth > 0 ? Item.MediaWidth : 320, h = Item.MediaHeight > 0 ? Item.MediaHeight : 240;
        var k = Math.Min(MaxMediaWidth / w, MaxMediaHeight / h);
        k = Math.Min(k, 1.5);
        w *= k;
        h *= k;
        return (Math.Max(w, 140), Math.Max(h, 90));
    }

    /// <summary>Ссылка на карту для геопозиции.</summary>
    public string? MapUrl => Item.Latitude is { } la && Item.Longitude is { } lo
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"https://www.openstreetmap.org/?mlat={la}&mlon={lo}#map=16/{la}/{lo}") : null;

    public async void EnsurePreview(MediaService media)
    {
        if (_previewRequested) return;
        if (Item.Media is MediaKind.Location && Item.Latitude is not null)
        {
            _previewRequested = true;
            Preview = Images.FromBytes(await media.GetMapAsync(Item).ConfigureAwait(false));
            return;
        }
        if (!(Item.Media is MediaKind.Photo or MediaKind.Video)) return;
        _previewRequested = true;
        var bytes = await media.GetMessagePreviewAsync(Item).ConfigureAwait(false);
        Preview = Images.FromBytes(bytes);
    }

    public async void EnsureSenderAvatar(MediaService media)
    {
        if (SenderAvatar is not null || !ReserveAvatarSpace || Item.From is not { HasPhoto: true } from) return;
        var bytes = await media.GetPeerAvatarAsync(from).ConfigureAwait(false);
        SenderAvatar = Images.FromBytes(bytes, 64);
    }

    public void BuildWave()
    {
        var wave = Item.Waveform;
        const int bars = 40;
        var result = new double[bars];
        for (var i = 0; i < bars; i++)
        {
            var v = wave is { Count: > 0 } ? wave[(int)((double)i / bars * wave.Count)] : 6 + (i * 7 % 11);
            result[i] = 3 + v / 31.0 * 23;
        }
        WaveBars = result;
    }

    public void SetReply(MessageItem? target)
    {
        if (target is null)
        {
            ReplySender = "Сообщение";
            ReplyText = "недоступно";
            ReplyBrush = AvatarColors.NameFor(0);
            return;
        }
        ReplySender = target.IsOut ? "Вы" : target.From?.DisplayTitle ?? Chat.DisplayTitle;
        ReplyText = target.Preview is { Length: > 0 } p ? p : "Медиа";
        ReplyBrush = AvatarColors.NameFor(target.From?.Id ?? Chat.Id);
    }
}

