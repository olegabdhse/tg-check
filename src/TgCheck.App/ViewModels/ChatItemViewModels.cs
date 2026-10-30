using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    // «Избранное» — как в Telegram: синий круг с закладкой (не своё фото).
    public Brush AvatarBrush => Peer.IsSelf ? (Brush)System.Windows.Application.Current.FindResource("AccentBrush") : AvatarColors.For(Peer.Id);

    [ObservableProperty] private ImageSource? _avatar;
    /// <summary>У пира есть истории (кольцо вокруг аватара).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStoryRing), nameof(StoryRing))] private bool _hasStories;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StoryRing))] private bool _storiesUnread;

    /// <summary>Рамка аватара: градиент — непросмотренные истории, серая — просмотренные, нет — нет историй.</summary>
    public Brush? StoryRing => !HasStories ? null : StoriesUnread ? AvatarColors.StoryUnread : AvatarColors.StorySeen;

    public bool HasStoryRing => HasStories;

    /// <summary>
    /// Свежие данные пира с сервера (имя, фото, статус): раньше строка навсегда оставалась с данными из кэша —
    /// новое имя и аватар появлялись только после перезапуска. Фото сменилось — аватар перекачивается.
    /// </summary>
    public void RefreshPeer(PeerRef fresh, MediaService? media)
    {
        if (ReferenceEquals(Peer, fresh) || fresh.Key != Peer.Key) return;
        var photoChanged = fresh.PhotoId != Peer.PhotoId;
        var changed = photoChanged || fresh.Title != Peer.Title || fresh.EmojiStatusId != Peer.EmojiStatusId || fresh.Username != Peer.Username;
        Peer = fresh;
        if (!changed) return;
        OnPropertyChanged(string.Empty); // имя, инициалы, статус — всё, что зависит от пира
        if (!photoChanged) return;
        _requested = false;
        if (!fresh.HasPhoto) { Avatar = null; return; }
        if (media is not null && Avatar is not null) EnsureAvatar(media); // показанный аватар — заменить новым
    }

    public async void EnsureAvatar(MediaService media)
    {
        if (_requested || !Peer.HasPhoto || Peer.IsSelf) return;
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
    /// <summary>Обновить из данных сервера. true — изменилось то, от чего зависит место в списке.</summary>
    public bool Update(ChatSummary s)
    {
        var moved = false;
        // То же сообщение без правок — не подменяем: иначе строка перерисовывается впустую (а их тысячи).
        if (s.Last is not null && (Last is null || s.Last.Id > Last.Id || (s.Last.Id == Last.Id && s.Last.EditDateUtc != Last.EditDateUtc)))
        {
            moved = Last?.DateUtc != s.Last.DateUtc;
            Last = s.Last;
        }
        moved |= IsPinned != s.IsPinned || IsArchived != s.IsArchived;
        Unread = s.UnreadCount;
        Mentions = s.UnreadMentions;
        ReadOutboxMaxId = Math.Max(ReadOutboxMaxId, s.ReadOutboxMaxId);
        ReadInboxMaxId = Math.Max(ReadInboxMaxId, s.ReadInboxMaxId);
        IsPinned = s.IsPinned;
        IsMuted = s.IsMuted;
        IsArchived = s.IsArchived;
        IsMarkedUnread = s.IsMarkedUnread;
        return moved;
    }

    /// <summary>До какого входящего сообщения чат прочитан (сервер).</summary>
    public int ReadInboxMaxId { get; set; }

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
    /// <summary>Чат открыт из поиска, но переписки ещё нет: держим его в списке (наверху, по времени открытия).</summary>
    public DateTime? AddedUtc { get; set; }
    public DateTime SortDate => Last?.DateUtc ?? AddedUtc ?? DateTime.MinValue;

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
public sealed record QuickReactionItem(MessageViewModel Message, ReactionInfo Choice)
{
    public string? Emoji => Choice.Emoji;
    /// <summary>Собственное (премиум) эмодзи канала.</summary>
    public long CustomEmojiId => Choice.CustomEmojiId;
    public bool IsCustom => CustomEmojiId != 0;
    public ReactionDef? Def { get; } = ReactionCatalog.Find(Choice.Emoji);
    /// <summary>Анимация «выбора» из каталога Telegram (играет в панели реакций), иначе — просто эмодзи.</summary>
    public MediaHolder? Animation => Def?.Icon ?? Def?.Select;
    public bool HasAnimation => Animation is not null;
    public bool ShowEmojiText => !IsCustom && !HasAnimation;
}

/// <summary>Плашка реакции под сообщением.</summary>
public sealed class ReactionViewModel(MessageViewModel message, ReactionInfo info, bool justChosen = false)
{
    /// <summary>Только что поставлена мной — один раз проигрывается эффект, как в Telegram.</summary>
    public bool JustChosen { get; } = justChosen;
    public ReactionDef? Def { get; } = info.CustomEmojiId == 0 ? ReactionCatalog.Find(info.Emoji) : null;
    public MediaHolder? Icon => Def?.Icon;
    public MediaHolder? Effect => Def?.Effect ?? Def?.Activate;
    public bool HasIcon => Icon is not null;
    public bool ShowEmojiText => !IsCustom && !HasIcon;
    /// <summary>Платная реакция звёздами — золотая плашка, как в Telegram.</summary>
    public bool IsPaid => Info.IsPaid;
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
        _isMediaUnread = item.MediaUnread;
        RebuildPoll();
    }

    public PeerRef Chat { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceDurationText))] private MessageItem _item;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasText), nameof(IsJumboEmoji), nameof(TextEmojiSize))] private string _text;
    [ObservableProperty] private bool _isEdited;
    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private ImageSource? _preview;

    /// <summary>Размытая (крошечная, растянутая) копия превью — для фона карточки песни и полос слайд-шоу.</summary>
    public ImageSource? BlurredPreview => _blurredPreview ??= Images.Tiny(Preview);
    private ImageSource? _blurredPreview;

    /// <summary>Фон карточки песни (музыка профиля): средний цвет обложки, затемнённый, — как в Telegram.</summary>
    [ObservableProperty] private Brush? _cardBrush;

    partial void OnPreviewChanged(ImageSource? value)
    {
        _blurredPreview = null;
        OnPropertyChanged(nameof(BlurredPreview));
        if (!IsAudio) return;
        CardBrush = value is System.Windows.Media.Imaging.BitmapSource b && Images.AverageColor(b) is { } c
            ? FrozenBrush(Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45)))
            : null;
    }

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
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
    /// <summary>Голосовое/кружок не прослушано (у своих — собеседником): маленькая закрашенная точка у длительности.</summary>
    [ObservableProperty] private bool _isMediaUnread;

    public string PlayIcon => IsPlaying ? "" : "";
    public string VoiceDurationText => PositionText ?? Fmt.Duration(Item.DurationSeconds);
    public string AudioKey => Item.Id == 0 ? $"song:{Item.MediaId}" : $"{Chat.Key}:{Item.Id}"; // музыка профиля — без сообщения

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

    /// <summary>Альбом из готовых частей (коллаж внутри сложного сообщения).</summary>
    public void SetAlbumParts(IEnumerable<MessageViewModel> parts)
    {
        Album.Clear();
        foreach (var p in parts) Album.Add(p);
        NotifyAlbum();
    }

    // ---- сложное сообщение (статья, коллаж, музыка): блоки по порядку, как в Telegram
    private IReadOnlyList<RichBlockViewModel>? _richBlocks;
    public IReadOnlyList<RichBlockViewModel> RichBlocks => _richBlocks ??= Item.Rich?.Select(b => new RichBlockViewModel(b, Chat)).ToList() ?? [];
    public bool IsRich => Item.Rich is { Count: > 0 };
    /// <summary>Все фото/видео сложного сообщения — для просмотрщика.</summary>
    public IEnumerable<MessageViewModel> RichMedia => RichBlocks.SelectMany(b => b.Slides.Count > 0 ? b.Slides : b.Media is null ? [] : b.Media.IsAlbum ? b.Media.Album.AsEnumerable() : [b.Media]).Where(m => m.IsVisualMedia);

    public void EnsureRichPreviews(MediaService media)
    {
        foreach (var b in RichBlocks)
        {
            foreach (var slide in b.Slides) slide.EnsurePreview(media);
            if (b.Media is not { } m) continue;
            m.EnsurePreview(media);
            foreach (var part in m.Album) part.EnsurePreview(media);
            if (m.IsVoice || m.IsAudio) m.BuildWave();
        }
    }

    public void RemoveFromAlbum(int id)
    {
        if (Album.FirstOrDefault(a => a.Id == id) is { } part) Album.Remove(part);
        NotifyAlbum();
    }

    private void NotifyAlbum()
    {
        foreach (var name in new[] { nameof(Layout), nameof(TextMaxWidth), nameof(ContentMaxWidth), nameof(IsAlbum), nameof(IsVisualAlbum), nameof(IsFileAlbum), nameof(AlbumColumns),
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

    /// <summary>
    /// Весь пузырь с фото/альбомом не шире самого медиа: длинная цитата ответа или имя обрезаются многоточием,
    /// а не растягивают пузырь с пустым местом справа от коллажа (как в Telegram).
    /// </summary>
    public double ContentMaxWidth => IsVisualAlbum || Item.Media is MediaKind.Photo or MediaKind.Video or MediaKind.Gif ? TextMaxWidth : double.PositiveInfinity;

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
    /// <summary>Первые реакции для панели в меню (порядок как в Telegram), остальные — по стрелке «ещё».</summary>
    public IReadOnlyList<QuickReactionItem> QuickReactionItems => AllReactionItems.Take(7).ToList();
    public IReadOnlyList<QuickReactionItem> MoreReactionItems => AllReactionItems.Skip(7).ToList();
    // Только реакции, разрешённые в этом чате (каналы и группы часто ограничивают набор).
    private IReadOnlyList<QuickReactionItem> AllReactionItems =>
        ReactionCatalog.ChoicesFor(Chat.Key).Select(e => new QuickReactionItem(this, e)).ToList();
    public IReadOnlyList<ReactionInfo> ReactionInfos => Reactions.Select(r => r.Info).ToList();

    public void SetReactions(IReadOnlyList<ReactionInfo> reactions, string? justChosenKey = null)
    {
        // По месту: неизменившиеся плашки не пересоздаются (иначе ответ сервера обрывает анимацию постановки).
        for (var i = 0; i < reactions.Count; i++)
        {
            var r = reactions[i];
            if (i < Reactions.Count && Reactions[i].Info == r) continue;
            var vm = new ReactionViewModel(this, r, r.Chosen && r.Key == justChosenKey);
            if (i < Reactions.Count) Reactions[i] = vm;
            else Reactions.Add(vm);
        }
        while (Reactions.Count > reactions.Count) Reactions.RemoveAt(Reactions.Count - 1);
        OnPropertyChanged(nameof(HasReactions));
    }
    public string CommentsText => Item.Comments is not { } n || n == 0 ? "Оставить комментарий"
        : $"{Fmt.N(n)} {Fmt.Plural(n, "комментарий", "комментария", "комментариев")}";
    /// <summary>Подсветка после перехода к сообщению (закреп, ответ).</summary>
    [ObservableProperty] private bool _isHighlighted;
    /// <summary>Выбрано в режиме выделения нескольких сообщений.</summary>
    [ObservableProperty] private bool _isSelected;

    public int Id => Item.Id;
    public bool IsOut => Item.IsOut;
    /// <summary>Можно удалить: своё — всегда; чужое — в личке и с ботом, в группах и каналах — с правом удаления.</summary>
    public bool CanDelete => !IsService && (IsOut || Chat.Kind is PeerKind.User or PeerKind.Bot || Chat.CanDeleteOthers);
    public bool IsService => Item.ServiceText is not null;
    // ---- автозагрузка голосовых и кружков, как только они на экране: тот же белый ободок с крестиком, что при отправке
    /// <summary>Сколько скачано (0..1); меньше 0 — не качается.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFileDownloading))] private double _fileProgress = -1;
    public bool IsFileDownloading => FileProgress >= 0;
    private bool _autoRequested;

    public async void AutoDownload(MediaService media)
    {
        if (_autoRequested || !(IsVoice || IsRound)) return;
        _autoRequested = true;
        if (await Task.Run(() => media.IsMediaFileCached(Item))) return;
        FileProgress = 0;
        await media.GetMediaFileAsync(Item, new Progress<double>(p => { if (IsFileDownloading) FileProgress = p; }), priority: false);
        FileProgress = -1;
    }

    /// <summary>Крестик на ободке: остановить загрузку (сама больше не начнётся — только по нажатию «играть»).</summary>
    public void CancelDownload(MediaService media)
    {
        media.CancelMediaFile(Item);
        FileProgress = -1;
    }

    /// <summary>Подарок: карточка вместо строки служебного сообщения.</summary>
    public GiftCard? Gift => Item.Gift;
    public bool HasGift => Item.Gift is not null;
    /// <summary>Фон карточки: у уникального подарка — его цвета, у обычного — как у служебных сообщений.</summary>
    // (у уникального: сверху — картинка фона с узором (Preview), ниже — цвет края, чтобы фон продолжался под текстом)
    public Brush GiftBackground => Item.Gift is { EdgeColor: { } e }
        ? Frozen(new SolidColorBrush(Rgb(e)))
        : Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0x18, 0x25, 0x33)));
    private static Color Rgb(int v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
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
    /// <summary>Песня: название (крупно) и исполнитель (мельче), как в Telegram.</summary>
    public string SongTitle => Item.SongTitle ?? Item.MediaTitle ?? "Аудио";
    public string? SongPerformer => Item.SongPerformer;
    /// <summary>Фон обложки, пока её нет или если её не нашлось: красивый градиент, всегда один и тот же для песни.</summary>
    public Brush CoverBrush => SongGradients[(int)((uint)StableHash(SongTitle + SongPerformer) % SongGradients.Length)];

    private static readonly Brush[] SongGradients =
    [
        Grad(0xFF5F6D, 0xFFC371), Grad(0x7F00FF, 0xE100FF), Grad(0x00C6FF, 0x0072FF), Grad(0x11998E, 0x38EF7D),
        Grad(0xFC466B, 0x3F5EFB), Grad(0xF7971E, 0xFFD200), Grad(0x8E2DE2, 0x4A00E0), Grad(0xEE0979, 0xFF6A00),
        Grad(0x43CEA2, 0x185A9D), Grad(0xDA4453, 0x89216B), Grad(0x00B09B, 0x96C93D), Grad(0x654EA3, 0xEAAFC8),
    ];

    private static Brush Grad(int a, int b)
    {
        var brush = new LinearGradientBrush(Color.FromRgb((byte)(a >> 16), (byte)(a >> 8), (byte)a), Color.FromRgb((byte)(b >> 16), (byte)(b >> 8), (byte)b), 45);
        brush.Freeze();
        return brush;
    }

    // string.GetHashCode меняется от запуска к запуску — свой, чтобы цвет у песни был всегда один.
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 23;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }
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
        if (Item.Gift is { IsUnique: true } gift)
        {
            // Уникальный подарок: фон его цветов с кольцами узора — под живой моделью.
            _previewRequested = true;
            Preview = Images.FromBytes(await media.GetGiftBackdropAsync(gift, 500).ConfigureAwait(false));
            return;
        }
        if (Item.Media is MediaKind.Location && Item.Latitude is not null)
        {
            _previewRequested = true;
            Preview = Images.FromBytes(await media.GetMapAsync(Item).ConfigureAwait(false));
            return;
        }
        if (Item.Media is MediaKind.Audio)
        {
            // Песня: обложка из файла, а если её нет — из каталога по названию и исполнителю.
            _previewRequested = true;
            var cover = await media.GetMessagePreviewAsync(Item).ConfigureAwait(false)
                        ?? await media.GetSongCoverAsync(Item.SongPerformer, Item.SongTitle ?? Item.MediaTitle ?? "").ConfigureAwait(false);
            Preview = Images.FromBytes(cover, 160);
            return;
        }
        if (!(Item.Media is MediaKind.Photo or MediaKind.Video)) return;
        _previewRequested = true;
        var bytes = await media.GetMessagePreviewAsync(Item).ConfigureAwait(false);
        Preview = Images.FromBytesMax(bytes, 760);
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
        const int bars = 50;
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


/// <summary>Строка темы в группе-форуме (как в Telegram: значок, название, последнее сообщение, счётчик).</summary>
public sealed partial class ForumTopicViewModel : ObservableObject
{
    public ForumTopicViewModel(ForumTopicInfo topic)
    {
        Topic = topic;
        _last = topic.Last;
        _unread = topic.UnreadCount;
    }

    public ForumTopicInfo Topic { get; }
    public string Title => Topic.Title;
    public long IconEmojiId => Topic.IconEmojiId;
    public bool HasEmojiIcon => Topic.IconEmojiId != 0;
    /// <summary>Значок без эмодзи: цветной кружок с первой буквой (у «Общего» — решётка).</summary>
    public string IconLetter => Topic.IsGeneral ? "#" : Title.Length > 0 ? char.ToUpperInvariant(Title[0]).ToString() : "?";
    public Brush IconBrush => Topic.IconColor != 0
        ? Freeze(new SolidColorBrush(Color.FromRgb((byte)(Topic.IconColor >> 16), (byte)(Topic.IconColor >> 8), (byte)Topic.IconColor)))
        : Freeze(new SolidColorBrush(Color.FromRgb(0x6F, 0xB9, 0xF0)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LastText), nameof(TimeText))] private MessageItem? _last;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LastText))] private string? _senderName;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText))] private int _unread;
    /// <summary>Эта тема сейчас открыта (подсветка в боковом островке тем).</summary>
    [ObservableProperty] private bool _isActive;

    public string LastText => Last is null ? "" : SenderName is { Length: > 0 } s ? $"{s}: {Last.Preview}" : Last.Preview;
    public string TimeText => Last is null ? "" : Fmt.ChatTime(Last.DateUtc);
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 999 ? $"{Unread / 1000}K" : Unread.ToString();
    public bool IsPinned => Topic.IsPinned;
    public bool IsClosed => Topic.IsClosed;
}

/// <summary>
/// Отправляется (идёт загрузка файла): сразу в ленте — с прогрессом и крестиком отмены, как в Telegram.
/// Когда сервер принял сообщение, элемент заменяется настоящим сообщением.
/// </summary>
public sealed partial class UploadViewModel(string title, string icon, ImageSource? preview = null) : ObservableObject
{
    public string Title { get; } = title;
    /// <summary>Значок типа (голосовое, фото, файл) — если нет превью.</summary>
    public string Icon { get; } = icon;
    public ImageSource? Preview { get; } = preview;
    public bool HasPreview => Preview is not null;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProgressText))] private double _progress;
    public string ProgressText => Progress > 0 ? $"Отправка… {Progress:P0}" : "Отправка…";

    public CancellationTokenSource Cts { get; } = new();

    [RelayCommand]
    private void Cancel() => Cts.Cancel();

    // ---- фото и видео: та же сетка, что будет после отправки; на каждой плитке — свой ободок загрузки
    public IReadOnlyList<UploadPart> Tiles { get; init; } = [];
    public bool IsMedia => Tiles.Count > 0;
    public bool IsSingleTile => Tiles.Count == 1;
    public bool IsAlbumTiles => Tiles.Count > 1;

    // ---- файлы: строки как у отправленных файлов, вместо значка — ободок загрузки
    public IReadOnlyList<UploadPart> Files { get; init; } = [];
    public bool IsFiles => Files.Count > 0;
    /// <summary>Старый вид (одна строка «Отправка… N%») — для остального.</summary>
    public bool IsPlain => !IsVoice && !IsMedia && !IsFiles;

    // ---- голосовое: пузырь той же формы, что после отправки (волна и длительность)
    public string? VoiceDuration { get; init; }
    public IReadOnlyList<double>? WaveBars { get; init; }
    public bool IsVoice => VoiceDuration is not null;
    public string TimeText { get; } = DateTime.Now.ToString("HH:mm");

    /// <summary>50 столбиков волны (высоты как у отправленного голосового) из громкости записи.</summary>
    public static IReadOnlyList<double> Bars(float[]? peaks)
    {
        const int bars = 50;
        var result = new double[bars];
        var max = peaks is { Length: > 0 } ? Math.Max(peaks.Max(), 0.05f) : 1;
        for (var i = 0; i < bars; i++)
        {
            var p = 0f;
            if (peaks is { Length: > 0 })
            {
                var from = i * peaks.Length / bars;
                var to = Math.Max(from + 1, (i + 1) * peaks.Length / bars);
                for (var j = from; j < to && j < peaks.Length; j++) p = Math.Max(p, peaks[j]);
            }
            result[i] = 3 + Math.Clamp(p / max, 0, 1) * 23;
        }
        return result;
    }
}

/// <summary>Блок сложного сообщения: заголовок, абзац, цитата, код, разделитель, медиа или коллаж.</summary>
public sealed partial class RichBlockViewModel : ObservableObject
{
    public RichBlockViewModel(RichBlock block, PeerRef chat)
    {
        Kind = block.Kind;
        Text = block.Text;
        if (block.Items.Count == 0) return;
        if (block.Kind == RichBlockKind.Slideshow)
        {
            // Слайд-шоу: одно окно, листание стрелками, точки — какой слайд сейчас (как в Telegram).
            Slides = block.Items.Select(i => new MessageViewModel(i, chat)).ToList();
            Dots = Slides.Select((_, i) => new SlideDot(i == 0)).ToList();
            SlideHeight = Math.Clamp(363 / Aspect(block.Items[0]), 200, 440);
            return;
        }
        // Медиа — обычная модель сообщения (те же шаблоны фото, видео, музыки); коллаж — альбом из частей.
        Media = new MessageViewModel(block.Items[0], chat);
        if (block.Kind == RichBlockKind.Album)
            Media.SetAlbumParts(block.Items.Select(i => new MessageViewModel(i, chat)));
    }

    public RichBlockKind Kind { get; }
    public string? Text { get; }
    public MessageViewModel? Media { get; }

    // ---- слайд-шоу
    public IReadOnlyList<MessageViewModel> Slides { get; } = [];
    public IReadOnlyList<SlideDot> Dots { get; } = [];
    public double SlideHeight { get; } = 300;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Current), nameof(HasPrev), nameof(HasNext))] private int _index;
    public MessageViewModel? Current => Slides.Count > 0 ? Slides[Index] : null;
    public bool HasPrev => Index > 0;
    public bool HasNext => Index < Slides.Count - 1;

    partial void OnIndexChanged(int value)
    {
        for (var i = 0; i < Dots.Count; i++) Dots[i].IsActive = i == value;
    }

    [RelayCommand] private void PrevSlide() { if (HasPrev) Index--; }
    [RelayCommand] private void NextSlide() { if (HasNext) Index++; }

    private static double Aspect(MessageItem m) => m is { MediaWidth: > 0, MediaHeight: > 0 } ? Math.Clamp((double)m.MediaWidth / m.MediaHeight, 0.6, 2) : 1;
}

/// <summary>Точка под слайд-шоу: белая — текущий слайд.</summary>
public sealed partial class SlideDot(bool active) : ObservableObject
{
    [ObservableProperty] private bool _isActive = active;
}

/// <summary>Часть отправки: одна плитка сетки (фото/видео) или один файл — со своим процентом загрузки.</summary>
public sealed partial class UploadPart : ObservableObject
{
    private const double MaxW = 360, MaxH = 400;

    public UploadPart(string name, string size, ImageSource? preview, double aspect, bool isVideo)
    {
        Name = name;
        Size = size;
        Preview = preview;
        Aspect = aspect;
        IsVideo = isVideo;
        // Одиночное медиа — того же размера, что пузырь после отправки (см. MessageViewModel.FitSize).
        double w = aspect >= 1 ? 1000 : 1000 * aspect, h = aspect >= 1 ? 1000 / aspect : 1000;
        var k = Math.Min(Math.Min(MaxW / w, MaxH / h), 1.5);
        SingleWidth = Math.Max(w * k, 140);
        SingleHeight = Math.Max(h * k, 90);
    }

    public string Name { get; }
    public string Size { get; }
    public ImageSource? Preview { get; }
    /// <summary>Ширина / высота (для раскладки сетки).</summary>
    public double Aspect { get; }
    public bool IsVideo { get; }
    public double SingleWidth { get; }
    public double SingleHeight { get; }

    [ObservableProperty] private double _progress;
}
