using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Файл, прикреплённый к сообщению перед отправкой.</summary>
public sealed partial class AttachmentViewModel(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string SizeText => Fmt.FileSize(new FileInfo(Path).Length);
    public bool IsImage => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif";
    public ImageSource? Thumb { get; } = LoadThumb(path);
    public bool IsVideo => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi";
    public string Icon => IsVideo ? "\uE714" : System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".mp3" or ".ogg" or ".wav" or ".m4a" or ".flac" ? "\uE8D6" : "\uE8A5";
    [ObservableProperty] private double _progress;

    private static ImageSource? LoadThumb(string path)
    {
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif")) return null;
        try { return Images.FromBytes(File.ReadAllBytes(path), 480); }
        catch { return null; }
    }
}

/// <summary>Открытый чат: история, отправка, ответы, правка, удаление, медиа, опросы, вложения.</summary>
public sealed partial class ConversationViewModel : PeerAvatarViewModel
{
    private readonly TelegramService _tg;
    private readonly ChatsViewModel _owner;
    private readonly Dictionary<int, MessageViewModel> _byId = [];
    private CancellationTokenSource? _typingCts;
    private int _readOutboxMaxId;
    private int _lastMarkedRead;
    private bool _loadedOnce;

    public ConversationViewModel(TelegramService tg, ChatsViewModel owner, PeerRef peer, int readOutboxMaxId) : base(peer)
    {
        _tg = tg;
        _owner = owner;
        _readOutboxMaxId = readOutboxMaxId;
        UpdateStatus();
        AudioPlayer.Instance.Ended += OnAudioEnded;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowStartBot));
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowMic));
    }

    /// <summary>Ветка комментариев к посту канала: чат обсуждения, отфильтрованный по исходному сообщению.</summary>
    /// <summary>
    /// Ветка: комментарии к посту канала (post — исходный пост) или тема группы-форума (title — название темы,
    /// generalTopic — тема «Общее», у её сообщений нет привязки к теме).
    /// </summary>
    public ConversationViewModel(TelegramService tg, ChatsViewModel owner, PeerRef discussion, int topId, ConversationViewModel parent, MessageItem? post,
        string? title = null, bool generalTopic = false)
        : this(tg, owner, discussion, 0)
    {
        ThreadTopId = topId;
        ThreadParent = parent;
        _threadPost = post;
        _threadTitle = title;
        _generalTopic = generalTopic;
    }

    public int? ThreadTopId { get; }
    public ConversationViewModel? ThreadParent { get; }
    public bool IsThread => ThreadTopId is not null;
    private readonly MessageItem? _threadPost;
    private readonly string? _threadTitle;
    private readonly bool _generalTopic;
    private bool IsTopic => _threadTitle is not null;
    public string HeaderTitle => IsThread ? _threadTitle ?? "Комментарии" : Title;
    /// <summary>Куда отвечать по умолчанию: в ветку/тему; в «Общее» — без привязки.</summary>
    private int? SendTopId => _generalTopic ? null : ThreadTopId;

    // История: обычный чат, ответы в ветке (исходный пост — первым сообщением ветки) или тема форума.
    private async Task<IReadOnlyList<MessageItem>> FetchAsync(int offsetId)
    {
        if (ThreadTopId is not { } top) return await _tg.GetHistoryAsync(Peer, offsetId, 50);
        if (_generalTopic) return await _tg.GetGeneralTopicAsync(Peer, offsetId, 50);
        var replies = await _tg.GetRepliesAsync(Peer, top, offsetId, 50);
        if (replies.Count < 50 && _threadPost is not null && replies.All(m => m.Id != _threadPost.Id))
            return [_threadPost, .. replies];
        return replies;
    }

    /// <summary>Сообщение относится к этой ветке / теме?</summary>
    public bool BelongsToThread(MessageItem m)
    {
        if (ThreadTopId is not { } top || m.PeerKey != Peer.Key) return false;
        if (_generalTopic) return m.TopicId is null;
        if (IsTopic) return m.TopicId == top;
        return m.ReplyToId == top || m.ThreadTopId == top || (m.ReplyToId is { } r && _byId.ContainsKey(r));
    }

    // ===================================================================== Группа с темами (форум)

    public bool IsForum => Peer.IsForum && !IsThread;
    public ObservableCollection<ForumTopicViewModel> Topics { get; } = [];
    [ObservableProperty] private bool _topicsLoading;

    private async Task LoadTopicsAsync()
    {
        TopicsLoading = Topics.Count == 0;
        try
        {
            var topics = await _tg.GetForumTopicsAsync(Peer);
            Topics.Clear();
            foreach (var t in topics)
            {
                var vm = new ForumTopicViewModel(t);
                if (t.Last?.From is { } from && !t.Last.IsOut) vm.SenderName = from.DisplayTitle;
                Topics.Add(vm);
            }
            SyncForumUnread();
            _loadedOnce = true;
        }
        catch (Exception ex)
        {
            if (Topics.Count == 0) Error = TgErrors.Describe(ex);
            else _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally { TopicsLoading = false; }
    }

    /// <summary>Темы форума для бокового островка (когда открыта тема).</summary>
    public ObservableCollection<ForumTopicViewModel>? ForumTopics => IsTopic ? ThreadParent?.Topics : null;
    public bool ShowTopicRail => IsTopic && ThreadParent is not null;

    /// <summary>Переключить тему из островка — без возврата к списку.</summary>
    [RelayCommand]
    private void SwitchTopic(ForumTopicViewModel? topic)
    {
        if (topic is null || topic.Topic.Id == ThreadTopId) return;
        ThreadParent?.OpenTopicCommand.Execute(topic);
    }

    // Открытые темы держим готовыми — переключение между ними без перезагрузки.
    private readonly Dictionary<int, ConversationViewModel> _topicThreads = [];

    // Пометка группы в списке чатов — сумма непрочитанных по темам.
    private void SyncForumUnread() => _owner.SetForumUnread(Peer.Key, Topics.Sum(t => t.Unread));

    [RelayCommand]
    private void OpenTopic(ForumTopicViewModel? topic)
    {
        if (topic is null) return;
        topic.Unread = 0;
        foreach (var t in Topics) t.IsActive = t == topic;
        SyncForumUnread();
        if (!_topicThreads.TryGetValue(topic.Topic.Id, out var thread))
            _topicThreads[topic.Topic.Id] = thread = new ConversationViewModel(_tg, _owner, Peer, topic.Topic.Id, this, null, topic.Title, topic.Topic.IsGeneral);
        _owner.OpenThread(thread);
    }

    // Новое сообщение в форуме — обновить строку темы и поднять её (под закреплёнными).
    private void OnForumMessage(MessageItem m)
    {
        var topicId = m.TopicId ?? 1;
        var topic = Topics.FirstOrDefault(t => t.Topic.Id == topicId);
        if (topic is null) { _ = LoadTopicsAsync(); return; }
        topic.Last = m;
        topic.SenderName = m.IsOut ? "Вы" : m.From?.DisplayTitle;
        if (!m.IsOut) topic.Unread++;
        SyncForumUnread();
        if (topic.Topic.IsPinned) return;
        var index = Topics.IndexOf(topic);
        var target = Topics.TakeWhile(t => t.Topic.IsPinned).Count();
        if (index != target) Topics.Move(index, target);
    }

    public ChatsViewModel Owner => _owner;
    public MediaPickerViewModel Picker => _owner.Shell.Picker;

    /// <summary>Сообщения и разделители дат в хронологическом порядке.</summary>
    public ObservableCollection<object> Items { get; } = [];
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingOlder;
    [ObservableProperty] private bool _hasMoreHistory = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isTypingNow;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _draft = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasComposerBar), nameof(ComposerBarTitle), nameof(ComposerBarText))]
    private MessageViewModel? _replyTo;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasComposerBar), nameof(ComposerBarTitle), nameof(ComposerBarText))]
    private MessageViewModel? _editing;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isSending;
    [ObservableProperty] private bool _sendAsFiles;
    [ObservableProperty] private bool _isPickerOpen;

    // Просмотр фото и видео
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsViewerOpen))] private ImageSource? _viewerImage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsViewerOpen), nameof(ViewerIsVideo))] private MessageViewModel? _viewerMessage;
    [ObservableProperty] private bool _viewerLoading;
    [ObservableProperty] private double _viewerProgress;
    [ObservableProperty] private string? _viewerVideoPath;
    /// <summary>Встроенный плеер не смог открыть видео (кодек, формат) — показываем превью и кнопку «Открыть в плеере».</summary>
    [ObservableProperty] private bool _viewerVideoFailed;

    public bool IsViewerOpen => ViewerMessage is not null;
    public bool ViewerIsVideo => ViewerMessage?.Item.Media is MediaKind.Video or MediaKind.Gif;

    // Мини-профиль справа
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSideProfileOpen))] private ProfileViewModel? _sideProfile;
    public bool IsSideProfileOpen => SideProfile is not null;

    /// <summary>Сколько новых сообщений пришло, пока пользователь читал историю выше.</summary>
    [ObservableProperty] private int _pendingNewCount;

    public bool CanWrite => !IsForum && (IsThread || Peer.CanWrite);
    public bool HasComposerBar => ReplyTo is not null || Editing is not null;
    public bool HasAttachments => Attachments.Count > 0;
    public string ComposerBarTitle => Editing is not null ? "Редактирование" : $"В ответ {(ReplyTo is { IsOut: true } ? "себе" : ReplyTo?.SenderName)}";
    public string ComposerBarText => (Editing ?? ReplyTo)?.Item.Preview ?? "";

    /// <summary>View: прокрутить вниз (true — плавно).</summary>
    public event Action<bool>? ScrollToBottomRequested;
    /// <summary>View: вставить текст (эмодзи) в позицию курсора.</summary>
    public event Action<string>? InsertTextRequested;
    /// <summary>View: сейчас пользователь внизу ленты?</summary>
    public Func<bool>? IsAtBottom { get; set; }

    public int LastMessageId => _byId.Count == 0 ? 0 : _byId.Keys.Max();

    // ===================================================================== Загрузка

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        Error = null;
        if (IsForum)
        {
            await LoadTopicsAsync(); // форум открывается списком тем, как в Telegram
            return;
        }

        // 1) Мгновенно — из локального кэша.
        var cached = _loadedOnce || IsThread ? null : await Task.Run(() => _tg.GetCachedHistory(Peer));
        if (cached is { Count: > 0 } && Items.Count == 0)
        {
            ResetItems(cached);
            ScrollToBottomRequested?.Invoke(false);
        }
        else IsLoading = Items.Count == 0;

        // 2) Из сети — слияние без перерисовки всей ленты.
        try
        {
            var history = await FetchAsync(0);
            if (Items.Count == 0) ResetItems(history);
            else Merge(history);
            HasMoreHistory = history.Count >= 50;
            await ResolveRepliesAsync(history);
            UpdateReplyKeyboard();
            if (!_loadedOnce)
            {
                ScrollToBottomRequested?.Invoke(false);
                _ = LoadPinnedAsync();
                _ = LoadBotMenuAsync();
                _ = LoadAllowedReactionsAsync();
            }
            _loadedOnce = true;
            MarkRead();
        }
        catch (Exception ex)
        {
            if (Items.Count == 0) Error = TgErrors.Describe(ex);
            else _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private int _resetGeneration;

    // Первым кадром строятся только последние сообщения (их и видно), остальные — фоном над ними:
    // разметка 50 тяжёлых пузырей группы за раз подвешивала окно на секунду.
    private const int FirstScreen = 15;

    private void ResetItems(IReadOnlyList<MessageItem> messages, bool staged = true)
    {
        var generation = ++_resetGeneration;
        Items.Clear();
        _byId.Clear();
        var split = staged ? Math.Max(0, messages.Count - FirstScreen) : 0;
        AppendRange(messages.Skip(split), animate: false);
        LinkReplies();
        if (split == 0) return;
        var older = messages.Take(split).ToList();
        System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (generation != _resetGeneration) return; // за это время ленту уже пересобрали
            PrependRange(older);
            LinkReplies();
        });
    }

    private void LinkReplies()
    {
        foreach (var vm in _byId.Values.Distinct())
            if (vm.Item.ReplyToId is { } r && _byId.TryGetValue(r, out var t)) vm.SetReply(t.Item);
    }

    // Сеть vs то, что уже показано: новые — добавить, изменённые — обновить, удалённые в этом диапазоне — убрать.
    private void Merge(IReadOnlyList<MessageItem> fresh)
    {
        if (fresh.Count == 0) return;
        var freshIds = fresh.Select(m => m.Id).ToHashSet();
        var min = fresh.Min(m => m.Id);
        var gone = _byId.Keys.Where(id => id >= min && !freshIds.Contains(id)).ToList();
        if (gone.Count > 0) OnMessagesDeleted(gone);
        foreach (var m in fresh.Where(m => _byId.ContainsKey(m.Id))) OnMessageEdited(m);
        var newer = fresh.Where(m => !_byId.ContainsKey(m.Id)).ToList();
        if (newer.Count > 0)
        {
            var maxShown = _byId.Count == 0 ? 0 : _byId.Keys.Max();
            AppendRange(newer.Where(m => m.Id > maxShown), animate: _loadedOnce);
            var older = newer.Where(m => m.Id < maxShown).ToList();
            if (older.Count > 0) PrependRange(older);
        }
    }

    /// <summary>Подгрузить более старые сообщения (при прокрутке вверх).</summary>
    public async Task<bool> LoadOlderAsync()
    {
        if (IsLoadingOlder || !HasMoreHistory || IsLoading || _byId.Count == 0) return false;
        IsLoadingOlder = true;
        try
        {
            var oldest = _byId.Keys.Min();
            var older = await FetchAsync(oldest);
            HasMoreHistory = older.Count >= 50;
            PrependRange(older);
            await ResolveRepliesAsync(older);
            return older.Count > 0;
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
            return false;
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    private void AppendRange(IEnumerable<MessageItem> messages, bool animate)
    {
        foreach (var m in messages)
        {
            if (_byId.ContainsKey(m.Id)) continue;
            var last = Items.OfType<MessageViewModel>().LastOrDefault();
            // Части альбома склеиваются в одно сообщение.
            if (m.GroupedId != 0 && last is not null && last.Item.GroupedId == m.GroupedId)
            {
                var part = Create(m);
                last.AddToAlbum(part, _tg.Media);
                _byId[m.Id] = last;
                continue;
            }
            var vm = Create(m);
            vm.IsNew = animate;
            var day = m.DateUtc.ToLocalTime().Date;
            if (last is null || last.Item.DateUtc.ToLocalTime().Date != day) Items.Add(new DateSeparatorViewModel(day));
            Items.Add(vm);
            _byId[m.Id] = vm;
            Regroup(Items.Count - 1);
        }
    }

    private void PrependRange(IReadOnlyList<MessageItem> messages)
    {
        var fresh = messages.Where(m => !_byId.ContainsKey(m.Id)).OrderBy(m => m.Id).ToList();
        if (fresh.Count == 0) return;

        if (Items.FirstOrDefault() is DateSeparatorViewModel) Items.RemoveAt(0);

        var block = new List<object>();
        DateTime? lastDay = null;
        MessageViewModel? prev = null;
        foreach (var m in fresh)
        {
            if (m.GroupedId != 0 && prev is not null && prev.Item.GroupedId == m.GroupedId)
            {
                prev.AddToAlbum(Create(m), _tg.Media);
                _byId[m.Id] = prev;
                continue;
            }
            var day = m.DateUtc.ToLocalTime().Date;
            if (lastDay != day) block.Add(new DateSeparatorViewModel(day));
            lastDay = day;
            var vm = Create(m);
            block.Add(vm);
            _byId[m.Id] = vm;
            prev = vm;
        }
        var firstExisting = Items.OfType<MessageViewModel>().FirstOrDefault();
        if (firstExisting is not null && firstExisting.Item.DateUtc.ToLocalTime().Date != lastDay)
            block.Add(new DateSeparatorViewModel(firstExisting.Item.DateUtc.ToLocalTime().Date));

        for (var i = 0; i < block.Count; i++) Items.Insert(i, block[i]);
        for (var i = 0; i <= block.Count && i < Items.Count; i++) Regroup(i);
    }

    private MessageViewModel Create(MessageItem m)
    {
        var vm = new MessageViewModel(m, Peer) { IsRead = m.IsOut && m.Id <= _readOutboxMaxId };
        vm.EnsurePreview(_tg.Media);
        vm.EnsureSenderAvatar(_tg.Media);
        if (vm.IsVoice || vm.IsAudio) vm.BuildWave();
        VoicePlayback.Attach(vm);
        if (m.ReplyToId is { } r && _byId.TryGetValue(r, out var target)) vm.SetReply(target.Item);
        return vm;
    }

    // Серии сообщений от одного отправителя (< 5 минут) — без повторного имени/аватара.
    private void Regroup(int index)
    {
        if (index < 0 || index >= Items.Count || Items[index] is not MessageViewModel vm) return;
        var prev = index > 0 ? Items[index - 1] as MessageViewModel : null;
        var next = index + 1 < Items.Count ? Items[index + 1] as MessageViewModel : null;
        vm.IsFirstInGroup = !SameGroup(prev, vm);
        vm.IsLastInGroup = !SameGroup(vm, next);
        if (prev is not null) prev.IsLastInGroup = !SameGroup(prev, vm);
    }

    private static bool SameGroup(MessageViewModel? a, MessageViewModel? b) =>
        a is not null && b is not null && !a.IsService && !b.IsService &&
        a.Item.FromKey == b.Item.FromKey && a.IsOut == b.IsOut &&
        (b.Item.DateUtc - a.Item.DateUtc) < TimeSpan.FromMinutes(5);

    private async Task ResolveRepliesAsync(IEnumerable<MessageItem> messages)
    {
        var list = messages.ToList();
        var missing = list.Where(m => m.ReplyToId is { } r && !_byId.ContainsKey(r))
            .Select(m => m.ReplyToId!.Value).Distinct().ToArray();
        foreach (var vm in list.Select(m => _byId.GetValueOrDefault(m.Id)).OfType<MessageViewModel>())
            if (vm.Item.ReplyToId is { } r && _byId.TryGetValue(r, out var t)) vm.SetReply(t.Item);
        if (missing.Length == 0) return;
        try
        {
            var found = (await _tg.GetMessagesAsync(Peer, missing)).ToDictionary(m => m.Id);
            foreach (var vm in _byId.Values.Distinct().Where(v => v.Item.ReplyToId is { } r && missing.Contains(r)))
                vm.SetReply(found.GetValueOrDefault(vm.Item.ReplyToId!.Value));
        }
        catch { /* превью ответа не критично */ }
    }

    // ===================================================================== События

    public void OnMessageReceived(MessageItem m)
    {
        if (IsForum)
        {
            OnForumMessage(m);
            return;
        }
        if (_byId.ContainsKey(m.Id)) return;
        if (_detached)
        {
            // Лента у старого сообщения — новое дописываем в сохранённый конец (покажется по «вниз»).
            if (_liveTail is { } tail && !tail.Any(t => t.Id == m.Id)) tail.Add(m);
            if (!m.IsOut) PendingNewCount++;
            return;
        }
        var atBottom = IsAtBottom?.Invoke() ?? true;
        AppendRange([m], animate: true);
        if (m.ReplyKeyboard is not null || (!m.IsOut && Peer.Kind == PeerKind.Bot)) UpdateReplyKeyboard();
        _ = ResolveRepliesAsync([m]);
        IsTypingNow = false;
        UpdateStatus();
        if (atBottom || m.IsOut)
        {
            ScrollToBottomRequested?.Invoke(true);
            MarkRead();
        }
        else if (!m.IsOut)
        {
            PendingNewCount++;
        }
    }

    public void OnMessageEdited(MessageItem m)
    {
        if (!_byId.TryGetValue(m.Id, out var vm)) return;
        if (vm.IsAlbum)
        {
            if (vm.Album.FirstOrDefault(a => a.Id == m.Id) is { } part) { part.Text = m.Text; }
            if (m.Text.Length > 0) vm.Text = m.Text;
            return;
        }
        if (m.Poll is { } poll) vm.SetPoll(poll);
        if (!m.Reactions.SequenceEqual(vm.ReactionInfos)) vm.SetReactions(m.Reactions);
        if (vm.Text == m.Text && vm.IsEdited == (m.EditDateUtc is not null)) return;
        vm.Text = m.Text;
        vm.IsEdited = m.EditDateUtc is not null;
    }

    public void OnPollUpdated(long pollId, PollInfo poll)
    {
        foreach (var vm in _byId.Values.Distinct().Where(v => v.Poll?.Id == pollId)) vm.SetPoll(poll);
    }

    /// <summary>Сервер: голосовые/кружки прослушаны — убрать точку.</summary>
    public void OnMediaListened(IEnumerable<int> ids)
    {
        foreach (var id in ids)
            if (_byId.TryGetValue(id, out var vm)) { vm.IsMediaUnread = false; vm.Item.MediaUnread = false; }
    }

    /// <summary>Мы послушали чужое голосовое/кружок — точка пропадает и у собеседника (как в Telegram).</summary>
    public void MarkListened(MessageViewModel m)
    {
        if (m.IsOut || !m.IsMediaUnread) return;
        m.IsMediaUnread = false;
        m.Item.MediaUnread = false;
        _ = _tg.MarkMediaListenedAsync(Peer, m.Id);
    }

    public void OnMessagesDeleted(IEnumerable<int> ids)
    {
        foreach (var id in ids)
        {
            if (!_byId.Remove(id, out var vm)) continue;
            if (vm.IsAlbum && vm.Album.Count > 1)
            {
                vm.RemoveFromAlbum(id);
                if (vm.Album.Count > 0) continue;
            }
            var index = Items.IndexOf(vm);
            if (index < 0) continue;
            Items.RemoveAt(index);
            // Убираем «висящий» разделитель даты.
            if (index > 0 && Items[index - 1] is DateSeparatorViewModel && (index >= Items.Count || Items[index] is DateSeparatorViewModel))
                Items.RemoveAt(index - 1);
            Regroup(Math.Min(index, Items.Count - 1));
            Regroup(index - 1);
        }
    }

    public void OnOutboxRead(int maxId)
    {
        _readOutboxMaxId = Math.Max(_readOutboxMaxId, maxId);
        foreach (var vm in _byId.Values.Distinct().Where(v => v.IsOut && !v.IsRead && v.Id <= _readOutboxMaxId)) vm.IsRead = true;
    }

    public async void OnTyping(PeerRef who)
    {
        _typingCts?.Cancel();
        var cts = _typingCts = new CancellationTokenSource();
        IsTypingNow = true;
        Status = Peer.IsUser ? "печатает" : $"{who.DisplayTitle} печатает";
        try
        {
            await Task.Delay(5500, cts.Token);
            IsTypingNow = false;
            UpdateStatus();
        }
        catch (TaskCanceledException) { }
    }

    public void UpdateStatus()
    {
        if (IsTypingNow) return;
        Status = Peer.Kind switch
        {
            _ when Peer.IsSelf => "сохранённые сообщения",
            PeerKind.Bot => "бот",
            PeerKind.User => Fmt.LastSeen(_tg.GetPresence(Peer).Presence, _tg.GetPresence(Peer).LastSeen),
            PeerKind.Channel => Peer.MembersCount > 0
                ? $"{Fmt.N(Peer.MembersCount)} {Fmt.Plural(Peer.MembersCount, "подписчик", "подписчика", "подписчиков")}" : "канал",
            _ => Peer.MembersCount > 0
                ? $"{Fmt.N(Peer.MembersCount)} {Fmt.Plural(Peer.MembersCount, "участник", "участника", "участников")}" : "группа",
        };
    }

    public void MarkRead()
    {
        // Чат в памяти, но не на экране (быстрый ответ закрыт, окно свёрнуто) — сообщения не прочитаны.
        if (!_owner.Shell.IsOnScreen(Peer.Key)) return;
        if (IsThread)
        {
            // Тема форума отмечается прочитанной отдельно; ветки комментариев — нет.
            if (IsTopic && !_generalTopic && ThreadTopId is { } topic && LastMessageId > _lastMarkedRead)
            {
                _lastMarkedRead = LastMessageId;
                _ = _tg.ReadTopicAsync(Peer, topic, LastMessageId);
            }
            if (!_generalTopic) return;
            // «Общее»: Telegram ведёт прочтение тем отдельно — отмечаем и тему (id 1), и историю группы.
            if (LastMessageId > _lastMarkedRead) _ = _tg.ReadTopicAsync(Peer, 1, LastMessageId);
        }
        var max = LastMessageId;
        if (max <= _lastMarkedRead || max == 0) return;
        _lastMarkedRead = max;
        _owner.OnChatRead(Peer.Key);
        _ = _tg.MarkReadAsync(Peer, max);
    }

    // ===================================================================== Esc

    /// <summary>Закрыть верхний слой. false — закрывать в чате нечего (можно закрыть сам чат).</summary>
    public bool HandleEscape()
    {
        if (IsViewerOpen) { CloseViewer(); return true; }
        if (IsSelecting) { ClearSelection(); return true; }
        if (IsForwardOpen) { Forwarding = null; return true; }
        if (IsStickerPackOpen) { StickerPack = null; return true; }
        if (IsPickerOpen) { IsPickerOpen = false; return true; }
        if (HasAttachments) { Attachments.Clear(); OnPropertyChanged(nameof(HasAttachments)); return true; }
        if (HasComposerBar) { CancelComposerBar(); return true; }
        if (SideProfile is { IsAvatarViewerOpen: true } sp) { sp.IsAvatarViewerOpen = false; return true; }
        if (IsSideProfileOpen) { SideBack(); return true; }
        return false;
    }

    // ===================================================================== Композер

    partial void OnDraftChanged(string value)
    {
        OnPropertyChanged(nameof(ShowMic));
        if (value.Length > 0 && Editing is null) _ = _tg.NotifyTypingAsync(Peer);
    }

    private bool CanSend() => (!string.IsNullOrWhiteSpace(Draft) || HasAttachments) && !IsSending;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (HasAttachments)
        {
            await SendAttachmentsAsync(text);
            return;
        }
        if (text.Length == 0) return;
        IsSending = true;
        try
        {
            if (Editing is { } edit)
            {
                await _tg.EditTextAsync(Peer, edit.Id, text);
                edit.Text = text;
                edit.IsEdited = true;
                Editing = null;
                Draft = "";
            }
            else
            {
                var reply = ReplyTo?.Id ?? SendTopId;
                Draft = "";
                ReplyTo = null;
                var sent = await _tg.SendTextAsync(Peer, text, reply);
                if (!_byId.ContainsKey(sent.Id))
                {
                    OnMessageReceived(sent);
                    _owner.OnMessageReceived(sent, fromConversation: true);
                }
            }
        }
        catch (Exception ex)
        {
            if (Editing is null && Draft.Length == 0) Draft = text; // вернуть текст, если не ушло
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            IsSending = false;
        }
    }

    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var p in paths.Where(File.Exists))
            if (Attachments.All(a => a.Path != p)) Attachments.Add(new AttachmentViewModel(p));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentsTitle));
        SendCommand.NotifyCanExecuteChanged();
    }

    // ===================================================================== Запись голосовых (как в Telegram Desktop)
    // Пустое поле — вместо «отправить» микрофон: держите — идёт запись, отпустили — отправилось,
    // увели курсор с кнопки и отпустили — отмена, потянули вверх — запись «закрепляется» без удержания.

    private VoiceRecorder? _recorder;
    private System.Windows.Threading.DispatcherTimer? _recordTimer;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMic))] private bool _isRecording;
    [ObservableProperty] private bool _isRecordLocked;
    [ObservableProperty] private string _recordTime = "0:00,0";
    [ObservableProperty] private double _recordLevel;

    public bool ShowMic => string.IsNullOrWhiteSpace(Draft) && Editing is null && !HasAttachments;

    partial void OnEditingChanged(MessageViewModel? value) => OnPropertyChanged(nameof(ShowMic));

    public void StartRecording()
    {
        if (IsRecording || !CanWrite) return;
        try
        {
            AudioPlayer.Instance.Stop();
            _recorder = new VoiceRecorder();
            _recorder.Start();
        }
        catch (Exception ex)
        {
            _recorder = null;
            _owner.ShowToast("Микрофон недоступен: " + TgErrors.Describe(ex), true);
            return;
        }
        IsRecordLocked = false;
        IsRecording = true;
        _recordTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _recordTimer.Tick += (_, _) =>
        {
            if (_recorder is null) return;
            var s = _recorder.Seconds;
            RecordTime = $"{(int)s / 60}:{(int)s % 60:00},{(int)(s * 10) % 10}";
            RecordLevel = _recorder.Level;
        };
        _recordTimer.Start();
        _ = _tg.NotifyTypingAsync(Peer);
    }

    [RelayCommand]
    private void LockRecording() => IsRecordLocked = IsRecording;

    [RelayCommand]
    private void CancelRecording() => _ = FinishRecordingAsync(cancel: true);

    [RelayCommand]
    private Task SendRecording() => FinishRecordingAsync(cancel: false);

    public async Task FinishRecordingAsync(bool cancel)
    {
        if (_recorder is not { } recorder) return;
        _recorder = null;
        _recordTimer?.Stop();
        IsRecording = false;
        IsRecordLocked = false;
        RecordLevel = 0;
        var voice = await Task.Run(() => recorder.Stop(cancel));
        RecordTime = "0:00,0";
        if (voice is null)
        {
            if (!cancel) _owner.ShowToast("Удерживайте кнопку, чтобы записать голосовое");
            return;
        }
        try
        {
            var reply = ReplyTo?.Id ?? SendTopId;
            ReplyTo = null;
            var sent = await _tg.SendVoiceAsync(Peer, voice, reply);
            OnMessageReceived(sent);
            _owner.OnMessageReceived(sent, fromConversation: true);
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    public string AttachmentsTitle
    {
        get
        {
            var n = Attachments.Count;
            if (n > 0 && !SendAsFiles && Attachments.All(a => a.IsImage || a.IsVideo))
                return Attachments.All(a => a.IsImage) ? $"Отправить {Fmt.N(n)} {Fmt.Plural(n, "фото", "фото", "фото")}" : $"Отправить {Fmt.N(n)} медиа";
            return $"Отправить {Fmt.N(n)} {Fmt.Plural(n, "файл", "файла", "файлов")}";
        }
    }

    partial void OnSendAsFilesChanged(bool value) => OnPropertyChanged(nameof(AttachmentsTitle));

    [RelayCommand]
    private void CancelAttachments()
    {
        Attachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ctrl+V: файлы и картинки из буфера обмена — во вложения. true — вставка обработана.</summary>
    public bool PasteFromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                AddAttachments(System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList());
                return true;
            }
            if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } image)
            {
                var dir = Path.Combine(Path.GetTempPath(), "TgCheck", "paste");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"image_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using (var fs = File.Create(file)) encoder.Save(fs);
                AddAttachments([file]);
                return true;
            }
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
        return false; // обычный текст вставит само поле ввода
    }

    [RelayCommand]
    private void Attach()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Прикрепить файлы" };
        if (dlg.ShowDialog() == true) AddAttachments(dlg.FileNames);
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentViewModel? a)
    {
        if (a is null) return;
        Attachments.Remove(a);
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentsTitle));
        SendCommand.NotifyCanExecuteChanged();
    }

    private async Task SendAttachmentsAsync(string caption)
    {
        IsSending = true;
        var files = Attachments.ToList();
        try
        {
            await _tg.SendFilesAsync(Peer, files.Select(f => f.Path).ToList(), caption.Length > 0 ? caption : null, ReplyTo?.Id ?? SendTopId,
                SendAsFiles, new Progress<(int Index, double Fraction)>(p => files[p.Index].Progress = p.Fraction));
            Attachments.Clear();
            OnPropertyChanged(nameof(HasAttachments));
            Draft = "";
            ReplyTo = null;
            ScrollToBottomRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void TogglePicker() => IsPickerOpen = !IsPickerOpen;

    partial void OnIsPickerOpenChanged(bool value)
    {
        if (value) _ = Picker.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void InsertEmoji(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        Picker.RememberEmoji(emoji);
        InsertTextRequested?.Invoke(emoji);
    }

    [RelayCommand]
    private async Task SendSticker(MediaHolder? media)
    {
        if (media is null) return;
        IsPickerOpen = false;
        try
        {
            var reply = ReplyTo?.Id ?? SendTopId;
            ReplyTo = null;
            var sent = await _tg.SendDocumentAsync(Peer, media, reply);
            OnMessageReceived(sent);
            _owner.OnMessageReceived(sent, fromConversation: true);
            if (media is StickerItem sticker)
            {
                _owner.Shell.Picker.AddRecentSticker(sticker);
                _ = _tg.SaveRecentStickerAsync(sticker);
            }
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
    }

    [RelayCommand]
    private void Reply(MessageViewModel? m)
    {
        if (m is null || m.IsService) return;
        Editing = null;
        ReplyTo = m;
    }

    [RelayCommand]
    private void Edit(MessageViewModel? m)
    {
        if (m is null || !m.IsOut || m.IsService) return;
        ReplyTo = null;
        Editing = m;
        Draft = m.Text;
    }

    [RelayCommand]
    private void CancelComposerBar()
    {
        if (Editing is not null) Draft = "";
        Editing = null;
        ReplyTo = null;
    }

    [RelayCommand]
    private async Task CopyImage(MessageViewModel? m)
    {
        if (m?.Item.Media is not MediaKind.Photo) return;
        try
        {
            var bytes = await _tg.Media.GetFullPhotoAsync(m.Item);
            if (bytes is null || Images.FromBytes(bytes) is not { } image) return;
            Clipboard.SetImage(image);
            _owner.ShowToast("Картинка скопирована");
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    // ---- просмотр набора стикеров (клик по стикеру в чате)
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsStickerPackOpen))] private StickerPackViewModel? _stickerPack;
    public bool IsStickerPackOpen => StickerPack is not null;

    [RelayCommand]
    private async Task OpenStickerPack(MessageViewModel? m)
    {
        if (m is null || !m.IsSticker) return;
        try
        {
            var pack = await _tg.GetStickerPackOfAsync(m.Item);
            if (pack is not { } p) { _owner.ShowToast("Набор стикеров недоступен"); return; }
            StickerPack = new StickerPackViewModel(p.Info, p.Stickers, p.Installed);
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    [RelayCommand]
    private void CloseStickerPack() => StickerPack = null;

    [RelayCommand]
    private async Task ToggleStickerPackInstalled()
    {
        if (StickerPack is not { } pack) return;
        try
        {
            await _tg.SetStickerPackInstalledAsync(pack.Info, !pack.Installed);
            pack.Installed = !pack.Installed;
            _owner.ShowToast(pack.Installed ? "Набор добавлен" : "Набор удалён");
            Picker.Reset(); // панель стикеров перечитает наборы
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    [RelayCommand]
    private async Task SendFromPack(StickerItem? sticker)
    {
        if (sticker is null || !CanWrite) return;
        StickerPack = null;
        await SendSticker(sticker);
    }

    // ---- реакции: одна своя реакция (как без премиума), повторный выбор — снять
    public IReadOnlyList<string> QuickReactions { get; } = TelegramService.QuickReactions;

    /// <summary>Параметр — MessageViewModel и эмодзи (из меню) либо плашка реакции (клик под сообщением).</summary>
    [RelayCommand]
    private async Task React(object? parameter)
    {
        MessageViewModel? m;
        ReactionInfo picked;
        switch (parameter)
        {
            case ReactionViewModel chip:
                m = chip.Message;
                picked = chip.Info;
                break;
            case QuickReactionItem quick:
                m = quick.Message;
                picked = quick.Choice;
                break;
            default:
                return;
        }
        if (picked.IsPaid) return; // звёздные реакции не ставим
        var before = m.ReactionInfos;
        var mineAlready = before.Any(r => r.Chosen && r.Key == picked.Key);
        // Оптимистично обновляем сразу, сервер затем пришлёт точные цифры.
        var after = before
            .Select(r => r.Chosen ? r with { Count = r.Count - 1, Chosen = false } : r)
            .Where(r => r.Count > 0)
            .ToList();
        if (!mineAlready)
        {
            var index = after.FindIndex(r => r.Key == picked.Key);
            if (index >= 0) after[index] = after[index] with { Count = after[index].Count + 1, Chosen = true };
            else after.Add(picked with { Count = 1, Chosen = true });
        }
        m.SetReactions(after, mineAlready ? null : picked.Key);
        try
        {
            await _tg.SetReactionsAsync(Peer, m.Id, mineAlready ? [] : [picked]);
        }
        catch (Exception ex)
        {
            m.SetReactions(before);
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
    }

    private async Task LoadAllowedReactionsAsync() =>
        ReactionCatalog.SetAllowed(Peer.Key, await _tg.GetAllowedReactionsAsync(Peer));

    public void OnReactions(int messageId, IReadOnlyList<ReactionInfo> reactions)
    {
        if (_byId.TryGetValue(messageId, out var vm) && vm.Id == messageId) vm.SetReactions(reactions);
    }

    // ---- пересылка: выбор чата из своего списка с поиском
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsForwardOpen))] private MessageViewModel? _forwarding;
    [ObservableProperty] private string _forwardQuery = "";
    public bool IsForwardOpen => Forwarding is not null;
    public ObservableCollection<ChatItemViewModel> ForwardChats { get; } = [];

    partial void OnForwardQueryChanged(string value) => FillForwardChats();

    private void FillForwardChats()
    {
        ForwardChats.Clear();
        var q = ForwardQuery.Trim();
        var list = _owner.Chats
            .Where(c => q.Length > 0 || !c.IsArchived)
            .Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                        c.Peer.Username?.Contains(q.TrimStart('@'), StringComparison.OrdinalIgnoreCase) == true)
            .Where(c => c.Peer.CanWrite)
            .OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.SortDate)
            .Take(60);
        foreach (var c in list)
        {
            c.EnsureAvatar(_tg.Media);
            ForwardChats.Add(c);
        }
    }

    [RelayCommand]
    private void Forward(MessageViewModel? m)
    {
        if (m is null) return;
        ForwardQuery = "";
        Forwarding = m;
        FillForwardChats();
    }

    [RelayCommand]
    private void CancelForward() => Forwarding = null;

    [RelayCommand]
    private async Task ForwardTo(ChatItemViewModel? target)
    {
        if (Forwarding is not { } m || target is null) return;
        Forwarding = null;
        var batch = _forwardBatch;
        _forwardBatch = null;
        try
        {
            var ids = batch ?? (m.IsAlbum ? m.Album.Select(a => a.Id).ToList() : [m.Id]);
            await _tg.ForwardAsync(Peer, ids, target.Peer);
            _owner.ShowToast($"Переслано: {target.Title}");
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    // ===================================================================== Выделение нескольких сообщений

    private List<int>? _forwardBatch;
    public ObservableCollection<MessageViewModel> Selected { get; } = [];
    public bool IsSelecting => Selected.Count > 0;
    public string SelectedText => $"{Fmt.N(Selected.Count)} {Fmt.Plural(Selected.Count, "сообщение", "сообщения", "сообщений")}";
    /// <summary>Удалить выбранные можно, только если все они свои.</summary>
    public bool CanDeleteSelected => Selected.Count > 0 && Selected.All(m => m.IsOut);

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(IsSelecting));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    /// <summary>Выбрать / снять сообщение (меню «Выделить», Ctrl+клик, клик в режиме выделения).</summary>
    [RelayCommand]
    public void ToggleSelect(MessageViewModel? m)
    {
        if (m is null || m.Item.ServiceText is not null) return;
        m.IsSelected = !m.IsSelected;
        if (m.IsSelected) Selected.Add(m); else Selected.Remove(m);
        SelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var m in Selected) m.IsSelected = false;
        Selected.Clear();
        SelectionChanged();
    }

    private List<int> SelectedIds() =>
        Selected.OrderBy(m => m.Id).SelectMany(m => m.IsAlbum ? m.Album.Select(a => a.Id) : [m.Id]).Distinct().ToList();

    [RelayCommand]
    private void ForwardSelected()
    {
        if (Selected.Count == 0) return;
        _forwardBatch = SelectedIds();
        var first = Selected.OrderBy(m => m.Id).First();
        ClearSelection();
        ForwardQuery = "";
        Forwarding = first;
        FillForwardChats();
    }

    /// <summary>Скопировать текст выбранных — как в Telegram: «Имя, [время]» и текст.</summary>
    [RelayCommand]
    private void CopySelected()
    {
        if (Selected.Count == 0) return;
        var text = string.Join("\n\n", Selected.OrderBy(m => m.Id).Select(m =>
            $"{(m.IsOut ? "Вы" : m.SenderName)}, [{m.Item.DateUtc.ToLocalTime():dd.MM.yyyy HH:mm}]\n{(m.HasText ? m.Text : m.Item.Preview)}"));
        try { Clipboard.SetText(text); _owner.ShowToast("Скопировано"); }
        catch { /* буфер занят */ }
        ClearSelection();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (!CanDeleteSelected) return;
        var ids = SelectedIds().ToArray();
        _owner.Confirm = new ConfirmRequest(
            $"Удалить {SelectedText}?", "Сообщения будут удалены для всех.", "Удалить", true,
            async () =>
            {
                await _tg.DeleteAsync(Peer, ids, true);
                OnMessagesDeleted(ids);
                _owner.OnLocalDeleted(Peer.Key);
                ClearSelection();
            });
    }

    [RelayCommand]
    private void Copy(MessageViewModel? m)
    {
        if (m is null) return;
        try
        {
            Clipboard.SetText(m.HasText ? m.Text : m.Item.Preview);
            _owner.ShowToast("Скопировано");
        }
        catch { /* буфер занят */ }
    }

    [RelayCommand]
    private void Delete(MessageViewModel? m)
    {
        if (m is null) return;
        var everyone = m.IsOut || Peer.Kind != PeerKind.User;
        var ids = m.AllIds.ToArray();
        _owner.Confirm = new ConfirmRequest(
            ids.Length > 1 ? $"Удалить {ids.Length} сообщения?" : "Удалить сообщение?",
            Peer.Kind == PeerKind.User
                ? "Сообщение будет удалено у вас и у собеседника."
                : "Сообщение будет удалено для всех участников (нужны права администратора, если оно не ваше).",
            "Удалить", true,
            async () =>
            {
                await _tg.DeleteAsync(Peer, ids, everyone);
                OnMessagesDeleted(ids);
                _owner.OnLocalDeleted(Peer.Key);
            });
    }

    [RelayCommand]
    private async Task Download(MessageViewModel? m)
    {
        if (m is null || !m.Item.CanDownload) return;
        var ext = m.Item.Media switch
        {
            MediaKind.Photo => ".jpg",
            MediaKind.Voice => ".ogg",
            MediaKind.Video or MediaKind.VideoNote or MediaKind.Gif => ".mp4",
            _ => "",
        };
        var name = m.Item.Media is MediaKind.Document or MediaKind.Audio && m.FileTitle is { } t ? t : $"telegram_{m.Id}{ext}";
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Sanitize(name) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            m.DownloadProgress = 0;
            await using (var fs = File.Create(dlg.FileName))
                await _tg.DownloadMediaAsync(m.Item, fs, new Progress<double>(p => m.DownloadProgress = p));
            _owner.ShowToast("Файл сохранён");
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            m.DownloadProgress = -1;
        }
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---- голосовые и аудио
    [RelayCommand]
    private async Task PlayAudio(MessageViewModel? m)
    {
        if (m is null) return;
        try
        {
            if (AudioPlayer.Instance.Key == m.AudioKey)
            {
                AudioPlayer.Instance.Toggle(m.AudioKey, "");
                return;
            }
            m.IsLoadingAudio = true;
            NowPlaying.Instance.RequestedAudio = m.AudioKey;
            var path = await _tg.Media.GetMediaFileAsync(m.Item, new Progress<double>(p => m.DownloadProgress = p));
            m.DownloadProgress = -1;
            m.IsLoadingAudio = false;
            if (path is null) throw new IOException("Не удалось загрузить аудио.");
            // Пока файл качался, выбрали другое сообщение — играем только последнее выбранное.
            if (NowPlaying.Instance.RequestedAudio != m.AudioKey) return;
            AudioPlayer.Instance.Play(m.AudioKey, path);
            MarkListened(m);
            NowPlaying.Instance.StartAudio(m.AudioKey, m.IsOut ? "Вы" : (m.SenderName is { Length: > 0 } s ? s : Title),
                m.IsVoice ? "Голосовое сообщение" : (m.FileTitle ?? "Аудио"));
        }
        catch (Exception ex)
        {
            m.IsLoadingAudio = false;
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
    }

    [RelayCommand]
    private void SeekAudio(object? parameter)
    {
        if (parameter is (MessageViewModel m, double fraction) && AudioPlayer.Instance.Key == m.AudioKey)
            AudioPlayer.Instance.Seek(fraction);
    }

    // ---- опросы
    [RelayCommand]
    private async Task Vote(PollOptionViewModel? option)
    {
        var m = _byId.Values.Distinct().FirstOrDefault(v => v.PollOptions.Contains(option!));
        if (option is null || m?.Poll is not { } poll || poll.IsClosed || poll.ShowResults && !poll.IsMultiple) return;
        if (poll.IsMultiple)
        {
            option.IsSelected = !option.IsSelected;
            return;
        }
        await SendVoteAsync(m, [option.Option]);
    }

    [RelayCommand]
    private Task SubmitVotes(MessageViewModel? m) =>
        m is null ? Task.CompletedTask : SendVoteAsync(m, m.PollOptions.Where(o => o.IsSelected).Select(o => o.Option).ToList());

    [RelayCommand]
    private Task RetractVote(MessageViewModel? m) => m is null ? Task.CompletedTask : SendVoteAsync(m, []);

    private async Task SendVoteAsync(MessageViewModel m, IReadOnlyList<PollOption> options)
    {
        if (m.IsVoting || (options.Count == 0 && !m.CanRetractVote)) return;
        m.IsVoting = true;
        try
        {
            if (await _tg.VotePollAsync(Peer, m.Item, options) is { } updated) m.SetPoll(updated);
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            m.IsVoting = false;
        }
    }

    // ---- навигация
    [RelayCommand]
    private void OpenSender(MessageViewModel? m)
    {
        if (m?.Item.From is { } from) _owner.Shell.OpenProfile(from);
    }

    /// <summary>Клик по шапке — мини-профиль справа.</summary>
    [RelayCommand]
    private void ToggleSideProfile()
    {
        if (SideProfile is not null) { SideProfile = null; return; }
        // Аватар уже загружен в шапке — не качаем его заново.
        var vm = new ProfileViewModel(_tg, Peer, () => Task.CompletedTask, _owner.Shell, compact: true) { Avatar = Avatar };
        SideProfile = vm;
        _ = vm.LoadAsync();
        _ = LoadSharedAsync(SharedTab);
    }

    // ===================================================================== Боты: кнопки, клавиатура, меню, мини-приложения

    /// <summary>Клавиатура бота под полем ввода (из последнего сообщения, где она пришла).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasReplyKeyboard), nameof(ShowReplyKeyboard))]
    private IReadOnlyList<IReadOnlyList<BotButton>>? _replyKeyboard;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowReplyKeyboard))] private bool _isReplyKeyboardOpen = true;
    public bool ShowReplyKeyboard => HasReplyKeyboard && IsReplyKeyboardOpen;
    public bool HasReplyKeyboard => ReplyKeyboard is { Count: > 0 };

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBotMenu))] private BotMenu? _botMenu;
    public bool HasBotMenu => BotMenu is not null;

    private void UpdateReplyKeyboard()
    {
        var withKeyboard = Items.OfType<MessageViewModel>().LastOrDefault(m => m.Item.ReplyKeyboard is not null && !m.IsOut);
        ReplyKeyboard = withKeyboard?.Item.ReplyKeyboard;
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowStartBot));

    /// <summary>Бот, с которым ещё нет переписки: вместо поля ввода — кнопка «Старт» (как в Telegram).</summary>
    public bool ShowStartBot => Peer.Kind == PeerKind.Bot && _loadedOnce && !IsLoading && !Items.OfType<MessageViewModel>().Any();
    [ObservableProperty] private bool _isStartingBot;

    [RelayCommand]
    private async Task StartBot()
    {
        if (IsStartingBot) return;
        IsStartingBot = true;
        try
        {
            await _tg.StartBotAsync(Peer);
            _loadedOnce = false; // перечитать историю — пришёл ответ бота
            await LoadAsync();
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
        finally
        {
            IsStartingBot = false;
            OnPropertyChanged(nameof(ShowStartBot));
        }
    }

    private async Task LoadBotMenuAsync()
    {
        if (Peer.Kind != PeerKind.Bot || BotMenu is not null) return;
        BotMenu = await _tg.GetBotMenuAsync(Peer);
    }

    [RelayCommand]
    private async Task PressKeyboardButton(BotButton? button)
    {
        if (button is null) return;
        try
        {
            var sent = await _tg.SendKeyboardButtonAsync(Peer, button);
            OnMessageReceived(sent);
            _owner.OnMessageReceived(sent, fromConversation: true);
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    [RelayCommand]
    private void ToggleReplyKeyboard() => IsReplyKeyboardOpen = !IsReplyKeyboardOpen;

    [RelayCommand]
    private async Task OpenBotMenu()
    {
        if (BotMenu is not { } menu) return;
        await OpenWebAppAsync(Peer, menu.Url, fromMenu: menu.Url is not null, title: menu.Text);
    }

    [RelayCommand]
    private async Task PressInline(InlineButtonViewModel? b)
    {
        if (b is null) return;
        var msg = b.Message;
        var button = b.Button;
        try
        {
            switch (button.Kind)
            {
                case BotButtonKind.Callback:
                    var answer = await _tg.PressCallbackAsync(Peer, msg.Id, button);
                    if (answer.Url is { } cbUrl) Shell.Open(cbUrl);
                    if (answer.Message is { Length: > 0 } text)
                    {
                        if (answer.IsAlert) MessageBox.Show(Application.Current.MainWindow!, text, Title);
                        else _owner.ShowToast(text);
                    }
                    break;
                case BotButtonKind.Url when button.Url is { } url:
                    Shell.Open(url);
                    break;
                case BotButtonKind.WebApp or BotButtonKind.SimpleWebApp:
                    var bot = msg.Item.From is { Kind: PeerKind.Bot } from ? from : Peer;
                    await OpenWebAppAsync(bot, button.Url, fromMenu: false, title: button.Text);
                    break;
                case BotButtonKind.Copy when button.CopyText is { } copy:
                    Clipboard.SetText(copy);
                    _owner.ShowToast("Скопировано");
                    break;
                case BotButtonKind.Profile:
                    _owner.Shell.OpenProfile(await _tg.ResolveAsync(button.UserId.ToString()));
                    break;
                default:
                    _owner.ShowToast("Эта кнопка пока не поддерживается");
                    break;
            }
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    private async Task OpenWebAppAsync(PeerRef bot, string? url, bool fromMenu, string title)
    {
        // Окно — сразу (WebView2 готовится, пока Telegram выдаёт адрес приложения), а не после ответа сервера.
        var appUrl = _tg.RequestWebAppAsync(Peer, bot, url, fromMenu);
        var window = new Views.WebAppWindow(title.Length > 0 ? title : bot.DisplayTitle, appUrl,
            link => _ = _owner.Shell.OpenLinkAsync(link)) { Owner = Application.Current.MainWindow };
        window.Show();
        try { await appUrl; }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    // ===================================================================== Комментарии к постам

    /// <summary>Комментарии открываются как обычный чат-ветка (как в Telegram Desktop), «назад» — к каналу.</summary>
    [RelayCommand]
    private async Task OpenComments(MessageViewModel? post)
    {
        if (post is null) return;
        SideProfile = null;
        try
        {
            var (discussion, topId, top) = await _tg.GetThreadAsync(Peer, post.Id);
            _owner.OpenThread(new ConversationViewModel(_tg, _owner, discussion, topId, this, top));
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    // ===================================================================== Закреплённые и переход к сообщению

    public ObservableCollection<MessageItem> Pinned { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PinnedText), nameof(PinnedTitle), nameof(HasPinned))] private int _pinnedIndex;
    public bool HasPinned => Pinned.Count > 0;
    public string PinnedTitle => Pinned.Count > 1 ? $"Закреплённое сообщение {PinnedIndex + 1} из {Pinned.Count}" : "Закреплённое сообщение";
    public string PinnedText => Pinned.Count > 0 ? Pinned[Math.Min(PinnedIndex, Pinned.Count - 1)].Preview : "";

    /// <summary>View: прокрутить к сообщению и подсветить его.</summary>
    public event Action<MessageViewModel>? ScrollToMessageRequested;
    /// <summary>View: лента сейчас будет пересобрана (переход далеко) — спрятать её, чтобы не мигала.</summary>
    public event Action? FeedResetting;
    public bool IsDetached => _detached;
    private bool _detached;
    private List<MessageItem>? _liveTail;

    private async Task LoadPinnedAsync()
    {
        var pinned = await _tg.GetPinnedAsync(Peer);
        Pinned.Clear();
        foreach (var p in pinned) Pinned.Add(p);
        PinnedIndex = 0;
        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(PinnedText));
        OnPropertyChanged(nameof(PinnedTitle));
        PrefetchNextPinned();
    }

    /// <summary>Клик по плашке закрепа: перейти к сообщению, следующий клик — к более старому закрепу.</summary>
    [RelayCommand]
    private async Task OpenPinned()
    {
        if (Pinned.Count == 0) return;
        var target = Pinned[Math.Min(PinnedIndex, Pinned.Count - 1)];
        PinnedIndex = (PinnedIndex + 1) % Pinned.Count;
        await JumpToMessageAsync(target.Id);
        PrefetchNextPinned();
    }

    // Окрестность следующего закрепа — заранее, в фоне: клик по плашке переходит сразу, без ожидания сети.
    private readonly Dictionary<int, Task<IReadOnlyList<MessageItem>>> _aroundCache = [];

    private void PrefetchNextPinned()
    {
        if (Pinned.Count == 0) return;
        var next = Pinned[Math.Min(PinnedIndex, Pinned.Count - 1)].Id;
        if (_byId.ContainsKey(next) || _aroundCache.ContainsKey(next)) return;
        var task = _tg.GetHistoryAroundAsync(Peer, next, 40);
        _aroundCache[next] = task;
        task.ContinueWith(_ => _aroundCache.Remove(next), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public async Task JumpToMessageAsync(int id)
    {
        if (!_byId.ContainsKey(id))
        {
            try
            {
                // Уже подгружено заранее (следующий закреп) — без запроса; иначе — 40 сообщений вокруг (меньше пересборка).
                var around = _aroundCache.Remove(id, out var prefetched) ? await prefetched : await _tg.GetHistoryAroundAsync(Peer, id, 40);
                // Конец ленты запоминаем (один раз — при первом уходе от него): «вниз» вернёт его мгновенно.
                if (!_detached)
                    _liveTail = Items.OfType<MessageViewModel>().SelectMany(v => v.IsAlbum ? v.Album.Select(a => a.Item) : [v.Item]).TakeLast(60).ToList();
                FeedResetting?.Invoke();
                ResetItems(around, staged: false); // нужное сообщение — в середине, оно должно быть сразу
                HasMoreHistory = true;
                _detached = true;
            }
            catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); return; }
        }
        if (_byId.TryGetValue(id, out var vm))
        {
            ScrollToMessageRequested?.Invoke(vm);
            vm.IsHighlighted = true;
            await Task.Delay(1600);
            vm.IsHighlighted = false;
        }
        else ScrollToBottomRequested?.Invoke(false);
    }

    [RelayCommand]
    private Task JumpToReply(MessageViewModel? m) => m?.Item.ReplyToId is { } r ? JumpToMessageAsync(r) : Task.CompletedTask;

    [RelayCommand]
    private void OpenMap(MessageViewModel? m)
    {
        if (m?.MapUrl is { } url) Shell.Open(url);
    }

    // ===================================================================== Мини-профиль: переходы внутри панели

    private readonly Stack<ProfileViewModel> _sideStack = new();
    public bool SideCanGoBack => _sideStack.Count > 0;
    /// <summary>В панели открыт не собеседник этого чата (участник группы) — можно написать ему.</summary>
    public bool SideCanWrite => SideProfile?.Profile?.Peer is { IsUser: true } p && p.Key != Peer.Key && !p.IsSelf;

    partial void OnSideProfileChanged(ProfileViewModel? value)
    {
        if (value is null) _sideStack.Clear();
        OnPropertyChanged(nameof(SideCanGoBack));
        OnPropertyChanged(nameof(SideCanWrite));
        if (value is not null) value.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileViewModel.Profile)) OnPropertyChanged(nameof(SideCanWrite));
        };
    }

    /// <summary>Участник группы → его мини-профиль в этой же панели.</summary>
    [RelayCommand]
    private void OpenSideMember(PeerResultViewModel? member)
    {
        if (member is null || SideProfile is null) return;
        _sideStack.Push(SideProfile);
        var vm = new ProfileViewModel(_tg, member.Peer, () => Task.CompletedTask, _owner.Shell, compact: true) { Avatar = member.Avatar };
        SideProfile = vm;
        OnPropertyChanged(nameof(SideCanGoBack));
        _ = vm.LoadAsync();
    }

    [RelayCommand]
    private void SideBack()
    {
        if (_sideStack.Count == 0) { SideProfile = null; return; }
        var prev = _sideStack.Pop();
        SideProfile = prev;
        OnPropertyChanged(nameof(SideCanGoBack));
    }

    [RelayCommand]
    private void SideWrite()
    {
        if (SideProfile?.Profile?.Peer is not { } peer) return;
        SideProfile = null;
        _owner.Shell.OpenChat(peer);
    }

    // ===================================================================== Общие медиа (вкладки в мини-профиле)

    public ObservableCollection<MessageViewModel> SharedItems { get; } = [];
    [ObservableProperty] private SharedMediaKind _sharedTab = SharedMediaKind.Media;
    [ObservableProperty] private bool _isSharedLoading;
    public bool SharedIsGrid => SharedTab == SharedMediaKind.Media;
    private CancellationTokenSource? _sharedCts;

    partial void OnSharedTabChanged(SharedMediaKind value)
    {
        OnPropertyChanged(nameof(SharedIsGrid));
        _ = LoadSharedAsync(value);
    }

    private async Task LoadSharedAsync(SharedMediaKind kind)
    {
        _sharedCts?.Cancel();
        var cts = _sharedCts = new CancellationTokenSource();
        SharedItems.Clear();
        IsSharedLoading = true;
        try
        {
            var items = await _tg.SearchMediaAsync(Peer, kind);
            if (cts.IsCancellationRequested) return;
            foreach (var m in items)
            {
                var vm = new MessageViewModel(m, Peer);
                vm.EnsurePreview(_tg.Media);
                if (vm.IsVoice || vm.IsAudio) vm.BuildWave();
                VoicePlayback.Attach(vm);
                SharedItems.Add(vm);
            }
        }
        catch (Exception ex) { TgErrors.Describe(ex); }
        finally
        {
            if (!cts.IsCancellationRequested) IsSharedLoading = false;
        }
    }

    // ===================================================================== Автопроигрывание голосовых по порядку

    private void OnAudioEnded(string key) => Anim.Ui(() =>
    {
        var all = Items.OfType<MessageViewModel>().ToList();
        var index = all.FindIndex(v => v.AudioKey == key);
        if (index < 0) return;
        var next = all.Skip(index + 1).FirstOrDefault(v => v.IsVoice);
        if (next is not null) PlayAudioCommand.Execute(next);
    });

    [RelayCommand]
    private void OpenFullProfile()
    {
        SideProfile = null;
        _owner.Shell.OpenProfile(Peer);
    }

    [RelayCommand]
    private void CloseChat() => _owner.CloseChat();

    [RelayCommand]
    private async Task JumpToBottom()
    {
        PendingNewCount = 0;
        if (_detached)
        {
            // После перехода к старому сообщению лента «оторвана» — возвращаем последние: сохранённый конец ленты
            // (с пришедшими за это время сообщениями) — сразу, без запроса; нет его — с сервера.
            _detached = false;
            var tail = _liveTail;
            _liveTail = null;
            FeedResetting?.Invoke();
            try { ResetItems(tail is { Count: > 0 } ? tail : await FetchAsync(0)); } catch { }
            ScrollToBottomRequested?.Invoke(true); // короткая анимация к низу (как в Telegram), а не прыжок
        }
        else ScrollToBottomRequested?.Invoke(true);
        MarkRead();
    }

    // ---- просмотр фото / видео
    [RelayCommand]
    private Task OpenMedia(MessageViewModel? m)
    {
        if (m is null) return Task.CompletedTask;
        // Листаем всё медиа чата (или вкладки «Медиа» в профиле, если открыли оттуда).
        _viewerList = SharedItems.Contains(m) ? SharedItems.Where(v => v.IsVisualMedia).ToList() : ChatVisualMedia();
        if (!_viewerList.Contains(m)) _viewerList = [m];
        if (!SharedItems.Contains(m) && !IsThread) _ = ExtendViewerListAsync();
        return ShowInViewer(m);
    }

    private List<MessageViewModel> _viewerList = [];

    /// <summary>Все фото/видео/GIF ленты по порядку (части альбомов — по отдельности).</summary>
    private List<MessageViewModel> ChatVisualMedia() =>
        Items.OfType<MessageViewModel>()
            .SelectMany(v => v.IsAlbum ? v.Album.AsEnumerable() : [v])
            .Where(v => v.IsVisualMedia)
            .ToList();

    public string ViewerCounter => ViewerMessage is { } m && _viewerList.Count > 1 ? $"{_viewerList.IndexOf(m) + 1} из {_viewerList.Count}" : "";
    public bool ViewerHasPrev => ViewerMessage is { } m && _viewerList.IndexOf(m) > 0;
    public bool ViewerHasNext => ViewerMessage is { } m && _viewerList.IndexOf(m) is var i && i >= 0 && i < _viewerList.Count - 1;

    [RelayCommand]
    private Task ViewerPrev() => ViewerMessage is { } m && _viewerList.IndexOf(m) is > 0 and var i ? ShowInViewer(_viewerList[i - 1]) : Task.CompletedTask;

    [RelayCommand]
    private Task ViewerNext() => ViewerMessage is { } m && _viewerList.IndexOf(m) is var i && i >= 0 && i < _viewerList.Count - 1
        ? ShowInViewer(_viewerList[i + 1]) : Task.CompletedTask;

    private async Task ShowInViewer(MessageViewModel m)
    {
        if (m.Item.Media is not (MediaKind.Photo or MediaKind.Video or MediaKind.Gif))
        {
            if (m.CanDownload) await Download(m);
            return;
        }
        AudioPlayer.Instance.Stop();
        ViewerMessage = m;
        m.EnsurePreview(_tg.Media); // элемент из полного списка чата мог ещё не иметь превью
        OnPropertyChanged(nameof(ViewerCounter));
        OnPropertyChanged(nameof(ViewerHasPrev));
        OnPropertyChanged(nameof(ViewerHasNext));
        ViewerImage = m.Preview;
        ViewerVideoFailed = false;
        ViewerVideoPath = null;
        ViewerLoading = true;
        ViewerProgress = 0;
        try
        {
            if (m.Item.Media is MediaKind.Photo)
            {
                var bytes = await _tg.Media.GetFullPhotoAsync(m.Item);
                if (ViewerMessage == m) ViewerImage = (bytes is null ? null : await Task.Run(() => Images.FromBytes(bytes))) ?? m.Preview;
            }
            else
            {
                var path = await _tg.Media.GetMediaFileAsync(m.Item, new Progress<double>(p => ViewerProgress = p));
                if (ViewerMessage == m) ViewerVideoPath = path;
            }
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
        finally { if (ViewerMessage == m) ViewerLoading = false; }
        PrefetchNeighbours(m);
    }

    // Соседние фото подгружаем заранее в фоне — переход стрелками мгновенный.
    private void PrefetchNeighbours(MessageViewModel m)
    {
        var i = _viewerList.IndexOf(m);
        if (i < 0) return;
        foreach (var n in new[] { i + 1, i - 1, i + 2 })
            if (n >= 0 && n < _viewerList.Count && _viewerList[n].Item.Media is MediaKind.Photo)
                _ = _tg.Media.GetFullPhotoAsync(_viewerList[n].Item, priority: false);
    }

    [RelayCommand]
    private void CloseViewer()
    {
        _viewerListCts?.Cancel();
        ViewerMessage = null;
        ViewerImage = null;
        ViewerVideoPath = null;
    }

    [RelayCommand]
    private Task SaveViewer() => ViewerMessage is { } m ? Download(m) : Task.CompletedTask;

    /// <summary>Видео, которое не открылось встроенным плеером, — в системном (там есть все установленные кодеки).</summary>
    [RelayCommand]
    private void OpenViewerExternally()
    {
        if (ViewerVideoPath is not { } path || !System.IO.File.Exists(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    // Список для листания — всё фото/видео чата с сервера, а не только уже загруженное в ленту
    // (как в Telegram: «5 из 120», стрелки идут дальше загруженного). Догружается в фоне после открытия.
    private CancellationTokenSource? _viewerListCts;

    private async Task ExtendViewerListAsync()
    {
        _viewerListCts?.Cancel();
        var cts = _viewerListCts = new CancellationTokenSource();
        try
        {
            var all = new List<MessageItem>();
            var offset = 0;
            for (var page = 0; page < 30; page++) // до 3000 фото/видео
            {
                var items = await _tg.SearchMediaAsync(Peer, SharedMediaKind.Media, offset, 100);
                if (cts.IsCancellationRequested) return;
                if (items.Count == 0) break;
                all.AddRange(items);
                offset = items[^1].Id;
                if (items.Count < 100) break;
            }
            if (cts.IsCancellationRequested || ViewerMessage is null || all.Count == 0) return;
            // Уже открытые в ленте модели переиспользуем (у них загружены превью), недостающие создаём.
            var known = _viewerList.GroupBy(v => v.Item.Id).ToDictionary(g => g.Key, g => g.First());
            var list = all.Where(i => i.Media is MediaKind.Photo or MediaKind.Video or MediaKind.Gif)
                .Select(i =>
                {
                    if (known.Remove(i.Id, out var vm)) return vm;
                    var created = new MessageViewModel(i, Peer);
                    return created;
                })
                .Concat(known.Values) // что есть в ленте, но поиск не вернул (GIF и т.п.)
                .OrderBy(v => v.Item.Id)
                .ToList();
            if (!list.Contains(ViewerMessage)) return;
            _viewerList = list;
            OnPropertyChanged(nameof(ViewerCounter));
            OnPropertyChanged(nameof(ViewerHasPrev));
            OnPropertyChanged(nameof(ViewerHasNext));
        }
        catch (Exception ex) { TgErrors.Describe(ex); }
    }
}
