using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Config;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

public enum Section { Chats, Profile }

/// <summary>Главное окно после входа: навигация (чаты / мой профиль / чужие профили), поиск профилей, обновления.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly TelegramService _tg;
    private readonly Action _onLoggedOut;
    private readonly Stack<object> _back = new();
    private readonly UpdateService? _updates;
    private UpdateInfo? _update;
    private CancellationTokenSource? _toastCts;

    public ShellViewModel(TelegramService tg, AppEnv env, Action onLoggedOut)
    {
        _tg = tg;
        _onLoggedOut = onLoggedOut;
        Picker = new MediaPickerViewModel(tg, env.DataDirectory);
        Chats = new ChatsViewModel(tg, this);
        MyProfile = new ProfileViewModel(tg, null, LogoutAsync, this);
        Controls.AppServices.Media = tg.Media;
        Controls.AppServices.Tg = tg;
        tg.Media.TrimMediaCache();
        if (env.UpdateRepo is { } repo) _updates = new UpdateService(repo);
        _currentPage = Chats;
    }

    public ChatsViewModel Chats { get; }
    public MediaPickerViewModel Picker { get; }
    public ProfileViewModel MyProfile { get; }
    public SettingsViewModel Settings { get; } = new();
    [ObservableProperty] private bool _isSettingsOpen;

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    public string VersionText => "v" + BuildInfo.VersionText;

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private Section _section = Section.Chats;
    [ObservableProperty] private string _profileQuery = "";
    [ObservableProperty] private bool _isResolving;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _toastIsError;

    [ObservableProperty] private string? _updateText;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _updateProgress;

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

    /// <summary>Островок должен обновить список чатов.</summary>
    public event Action? IslandChanged;
    /// <summary>Пришло входящее (островок может «подмигнуть»).</summary>
    public event Action<ChatItemViewModel>? Incoming;

    public void NotifyIslandChanged() => IslandChanged?.Invoke();
    public void OnIncoming(ChatItemViewModel chat) => Incoming?.Invoke(chat);

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
        if (IsSettingsOpen) { IsSettingsOpen = false; return true; }
        if (CurrentPage is ChatsViewModel chats) return chats.HandleEscape();
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
        Chats.UnreadChanged += () => OnPropertyChanged(nameof(ChatsUnread));
        Settings.StoriesSettingChanged += Chats.OnStoriesSettingChanged;
        _ = MyProfile.LoadAsync();
        _ = ReactionCatalog.LoadAsync(_tg);
        // Обновления — сразу при старте (не дожидаясь загрузки всех чатов) и затем каждые 30 минут.
        _ = CheckUpdatesAsync();
        _updateTimer.Tick += (_, _) => { if (_update is null) _ = CheckUpdatesAsync(); };
        _updateTimer.Start();
        await Chats.EnsureLoadedAsync();
        OnPropertyChanged(nameof(ChatsUnread));
    }

    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };

    partial void OnSectionChanged(Section value)
    {
        _back.Clear();
        OnPropertyChanged(nameof(CanGoBack));
        CurrentPage = value == Section.Chats ? Chats : MyProfile;
    }

    // ===================================================================== Навигация

    public void OpenProfile(PeerRef peer)
    {
        if (peer.IsSelf)
        {
            Section = Section.Profile;
            CurrentPage = MyProfile;
            return;
        }
        if (_tg.IsIgnored(peer))
        {
            ShowToast("Пользователь не найден", true);
            return;
        }
        var vm = new ProfileViewModel(_tg, peer, LogoutAsync, this);
        _back.Push(CurrentPage);
        CurrentPage = vm;
        OnPropertyChanged(nameof(CanGoBack));
        _ = vm.LoadAsync();
    }

    /// <summary>Ссылка t.me/… или tg://… — открыть чат внутри приложения, остальное — в браузере.</summary>
    public async Task OpenLinkAsync(string link)
    {
        if (!link.Contains("t.me/", StringComparison.OrdinalIgnoreCase) && !link.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
        {
            Helpers.Shell.Open(link);
            return;
        }
        try
        {
            var peer = await _tg.ResolveAsync(link);
            if (peer is null) Helpers.Shell.Open(link);
            else OpenChat(peer);
        }
        catch { Helpers.Shell.Open(link); }
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

    private async Task CheckUpdatesAsync()
    {
        if (_updates is null) return;
        _update = await _updates.CheckAsync();
        if (_update is not null) UpdateText = $"Доступна версия {_update.Version.ToString(3)}";
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_updates is null || _update is null || IsUpdating) return;
        IsUpdating = true;
        try
        {
            UpdateText = "Скачивание обновления…";
            await _updates.PrepareAsync(_update, new Progress<double>(p =>
            {
                UpdateProgress = p;
                UpdateText = $"Скачивание обновления… {p:P0}";
            }));
            UpdateText = "Перезапуск…";
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateText = $"Доступна версия {_update.Version.ToString(3)}";
            ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateText = null;

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
        Chats.Dispose();
    }
}
