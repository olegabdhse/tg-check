using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

public enum ChatTab { All, Personal }

/// <summary>Список чатов: «Все», «Личные», скрытый архив, поиск, истории; открытый чат справа.</summary>
public sealed partial class ChatsViewModel : ObservableObject, IDisposable
{
    private readonly TelegramService _tg;
    private readonly Dictionary<long, ChatItemViewModel> _byKey = [];
    private readonly Dictionary<long, ConversationViewModel> _conversations = [];
    private readonly Dictionary<long, CancellationTokenSource> _typingTimers = [];
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _reloadCts;
    private bool _loaded;

    public ChatsViewModel(TelegramService tg, ShellViewModel shell)
    {
        _tg = tg;
        Shell = shell;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Chats);
        view.Filter = Filter;
        view.CustomSort = new ChatComparer();
        ChatsView = view;

        _tg.MessageReceived += OnMessageReceivedEvent;
        _tg.MessageEdited += OnMessageEdited;
        _tg.ReactionsUpdated += OnReactionsUpdated;
        _tg.MessagesDeleted += OnMessagesDeleted;
        _tg.HistoryRead += OnHistoryRead;
        _syncTimer.Tick += async (_, _) => await SyncRecentAsync();
        _tg.UserTyping += OnUserTyping;
        _tg.UserStatusChanged += OnUserStatus;
        _tg.PollUpdated += OnPollUpdated;

        _presenceTimer.Tick += (_, _) => _ = RefreshPresenceAsync();
        _presenceTimer.Start();
    }

    // Статусы «в сети»: Telegram шлёт живые обновления не для всех — периодически сверяем сами.
    private readonly System.Windows.Threading.DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromSeconds(45) };

    private Task RefreshPresenceAsync()
    {
        var peers = Chats.Where(c => c.IsPersonal && !c.IsArchived).OrderByDescending(c => c.SortDate).Take(80).Select(c => c.Peer).ToList();
        if (Conversation?.Peer is { IsUser: true } open) peers.Insert(0, open);
        return _tg.RefreshPresenceAsync(peers);
    }

    public ShellViewModel Shell { get; }
    public Core.Media.MediaService Media => _tg.Media;

    public ObservableCollection<ChatItemViewModel> Chats { get; } = [];
    public ICollectionView ChatsView { get; }
    public ObservableCollection<PeerResultViewModel> GlobalResults { get; } = [];
    public ObservableCollection<StoryPeerViewModel> Stories { get; } = [];

    [ObservableProperty] private ChatTab _tab = ChatTab.All;
    [ObservableProperty] private bool _showArchive;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSearchingGlobal;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private ChatItemViewModel? _selectedChat;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasConversation))] private ConversationViewModel? _conversation;
    [ObservableProperty] private ConfirmRequest? _confirm;
    [ObservableProperty] private bool _isWorking;
    /// <summary>Узкое окно: список и чат показываются по очереди.</summary>
    [ObservableProperty] private bool _isCompact;

    public bool HasConversation => Conversation is not null;
    /// <summary>Полоса историй над чатами (можно скрыть в настройках).</summary>
    public bool HasStories => Stories.Count > 0 && UiSettings.ShowStories;
    public void OnStoriesSettingChanged() => OnPropertyChanged(nameof(HasStories));
    public int ArchiveCount => Chats.Count(c => c.IsArchived);
    public int ArchiveUnread => Chats.Count(c => c.IsArchived && c.HasUnread && !c.IsMuted);
    /// <summary>Непрочитанные чаты для бейджей на вкладках «Все» и «Личные».</summary>
    public int AllUnread => Chats.Count(c => !c.IsArchived && c.HasUnread && !c.IsMuted);
    public int PersonalUnread => Chats.Count(c => c.IsPersonal && c.HasUnread && !c.IsMuted);
    public bool HasArchive => ArchiveCount > 0;
    public bool IsListEmpty => !IsLoading && ChatsView.IsEmpty && GlobalResults.Count == 0;

    /// <summary>Изменилось число непрочитанных (для значка на боковой панели).</summary>
    public event Action? UnreadChanged;

    partial void OnTabChanged(ChatTab value) => RefreshView();
    partial void OnShowArchiveChanged(bool value) => RefreshView();
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsListEmpty));

    partial void OnSearchChanged(string value)
    {
        RefreshView();
        _ = SearchGlobalAsync(value);
    }

    partial void OnSelectedChatChanged(ChatItemViewModel? value)
    {
        if (value is null) return;
        var conv = GetConversation(value.Peer, value.ReadOutboxMaxId);
        if (Conversation == conv) return;
        Conversation = conv;
        conv.UpdateStatus();
        _ = conv.LoadAsync();
    }

    /// <summary>Модель чата (общая для главного окна и мини-окна).</summary>
    public ConversationViewModel GetConversation(PeerRef peer, int readOutboxMaxId = 0)
    {
        if (_conversations.TryGetValue(peer.Key, out var conv)) return conv;
        conv = new ConversationViewModel(_tg, this, peer, readOutboxMaxId);
        conv.EnsureAvatar(_tg.Media);
        _conversations[peer.Key] = conv;
        // Держим в памяти несколько последних чатов — быстрое переключение.
        if (_conversations.Count > 10)
            _conversations.Remove(_conversations.Keys.First(k => k != peer.Key && k != Conversation?.Peer.Key));
        return conv;
    }

    public ConversationViewModel? FindConversation(long key) => _conversations.GetValueOrDefault(key);

    // Открытая ветка комментариев (одна): получает свои новые сообщения отдельно от чата обсуждения.
    private ConversationViewModel? _thread;

    public void OpenThread(ConversationViewModel thread)
    {
        thread.EnsureAvatar(_tg.Media);
        _thread = thread;
        Conversation = thread;
        thread.UpdateStatus();
        _ = thread.LoadAsync();
    }

    // Из ветки «назад» — к каналу, а не к списку.
    private bool LeaveThread()
    {
        if (Conversation is not { ThreadParent: { } parent }) return false;
        Conversation = parent;
        _thread = null;
        return true;
    }

    public void CloseChat()
    {
        if (Conversation?.HandleEscape() == true) return;
        if (LeaveThread()) return;
        Conversation = null;
        SelectedChat = null;
    }

    /// <summary>Esc: сначала слои внутри чата, затем сам чат, поиск, архив. false — нечего закрывать.</summary>
    public bool HandleEscape()
    {
        if (Confirm is not null) { CancelConfirm(); return true; }
        if (Conversation?.HandleEscape() == true) return true;
        if (LeaveThread()) return true;
        if (Conversation is not null) { Conversation = null; SelectedChat = null; return true; }
        if (Search.Length > 0) { Search = ""; return true; }
        if (ShowArchive) { ShowArchive = false; return true; }
        return false;
    }

    private void RefreshView()
    {
        ChatsView.Refresh();
        OnPropertyChanged(nameof(IsListEmpty));
    }

    private bool Filter(object o)
    {
        if (o is not ChatItemViewModel c) return false;
        if (Search.Trim() is { Length: > 0 } q)
        {
            // Поиск по своим чатам (включая архив): по имени и по нику, с @ и без.
            var nick = q.TrimStart('@');
            return c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                   (nick.Length > 0 && c.Peer.Username?.Contains(nick, StringComparison.OrdinalIgnoreCase) == true);
        }
        if (ShowArchive) return c.IsArchived;
        // «Личные» — все личные переписки, включая архивные; «Все» — основная папка.
        return Tab == ChatTab.Personal ? c.IsPersonal : !c.IsArchived;
    }

    private sealed class ChatComparer : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ChatItemViewModel a || y is not ChatItemViewModel b) return 0;
            if (a.IsPinned != b.IsPinned) return a.IsPinned ? -1 : 1;
            if (a.IsPinned) return a.Order.CompareTo(b.Order);
            return b.SortDate.CompareTo(a.SortDate);
        }
    }

    // ===================================================================== Загрузка

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        Error = null;
        // 1) Мгновенно — из локального кэша.
        if (Chats.Count == 0)
        {
            var (cachedMain, cachedArchive) = await Task.Run(() => (_tg.GetCachedDialogs(archived: false), _tg.GetCachedDialogs(archived: true)));
            if (cachedMain is not null) Merge(cachedMain, false, removeMissing: false);
            if (cachedArchive is not null) Merge(cachedArchive, true, removeMissing: false);
            NotifyArchive();
            RefreshView();
        }
        IsLoading = Chats.Count == 0;

        // 2) Сеть: основная папка быстро, затем архив (он бывает огромным).
        try
        {
            Merge(await _tg.GetDialogsAsync(archived: false), false, removeMissing: true);
            IsLoading = false;
            NotifyArchive();
            RefreshView();
            _ = LoadStoriesAsync();
            _syncTimer.Start();
            _ = RefreshPresenceAsync();

            Merge(await _tg.GetDialogsAsync(archived: true), true, removeMissing: true);
            NotifyArchive();
            RefreshView();
            Shell.NotifyIslandChanged();
        }
        catch (Exception ex)
        {
            if (Chats.Count == 0) Error = TgErrors.Describe(ex);
            else ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Раз в минуту сверяем верх списка с сервером: в больших группах Telegram присылает не все обновления,
    // и без сверки счётчики непрочитанных и последние сообщения расходятся с официальным клиентом.
    private readonly System.Windows.Threading.DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(60) };

    private async Task SyncRecentAsync()
    {
        try { Merge(await _tg.GetRecentDialogsAsync(), false, removeMissing: false); NotifyArchive(); }
        catch (Exception ex) { Logging.Write("sync dialogs: " + ex.Message); }
    }

    // Обновляем существующие строки, добавляем новые, убираем пропавшие — без мерцания списка.
    private void Merge(IReadOnlyList<ChatSummary> dialogs, bool archived, bool removeMissing)
    {
        // Мало изменений — двигаем только изменившиеся строки (без перерисовки всего списка).
        if (dialogs.Count(d => !_byKey.ContainsKey(d.Peer.Key)) <= 30)
        {
            foreach (var chat in MergeCore(dialogs, archived, removeMissing)) Reposition(chat);
            return;
        }
        // Массовое добавление — без сортировки, одна пересортировка в конце (архив бывает огромным).
        var view = (ListCollectionView)ChatsView;
        var sort = view.CustomSort;
        view.CustomSort = null;
        try
        {
            MergeCore(dialogs, archived, removeMissing);
        }
        finally
        {
            view.CustomSort = sort;
        }
    }

    private static readonly ChatComparer Comparer = new();

    /// <summary>
    /// Поставить один чат на своё место (новое сообщение — вверх, под закреплённые). Если позиция не изменилась,
    /// ничего не делаем; иначе переставляем только эту строку — список не мерцает.
    /// </summary>
    private void Reposition(ChatItemViewModel chat)
    {
        var view = (ListCollectionView)ChatsView;
        var index = view.IndexOf(chat);
        var visible = Filter(chat);
        if (index < 0 || !visible)
        {
            if ((index < 0) == visible) MoveItem(chat); // фильтр изменился: показать или скрыть строку
            return;
        }
        var okBefore = index == 0 || Comparer.Compare(view.GetItemAt(index - 1), chat) <= 0;
        var okAfter = index == view.Count - 1 || Comparer.Compare(chat, view.GetItemAt(index + 1)) <= 0;
        if (!okBefore || !okAfter) MoveItem(chat);
    }

    // Удалить и добавить заново: представление вставит строку сразу в нужное место (и применит фильтр).
    private void MoveItem(ChatItemViewModel chat)
    {
        var selected = ReferenceEquals(SelectedChat, chat);
        Chats.Remove(chat);
        Chats.Add(chat);
        if (selected) SelectedChat = chat;
        OnPropertyChanged(nameof(IsListEmpty));
    }

    private List<ChatItemViewModel> MergeCore(IReadOnlyList<ChatSummary> dialogs, bool archived, bool removeMissing)
    {
        var touched = new List<ChatItemViewModel>();
        var seen = new HashSet<long>();
        var order = 0;
        foreach (var d in dialogs)
        {
            if (!seen.Add(d.Peer.Key)) continue;
            if (_byKey.TryGetValue(d.Peer.Key, out var existing))
            {
                existing.Update(d);
                existing.Order = order++;
                touched.Add(existing);
                continue;
            }
            var vm = new ChatItemViewModel(d, order++);
            if (d.Peer.IsUser) vm.IsOnline = _tg.GetPresence(d.Peer).Presence == Presence.Online;
            _byKey[d.Peer.Key] = vm;
            Chats.Add(vm);
        }
        if (!removeMissing) return touched;
        foreach (var gone in Chats.Where(c => c.IsArchived == archived && !seen.Contains(c.Key) && c != SelectedChat).ToList())
        {
            Chats.Remove(gone);
            _byKey.Remove(gone.Key);
        }
        return touched;
    }

    private async Task LoadStoriesAsync()
    {
        try
        {
            var stories = await _tg.GetStoriesAsync();
            Stories.Clear();
            foreach (var s in stories)
            {
                var vm = new StoryPeerViewModel(s);
                vm.EnsureAvatar(_tg.Media);
                Stories.Add(vm);
                if (_byKey.TryGetValue(s.Peer.Key, out var chat))
                {
                    chat.HasStories = true;
                    chat.StoriesUnread = s.HasUnread;
                }
            }
            OnPropertyChanged(nameof(HasStories));
        }
        catch (Exception ex) { TgErrors.Describe(ex); }
    }

    [RelayCommand]
    private void OpenStories(StoryPeerViewModel? peer)
    {
        if (peer is null) return;
        Shell.OpenStories(Stories.ToList(), Stories.IndexOf(peer));
    }

    /// <summary>Открыть истории пира из его аватара в списке чатов.</summary>
    public void OpenStoriesOf(PeerRef peer)
    {
        var index = Stories.ToList().FindIndex(s => s.Peer.Key == peer.Key);
        if (index >= 0) Shell.OpenStories(Stories.ToList(), index);
    }

    public void OnStoriesRead()
    {
        foreach (var s in Stories)
            if (_byKey.TryGetValue(s.Peer.Key, out var chat)) chat.StoriesUnread = s.StoriesUnread;
    }

    /// <summary>Пришло сообщение в чат, которого нет в списке, — перечитать диалоги (с задержкой).</summary>
    private async void ReloadSoon()
    {
        _reloadCts?.Cancel();
        var cts = _reloadCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(1500, cts.Token);
            Merge(await _tg.GetDialogsAsync(archived: false), false, removeMissing: false);
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { TgErrors.Describe(ex); }
    }

    private void NotifyArchive()
    {
        UnreadChanged?.Invoke();
        OnPropertyChanged(nameof(ArchiveCount));
        OnPropertyChanged(nameof(ArchiveUnread));
        OnPropertyChanged(nameof(AllUnread));
        OnPropertyChanged(nameof(PersonalUnread));
        OnPropertyChanged(nameof(HasArchive));
    }

    private async Task SearchGlobalAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        GlobalResults.Clear();
        var q = query.Trim();
        if (q.Length < 2) { OnPropertyChanged(nameof(IsListEmpty)); return; }
        try
        {
            await Task.Delay(350, cts.Token);
            IsSearchingGlobal = true;
            var found = new List<PeerRef>();

            // @ник, ссылка t.me/… или ID — открываем точное совпадение первым.
            if (q.StartsWith('@') || q.Contains("t.me/", StringComparison.OrdinalIgnoreCase) ||
                q.StartsWith("tg://", StringComparison.OrdinalIgnoreCase) || (q.Length >= 5 && q.All(char.IsDigit)))
            {
                try { found.Add(await _tg.ResolveAsync(q)); }
                catch (Exception ex) when (ex is PeerNotFoundException or TL.RpcException) { }
            }
            // Люди и каналы по имени и нику (Telegram ищет и по контактам, и глобально).
            if (q.TrimStart('@').Length >= 2) found.AddRange(await _tg.SearchPeersAsync(q.TrimStart('@')));
            if (cts.IsCancellationRequested) return;

            foreach (var p in found.DistinctBy(p => p.Key).Where(p => !_byKey.TryGetValue(p.Key, out var c) || !ChatsView.Contains(c)))
            {
                var vm = new PeerResultViewModel(p);
                vm.EnsureAvatar(_tg.Media);
                GlobalResults.Add(vm);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { TgErrors.Describe(ex); }
        finally
        {
            if (!cts.IsCancellationRequested) IsSearchingGlobal = false;
            OnPropertyChanged(nameof(IsListEmpty));
        }
    }

    /// <summary>Чаты для «островка»: где вы писали последними.</summary>
    public IReadOnlyList<ChatItemViewModel> RecentOutgoing(int count) =>
        Chats.Where(c => c.LastOutgoingUtc is not null && !c.Peer.IsSelf)
            .OrderByDescending(c => c.LastOutgoingUtc)
            .Concat(Chats.Where(c => !c.IsArchived && !c.Peer.IsSelf).OrderByDescending(c => c.SortDate))
            .Distinct().Take(count).ToList();

    // ===================================================================== Команды

    [RelayCommand]
    private void ToggleArchive()
    {
        Search = "";
        ShowArchive = !ShowArchive;
    }

    [RelayCommand]
    private void OpenGlobal(PeerResultViewModel? r)
    {
        if (r is null) return;
        Search = "";
        OpenChat(r.Peer);
    }

    /// <summary>Закрепить / открепить чат (как в Telegram: закреплённые — сверху, в своём порядке).</summary>
    [RelayCommand]
    private async Task TogglePin(ChatItemViewModel? c)
    {
        if (c is null) return;
        var pin = !c.IsPinned;
        try
        {
            await _tg.SetPinnedAsync(c.Peer, pin);
            c.IsPinned = pin;
            // Новый закреп — первым среди закреплённых.
            if (pin) c.Order = Chats.Where(x => x.IsPinned && x != c).Select(x => x.Order).DefaultIfEmpty(0).Min() - 1;
            Reposition(c);
            ShowToast(pin ? "Чат закреплён" : "Чат откреплён");
        }
        catch (Exception ex) { ShowToast(TgErrors.Describe(ex), true); }
    }

    /// <summary>Выключить / включить уведомления чата.</summary>
    [RelayCommand]
    private async Task ToggleMute(ChatItemViewModel? c)
    {
        if (c is null) return;
        var mute = !c.IsMuted;
        try
        {
            await _tg.SetMutedAsync(c.Peer, mute);
            c.IsMuted = mute;
            NotifyArchive();
            ShowToast(mute ? "Уведомления выключены" : "Уведомления включены");
        }
        catch (Exception ex) { ShowToast(TgErrors.Describe(ex), true); }
    }

    [RelayCommand]
    private void OpenProfile(ChatItemViewModel? c)
    {
        if (c is not null) Shell.OpenProfile(c.Peer);
    }

    /// <summary>Открыть чат с пиром (создаёт строку, если чата ещё нет).</summary>
    public void OpenChat(PeerRef peer)
    {
        if (!_byKey.TryGetValue(peer.Key, out var item))
        {
            item = new ChatItemViewModel(new ChatSummary { Peer = peer }, int.MaxValue);
            _byKey[peer.Key] = item;
            Chats.Add(item);
        }
        // Личный чат (даже архивный) показываем во вкладке «Личные», в архив уходим только для остальных.
        if (item.IsPersonal && (item.IsArchived || Tab == ChatTab.Personal))
        {
            ShowArchive = false;
            Tab = ChatTab.Personal;
        }
        else if (item.IsArchived && !ShowArchive) ShowArchive = true;
        Reposition(item);
        SelectedChat = item;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        if (!IsWorking) Confirm = null;
    }

    [RelayCommand]
    private async Task AcceptConfirm()
    {
        if (Confirm is not { } request || IsWorking) return;
        IsWorking = true;
        try
        {
            await request.Action();
        }
        catch (Exception ex)
        {
            ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            Confirm = null;
            IsWorking = false;
        }
    }

    public void ShowToast(string text, bool isError = false) => Shell.ShowToast(text, isError);

    // ===================================================================== События Telegram

    private void OnMessageReceivedEvent(MessageItem m) => Anim.Ui(() => OnMessageReceived(m, fromConversation: false));

    public void OnMessageReceived(MessageItem m, bool fromConversation)
    {
        if (_thread?.BelongsToThread(m) == true)
        {
            _thread.OnMessageReceived(m);
            if (!_byKey.ContainsKey(m.PeerKey)) return; // группа обсуждения может не быть в списке чатов
        }
        if (!_byKey.TryGetValue(m.PeerKey, out var chat))
        {
            ReloadSoon();
            return;
        }
        if (chat.Last is null || m.Id >= chat.Last.Id) chat.Last = m;
        chat.TypingText = null;
        var isOpen = Conversation?.Peer.Key == m.PeerKey || Shell.MiniChat?.Peer.Key == m.PeerKey;
        if (!m.IsOut && !isOpen && m.ServiceText is null) chat.Unread++;
        if (!fromConversation && _conversations.TryGetValue(m.PeerKey, out var conv)) conv.OnMessageReceived(m);
        Reposition(chat);
        NotifyArchive();
        if (m.IsOut) Shell.NotifyIslandChanged();
        if (!m.IsOut && !chat.IsMuted) Shell.OnIncoming(chat);
    }

    private void OnMessageEdited(MessageItem m) => Anim.Ui(() =>
    {
        if (_byKey.TryGetValue(m.PeerKey, out var chat) && chat.Last?.Id == m.Id) chat.Last = m;
        if (_conversations.TryGetValue(m.PeerKey, out var conv)) conv.OnMessageEdited(m);
        if (_thread?.Peer.Key == m.PeerKey) _thread.OnMessageEdited(m);
    });

    private void OnReactionsUpdated(long peerKey, int id, IReadOnlyList<ReactionInfo> reactions) => Anim.Ui(() =>
    {
        if (_conversations.TryGetValue(peerKey, out var conv)) conv.OnReactions(id, reactions);
        if (_thread?.Peer.Key == peerKey) _thread.OnReactions(id, reactions);
    });

    private void OnPollUpdated(long pollId, PollInfo poll) => Anim.Ui(() =>
    {
        foreach (var conv in _conversations.Values) conv.OnPollUpdated(pollId, poll);
    });

    private void OnMessagesDeleted(long? peerKey, int[] ids) => Anim.Ui(() =>
    {
        foreach (var conv in _conversations.Values.Where(c => peerKey is null ? (c.Peer.IsUser || c.Peer.IsBasicGroup) : c.Peer.Key == peerKey))
            conv.OnMessagesDeleted(ids);
    });

    public void OnLocalDeleted(long peerKey)
    {
        if (_byKey.TryGetValue(peerKey, out var chat) && _conversations.TryGetValue(peerKey, out var conv))
            chat.Last = conv.Items.OfType<MessageViewModel>().LastOrDefault()?.Item;
    }

    private void OnHistoryRead(long peerKey, int maxId, bool outbox, int stillUnread) => Anim.Ui(() =>
    {
        if (!_byKey.TryGetValue(peerKey, out var chat)) return;
        if (outbox)
        {
            chat.ReadOutboxMaxId = Math.Max(chat.ReadOutboxMaxId, maxId);
            if (_conversations.TryGetValue(peerKey, out var conv)) conv.OnOutboxRead(maxId);
        }
        else if (stillUnread >= 0)
        {
            // Сервер сообщил точный остаток (прочитали на другом устройстве не до конца) — так и показываем.
            chat.Unread = stillUnread;
            if (stillUnread == 0) chat.IsMarkedUnread = false;
            NotifyArchive();
        }
        else if (chat.Last is null || maxId >= chat.Last.Id)
        {
            chat.Unread = 0;
            chat.IsMarkedUnread = false;
            NotifyArchive();
        }
    });

    public void OnChatRead(long peerKey)
    {
        if (!_byKey.TryGetValue(peerKey, out var chat)) return;
        chat.Unread = 0;
        chat.IsMarkedUnread = false;
        NotifyArchive();
    }

    private void OnUserTyping(long peerKey, PeerRef who) => Anim.Ui(async () =>
    {
        if (_conversations.TryGetValue(peerKey, out var conv)) conv.OnTyping(who);
        if (!_byKey.TryGetValue(peerKey, out var chat)) return;
        if (_typingTimers.Remove(peerKey, out var old)) old.Cancel();
        var cts = _typingTimers[peerKey] = new CancellationTokenSource();
        chat.TypingText = chat.Peer.IsUser ? "печатает" : $"{who.DisplayTitle} печатает";
        try
        {
            await Task.Delay(5500, cts.Token);
            chat.TypingText = null;
        }
        catch (TaskCanceledException) { }
    });

    private void OnUserStatus(long userId, Presence presence, DateTime? seen) => Anim.Ui(() =>
    {
        if (_byKey.TryGetValue(userId, out var chat)) chat.IsOnline = presence == Presence.Online;
        if (_conversations.TryGetValue(userId, out var conv)) conv.UpdateStatus();
    });

    public void Dispose()
    {
        _presenceTimer.Stop();
        _syncTimer.Stop();
        _tg.MessageReceived -= OnMessageReceivedEvent;
        _tg.MessageEdited -= OnMessageEdited;
        _tg.ReactionsUpdated -= OnReactionsUpdated;
        _tg.MessagesDeleted -= OnMessagesDeleted;
        _tg.HistoryRead -= OnHistoryRead;
        _tg.UserTyping -= OnUserTyping;
        _tg.UserStatusChanged -= OnUserStatus;
        _tg.PollUpdated -= OnPollUpdated;
    }
}
