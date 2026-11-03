using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Config;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

public enum Section { Chats, Profile, Music }

/// <summary>Главное окно после входа: навигация (чаты / мой профиль / чужие профили), поиск профилей, обновления.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly TelegramService _tg;
    private readonly Action _onLoggedOut;
    private readonly Stack<object> _back = new();
    private readonly UpdateService? _updates;
    private bool _updateBusy;
    private CancellationTokenSource? _toastCts;

    public ShellViewModel(TelegramService tg, AppEnv env, Action onLoggedOut, Func<string?, Task>? changeProxy = null)
    {
        _tg = tg;
        _onLoggedOut = onLoggedOut;
        Proxy = new ProxyChoiceViewModel(env, changeProxy ?? (_ => Task.CompletedTask));
        Picker = new MediaPickerViewModel(tg, env.DataDirectory);
        Chats = new ChatsViewModel(tg, this);
        MyProfile = new ProfileViewModel(tg, null, LogoutAsync, this);
        Music = MusicViewModel.Instance = new MusicViewModel(tg, (text, error) => ShowToast(text, error));
        Controls.AppServices.Media = tg.Media;
        Controls.AppServices.Tg = tg;
        _ = Task.Run(() => tg.Media.TrimMediaCache()); // обход папки кэша — не в потоке интерфейса
        if (env.UpdateRepo is { } repo) _updates = new UpdateService(repo);
        tg.ConnectionChanged += OnConnectionChanged;
        tg.ReactionReceived += OnReactionReceived;
        // Выбранный способ не проходил — связь поднялась другим (встроенный прокси / напрямую): сказать, как подключены.
        tg.RouteChanged += route => Helpers.Anim.Ui(() => ShowToast($"Связь восстановлена: {route}"));
        _currentPage = Chats;
        MusicNow.Instance.PropertyChanged += OnMusicNowChanged;
    }

    public ChatsViewModel Chats { get; }
    public MediaPickerViewModel Picker { get; }
    public ProfileViewModel MyProfile { get; }
    /// <summary>Раздел «Музыка» (главная грузится при первом открытии).</summary>
    public MusicViewModel Music { get; }
    public SettingsViewModel Settings { get; } = new();
    private OnlinePresence? _presence;
    [ObservableProperty] private bool _isSettingsOpen;

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    /// <summary>Клик по своему профилю в настройках — закрыть настройки и открыть профиль.</summary>
    [RelayCommand]
    private void OpenMyProfileFromSettings()
    {
        IsSettingsOpen = false;
        Section = Section.Profile;
    }

    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (value) _ = Settings.RefreshCacheSizeAsync();
    }

    public string VersionText => "v" + BuildInfo.VersionText;

    /// <summary>Значок соединения над настройками (щит — через прокси, «антенна» — напрямую) и окно смены прокси.</summary>
    public ProxyChoiceViewModel Proxy { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMusicPanel))] private object _currentPage;

    /// <summary>Боковой плеер: песня играет, а раздел «Музыка» (там свой плеер) закрыт.</summary>
    public bool ShowMusicPanel => MusicNow.Instance.ShowMusicBar && !ReferenceEquals(CurrentPage, Music);

    private void OnMusicNowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MusicNow.ShowMusicBar)) return;
        OnPropertyChanged(nameof(ShowMusicPanel));
        if (ShowMusicPanel) Music.Activate(); // подборки и история в панели
    }
    [ObservableProperty] private Section _section = Section.Chats;
    [ObservableProperty] private string _profileQuery = "";
    [ObservableProperty] private bool _isResolving;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _toastIsError;

    /// <summary>«Соединение…» — пока чаты показаны из кэша, а связь с Telegram ещё устанавливается.</summary>
    [ObservableProperty] private string? _connectionStatus;

    // Обрыв связи: в шапке списка «Соединение…»; связь вернулась — открытый чат догружает пропущенное (список сверится сам).
    private void OnConnectionChanged(bool connected) => Anim.Ui(() =>
    {
        ConnectionStatus = connected ? null : "Соединение…";
        if (connected) _ = Chats.Conversation?.LoadAsync();
    });


    public bool CanGoBack => _back.Count > 0;

    // ===================================================================== Истории

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsStoriesOpen))] private StoriesViewModel? _storyViewer;
    public bool IsStoriesOpen => StoryViewer is not null;

    public void OpenStories(IReadOnlyList<StoryPeerViewModel> peers, int index)
    {
        if (index < 0) return;
        var vm = new StoriesViewModel(_tg, peers, index, () => { StoryViewer = null; Chats.OnStoriesRead(); }, OpenProfile, ShowToast);
        StoryViewer = vm;
        vm.Start();
    }

    // ===================================================================== «Островок» и мини-чат (когда окно свёрнуто)

    /// <summary>Чат, открытый в мини-окне.</summary>
    [ObservableProperty] private ConversationViewModel? _miniChat;

    private bool _mainOnScreen = true;

    /// <summary>Главное окно видно (не свёрнуто и не убрано в трей). Открытый в нём чат читается только тогда.</summary>
    public bool MainOnScreen
    {
        get => _mainOnScreen;
        set
        {
            if (_mainOnScreen == value) return;
            _mainOnScreen = value;
            if (value) Chats.Conversation?.MarkRead(); // вернулись в окно — пришедшее за это время теперь видно
        }
    }

    /// <summary>Чат сейчас открыт на экране (в главном окне или в мини-окне / быстром ответе).</summary>
    public bool IsOnScreen(long peerKey) =>
        (MainOnScreen && CurrentPage == Chats && Chats.Conversation?.Peer.Key == peerKey) || MiniChat?.Peer.Key == peerKey;

    // Вернулись из профиля к чатам — открытый чат снова на экране.
    partial void OnCurrentPageChanged(object value)
    {
        if (value == Chats) Chats.Conversation?.MarkRead();
    }

    /// <summary>Островок должен обновить список чатов.</summary>
    public event Action? IslandChanged;
    /// <summary>Пришло входящее (островок может «подмигнуть»).</summary>
    public event Action<ChatItemViewModel>? Incoming;

    public void NotifyIslandChanged() => IslandChanged?.Invoke();
    public void OnIncoming(ChatItemViewModel chat) => Incoming?.Invoke(chat);

    /// <summary>На ваше сообщение поставили реакцию (чат и текст уведомления: «Имя: ❤ на ваше сообщение»).</summary>
    public event Action<ChatItemViewModel, string>? ReactionIncoming;

    private void OnReactionReceived(PeerRef chat, int msgId, PeerRef who, string emoji) => Helpers.Anim.Ui(() =>
    {
        // Сердечко в списке и в чате: реакции копятся, пока их не просмотрят (и в чатах без звука).
        if (Chats.FindChat(chat.Key) is { } row)
        {
            row.UnreadReactions++;
            if (Chats.Conversation is { } conv && conv.Peer.Key == chat.Key) conv.ResetReactionQueue();
        }
        if (Chats.Chats.FirstOrDefault(c => c.Key == chat.Key) is not { IsMuted: false } item) return;
        // Что за сообщение — если оно уже есть в открытом чате.
        var quote = Chats.Conversation is { } open && open.Peer.Key == chat.Key ? open.MessageText(msgId) : null;
        var what = string.IsNullOrWhiteSpace(quote) ? "на ваше сообщение" : $"на «{(quote.Length > 60 ? quote[..60] + "…" : quote)}»";
        ReactionIncoming?.Invoke(item, chat.IsUser ? $"{emoji} {what}" : $"{who.DisplayTitle}: {emoji} {what}");
    });

    public IReadOnlyList<ChatItemViewModel> IslandChats() => Chats.RecentOutgoing(4);

    public ConversationViewModel OpenMiniChat(PeerRef peer)
    {
        var conv = Chats.GetConversation(peer);
        MiniChat = conv;
        _ = conv.LoadAsync();
        conv.MarkRead();
        return conv;
    }

    // ===================================================================== Esc

    /// <summary>Закрыть верхний слой интерфейса. false — закрывать нечего.</summary>
    public bool HandleEscape()
    {
        if (StoryViewer is { } stories) { stories.Close(); return true; }
        if (Settings.IsClearDialogOpen) { Settings.CloseClearDialogCommand.Execute(null); return true; }
        if (Settings.IsSessionConfirmOpen) { Settings.CancelSessionConfirmCommand.Execute(null); return true; }
        if (IsSettingsOpen && Settings.IsSessionsOpen) { Settings.IsSessionsOpen = false; return true; }
        if (IsSettingsOpen) { IsSettingsOpen = false; return true; }
        if (Proxy.IsOpen) { Proxy.IsOpen = false; return true; }
        if (CurrentPage is ChatsViewModel chats) return chats.HandleEscape();
        if (CurrentPage is MusicViewModel music)
        {
            if (music.IsChatsPage) { if (music.ChatsQuery.Length > 0) music.ChatsQuery = ""; else music.IsChatsPage = false; return true; }
            if (music.IsSearch) { music.Query = ""; return true; }
            Section = Section.Chats;
            return true;
        }
        if (CurrentPage is ProfileViewModel profile)
        {
            if (profile.HandleEscape()) return true;
            if (CanGoBack) { GoBack(); return true; }
            if (profile == MyProfile) { Section = Section.Chats; return true; }
        }
        return false;
    }
    public int ChatsUnread => Chats.Chats.Where(c => !c.IsArchived && c.HasUnread && !c.IsMuted).Count();

    public async Task StartAsync()
    {
        _ = LoadTelegramEmojiAsync();
        Chats.UnreadChanged += () => OnPropertyChanged(nameof(ChatsUnread));
        Settings.StoriesSettingChanged += Chats.OnStoriesSettingChanged;
        Settings.GetCacheSize = _tg.GetCacheSizeAsync;
        // Оставляем 5 последних личных чатов (с людьми), а также стикеры и эмодзи — это решает ядро.
        Settings.ClearCacheAction = (progress, alsoKeep) => _tg.ClearCacheAsync(Chats.Chats.Where(c => c.IsPersonal && !c.Peer.IsSelf)
            .OrderByDescending(c => c.SortDate).Take(5).Select(c => c.Peer).ToList(), progress, alsoKeep);
        Settings.GetCleanupSticker = _tg.GetCleanupStickerAsync;
        Settings.GetVaultChats = _tg.GetVaultChatsAsync;
        Settings.ClearVaultAction = async peers =>
        {
            await _tg.ClearVaultAsync(peers);
            Chats.OnVaultCleared(peers);
        };
        Settings.GetSessions = _tg.GetSessionsAsync;
        Settings.TerminateSessionAction = _tg.TerminateSessionAsync;
        Settings.TerminateOthersAction = _tg.TerminateOtherSessionsAsync;
        _presence ??= new OnlinePresence(_tg, MyProfile.ShowOwnPresence); // «в сети», пока главное окно открыто и с ним работают
        // Ссылки Telegram (в тексте сообщений, описаниях, кнопках) — открываются внутри приложения.
        Helpers.RichText.MentionExists = name => _tg.UsernameExistsAsync(name); // @имя в сообщениях — ссылка, если такое есть
        Helpers.RichText.OpenMention = name => _ = OpenMentionAsync(name);
        MessageViewModel.LoadReplyStory = _tg.GetStoryAsync; // ответы на истории — миниатюра истории над текстом
        Helpers.Shell.InAppLinks = url =>
        {
            if (!TelegramService.IsTelegramLink(url)) return false;
            _ = OpenLinkAsync(url);
            return true;
        };
        // Профиль (с подарками — это десятки запросов) — после основного списка чатов: при первом входе не отнимает у него сеть.
        _ = Chats.MainLoaded.ContinueWith(_ => MyProfile.LoadAsync(), TaskScheduler.FromCurrentSynchronizationContext());
        _ = ReactionCatalog.LoadAsync(_tg);
        // Обновления — сразу при старте (не дожидаясь загрузки всех чатов) и затем каждые 30 минут.
        _ = CheckUpdatesAsync();
        _updateTimer.Tick += (_, _) => _ = CheckUpdatesAsync();
        _updateTimer.Start();
        await Chats.EnsureLoadedAsync();
        Helpers.VlcRuntime.Prewarm(); // видеоплеер — заранее, в фоне: первое видео откроется без распаковки движка
        // Движок мини-приложений (WebView2) заранее не запускаем: это несколько процессов на ~150–200 МБ, а нужен он редко.
        OnPropertyChanged(nameof(ChatsUnread));
    }

    // Эмодзи в оформлении Telegram: из кэша — сразу, с сервера — следом (раз за запуск).
    private async Task LoadTelegramEmojiAsync()
    {
        if (await Task.Run(() => _tg.GetCachedTelegramEmoji()) is { } cached) Helpers.EmojiImages.SetTelegramSet(cached);
        if (await Task.Run(() => _tg.GetTelegramEmojiAsync()) is { } fresh) Helpers.EmojiImages.SetTelegramSet(fresh);
    }

    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };

    partial void OnSectionChanged(Section value)
    {
        _back.Clear();
        OnPropertyChanged(nameof(CanGoBack));
        CurrentPage = value switch { Section.Chats => Chats, Section.Music => Music, _ => MyProfile };
        if (value == Section.Music) Music.Activate();
    }

    // ===================================================================== Навигация

    /// <summary>
    /// Кнопка раздела. Нажатие на уже выбранный раздел тоже работает: открытый поверх чужой профиль закрывается,
    /// а в чатах — сбрасывается поиск и снова виден список.
    /// </summary>
    [RelayCommand]
    private void OpenSection(Section section)
    {
        if (Section != section)
        {
            Section = section;
            return;
        }
        _back.Clear();
        OnPropertyChanged(nameof(CanGoBack));
        CurrentPage = section switch { Section.Chats => Chats, Section.Music => Music, _ => MyProfile };
        if (section == Section.Chats) Chats.Search = "";
    }

    public void OpenProfile(PeerRef peer) => OpenProfile(peer, null);

    // @имя в тексте сообщения — профиль этого человека, бота, канала или группы.
    private async Task OpenMentionAsync(string username)
    {
        try { OpenProfile(await _tg.ResolveAsync("@" + username)); }
        catch (Exception ex) { ShowToast(TgErrors.Describe(ex), true); }
    }

    /// <param name="seed">Уже загруженный профиль этого же человека (боковая панель) — открыть сразу с его данными.</param>
    public void OpenProfile(PeerRef peer, ProfileViewModel? seed)
    {
        if (peer.IsSelf)
        {
            Section = Section.Profile;
            CurrentPage = MyProfile;
            return;
        }
        var vm = new ProfileViewModel(_tg, peer, LogoutAsync, this);
        if (seed?.Profile?.Peer?.Key == peer.Key) vm.SeedFrom(seed);
        if (CurrentPage is ProfileViewModel previous) previous.Suspend(); // ушёл назад — подарки отпускаем
        _back.Push(CurrentPage);
        // Назад — не глубже 5 страниц: дальние профили освобождаются (раньше все открытые подряд висели в памяти).
        if (_back.Count > 5)
        {
            var keep = _back.Take(5).Reverse().ToList();
            _back.Clear();
            foreach (var page in keep) _back.Push(page);
        }
        CurrentPage = vm;
        OnPropertyChanged(nameof(CanGoBack));
        _ = vm.LoadAsync();
    }

    /// <summary>Ссылка t.me/… или tg://… — открыть чат внутри приложения, остальное — в браузере.</summary>
    /// <summary>
    /// Ссылка Telegram — внутри приложения, как в официальном клиенте: чат, канал, бот, сообщение (с переходом к нему),
    /// приглашение (с вопросом «Вступить?»), набор стикеров. Остальные ссылки и то, что открыть нельзя, — в браузере.
    /// </summary>
    public async Task OpenLinkAsync(string link)
    {
        if (!TelegramService.IsTelegramLink(link))
        {
            Helpers.Shell.OpenExternal(link);
            return;
        }
        try
        {
            switch (await _tg.ParseLinkAsync(link))
            {
                case { Kind: TgLinkKind.Peer, Peer: { } peer } l:
                    OpenChat(peer);
                    if (l.MessageId is { } id) await JumpWhenOpenAsync(peer, id);
                    break;
                case { Kind: TgLinkKind.Invite, InviteHash: { } hash } inv:
                    var what = inv.InviteIsChannel ? "канал" : "группу";
                    Chats.Confirm = new ConfirmRequest($"Вступить в {what} «{inv.InviteTitle}»?",
                        $"{Fmt.N(inv.InviteMembers)} {Fmt.Plural(inv.InviteMembers, "участник", "участника", "участников")}", "Вступить", false, async () =>
                        {
                            if (await _tg.JoinInviteAsync(hash) is { } joined) OpenChat(joined);
                        });
                    if (Section != Section.Chats) Section = Section.Chats;
                    break;
                case { Kind: TgLinkKind.StickerSet, StickerPack: { } pack }:
                    if (Chats.Conversation is { } conv) conv.ShowStickerPack(pack.Info, pack.Stickers, pack.Installed);
                    else Helpers.Shell.OpenExternal(link);
                    break;
                default:
                    Helpers.Shell.OpenExternal(link);
                    break;
            }
        }
        catch (PeerNotFoundException ex)
        {
            ShowToast(ex.Message, true);
        }
        catch (Exception ex)
        {
            Logging.Write("open link: " + ex.Message);
            Helpers.Shell.OpenExternal(link);
        }
    }

    // Ссылка на сообщение: чат только что открылся — переходим к сообщению, когда лента загрузилась.
    private async Task JumpWhenOpenAsync(PeerRef peer, int messageId)
    {
        for (var i = 0; i < 40; i++)
        {
            if (Chats.Conversation is { Peer: var p, IsLoading: false } conv && p.Key == peer.Key)
            {
                await conv.JumpToMessageAsync(messageId);
                return;
            }
            await Task.Delay(100);
        }
    }

    public void OpenChat(PeerRef peer)
    {
        _back.Clear();
        OnPropertyChanged(nameof(CanGoBack));
        if (Section != Section.Chats) Section = Section.Chats;
        CurrentPage = Chats;
        Chats.OpenChat(peer);
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_back.Count == 0) return;
        CurrentPage = _back.Pop();
        if (CurrentPage is ProfileViewModel profile) profile.ResumePage();
        OnPropertyChanged(nameof(CanGoBack));
    }

    [RelayCommand]
    private void ShowChats() => Section = Section.Chats;

    [RelayCommand]
    private void ShowMyProfile()
    {
        if (Section == Section.Profile) CurrentPage = MyProfile;
        Section = Section.Profile;
    }

    /// <summary>Открыть профиль по @username, ссылке или ID.</summary>
    [RelayCommand]
    private async Task ResolveProfile()
    {
        var q = ProfileQuery.Trim();
        if (q.Length == 0 || IsResolving) return;
        IsResolving = true;
        try
        {
            var peer = await _tg.ResolveAsync(q);
            ProfileQuery = "";
            OpenProfile(peer);
        }
        catch (Exception ex)
        {
            ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            IsResolving = false;
        }
    }

    private async Task LogoutAsync()
    {
        Dispose();
        await _tg.LogoutAsync();
        _onLoggedOut();
    }

    // ===================================================================== Обновления

    // Два пути к одной версии: она сразу скачивается в фоне и встаёт на место exe, а плашка предлагает перезапуститься сейчас.
    // Не нажали — программа перезапустится сама, когда этого не будет видно.
    private async Task CheckUpdatesAsync()
    {
        if (_updates is null || _updateBusy || _updateInstalled) return;
        _updateBusy = true;
        try
        {
            if (await _updates.CheckAsync() is not { } update)
            {
                if (IsUpdating) StopUpdating(null);
                return;
            }
            var version = update.Version.ToString(3);
            if (!_updateDismissed && !IsUpdating) UpdateText = $"Доступна версия {version}";
            _updateVersion = version;
            await _updates.InstallAsync(update);
            Logging.Write($"update: версия {version} установлена, заработает после перезапуска");
            _updateInstalled = true;
            _updateTimer.Stop(); // пока работает старый код, следующая версия подождёт: патч к ней строится от новой
            UpdateInstalled?.Invoke();
            if (IsUpdating) RestartRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Logging.Write("update: не установилось, попробуем позже: " + ex.Message);
            if (IsUpdating) StopUpdating(TgErrors.Describe(ex));
        }
        finally { _updateBusy = false; }
    }

    private void StopUpdating(string? error)
    {
        IsUpdating = false;
        UpdateText = _updateVersion is { } version ? $"Доступна версия {version}" : null;
        if (error is not null) ShowToast(error, true);
    }

    /// <summary>Кнопка на плашке: готово — перезапуск сразу; ещё качается — перезапуск по окончании; не вышло — ещё попытка.</summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (IsUpdating) return;
        if (_updateInstalled)
        {
            RestartRequested?.Invoke();
            return;
        }
        IsUpdating = true;
        UpdateText = "Скачивание обновления…";
        await CheckUpdatesAsync(); // уже качается — дождётся та загрузка: она проверит IsUpdating
    }

    /// <summary>Крестик прячет только плашку: обновление всё равно поставится само.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        _updateDismissed = true;
        UpdateText = null;
    }

    [ObservableProperty] private string? _updateText;
    [ObservableProperty] private bool _isUpdating;
    private bool _updateInstalled;
    private bool _updateDismissed;
    private string? _updateVersion;

    /// <summary>Новая версия уже на диске; главное окно ищет момент для незаметного перезапуска.</summary>
    public event Action? UpdateInstalled;
    /// <summary>Нажали «Обновить», и новая версия готова: перезапуститься сразу.</summary>
    public event Action? RestartRequested;

    /// <summary>Перезапуск сейчас что-то потерял бы: черновик, отправка, сохранение файла, открытое мини-окно.</summary>
    public bool HasUnfinishedWork => MiniChat is not null || Chats.HasUnfinishedWork || ConversationViewModel.ActiveTransfers > 0;

    // ===================================================================== Уведомления

    public async void ShowToast(string text, bool isError = false)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        ToastIsError = isError;
        Toast = text;
        try
        {
            await Task.Delay(isError ? 6000 : 3000, cts.Token);
            Toast = null;
        }
        catch (TaskCanceledException) { }
    }

    [RelayCommand]
    private void HideToast() => Toast = null;

    public void Dispose()
    {
        _updateTimer.Stop();
        MusicNow.Instance.PropertyChanged -= OnMusicNowChanged;
        _presence?.Dispose();
        _tg.ConnectionChanged -= OnConnectionChanged;
        Chats.Dispose();
    }
}
