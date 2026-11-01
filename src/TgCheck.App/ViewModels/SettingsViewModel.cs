using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Models;

namespace TgCheck.App.ViewModels;

/// <summary>Настройки приложения (сохраняются сразу, в ui.json).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private bool _showIsland = UiSettings.ShowIsland;
    [ObservableProperty] private bool _showIslandMusic = UiSettings.ShowIslandMusic;
    [ObservableProperty] private bool _notifications = UiSettings.Notifications;
    [ObservableProperty] private bool _notificationSound = UiSettings.NotificationSound;
    [ObservableProperty] private bool _notifyArchived = UiSettings.NotifyArchived;
    [ObservableProperty] private bool _showStories = UiSettings.ShowStories;

    /// <summary>Изменилась настройка историй — список чатов показывает или прячет полосу.</summary>
    public event Action? StoriesSettingChanged;

    partial void OnShowStoriesChanged(bool value)
    {
        UiSettings.ShowStories = value;
        StoriesSettingChanged?.Invoke();
    }

    partial void OnShowIslandChanged(bool value) => UiSettings.ShowIsland = value;
    partial void OnShowIslandMusicChanged(bool value)
    {
        UiSettings.ShowIslandMusic = value;
        MusicNow.Instance.IslandMusicEnabled = value;
    }
    partial void OnNotificationsChanged(bool value) => UiSettings.Notifications = value;
    partial void OnNotificationSoundChanged(bool value)
    {
        UiSettings.NotificationSound = value;
        if (value) Notifier.PlaySound();
    }
    partial void OnNotifyArchivedChanged(bool value) => UiSettings.NotifyArchived = value;

    // ---- кэш: окно «Очистить кэш?» → утка с веником и полоска прогресса → «Готово»

    /// <summary>Размер кэша, очистка (с прогрессом и тем, что ещё не удалять) и стикер-утка — задаёт оболочка.</summary>
    public Func<Task<long>>? GetCacheSize { get; set; }
    public Func<IProgress<double>, IEnumerable<long>, Task<long>>? ClearCacheAction { get; set; }
    public Func<Task<StickerItem?>>? GetCleanupSticker { get; set; }

    public enum ClearStage { Confirm, Clearing, Done }

    [ObservableProperty] private string _cacheSizeText = "считаем…";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsConfirmStage), nameof(IsClearingStage), nameof(IsDoneStage), nameof(ClearTitle))]
    private ClearStage _stage;
    [ObservableProperty] private bool _isClearDialogOpen;
    [ObservableProperty] private double _clearProgress;
    [ObservableProperty] private string _clearResultText = "";
    [ObservableProperty] private StickerItem? _cleanupSticker;

    public bool IsConfirmStage => Stage == ClearStage.Confirm;
    public bool IsClearingStage => Stage == ClearStage.Clearing;
    public bool IsDoneStage => Stage == ClearStage.Done;
    public string ClearTitle => Stage switch
    {
        ClearStage.Clearing => "Очищаем кэш…",
        ClearStage.Done => "Готово",
        _ => "Очистить кэш?",
    };

    public async Task RefreshCacheSizeAsync()
    {
        if (GetCacheSize is null) return;
        CacheSizeText = Fmt.FileSize(await GetCacheSize());
    }

    /// <summary>Кнопка «Очистить кэш» — сначала окно с вопросом.</summary>
    [RelayCommand]
    private async Task AskClearCache()
    {
        Stage = ClearStage.Confirm;
        ClearProgress = 0;
        IsClearDialogOpen = true;
        if (CleanupSticker is null && GetCleanupSticker is not null)
            try { CleanupSticker = await GetCleanupSticker(); } catch { /* без утки */ }
    }

    [RelayCommand]
    private void CloseClearDialog()
    {
        if (Stage != ClearStage.Clearing) IsClearDialogOpen = false;
    }

    [RelayCommand]
    private async Task ClearCache()
    {
        if (ClearCacheAction is null || Stage == ClearStage.Clearing) return;
        Stage = ClearStage.Clearing;
        ClearProgress = 0;
        try
        {
            // Стикер-утку не удаляем, пока она на экране.
            var freed = await ClearCacheAction(new Progress<double>(p => ClearProgress = p),
                CleanupSticker is { } s ? [s.MediaId] : []);
            ClearProgress = 1;
            ClearResultText = $"Освобождено {Fmt.FileSize(freed)}";
        }
        catch (Exception ex) { ClearResultText = "Не удалось: " + ex.Message; }
        Stage = ClearStage.Done;
        await RefreshCacheSizeAsync();
    }

    // ---- активные сеансы (как в Telegram): этот сеанс, остальные устройства; завершить один или все, кроме этого

    public Func<Task<IReadOnlyList<SessionInfo>>>? GetSessions { get; set; }
    public Func<long, Task>? TerminateSessionAction { get; set; }
    public Func<Task>? TerminateOthersAction { get; set; }

    [ObservableProperty] private bool _isSessionsOpen;
    [ObservableProperty] private bool _isSessionsLoading;
    [ObservableProperty] private SessionItemViewModel? _currentSession;
    [ObservableProperty] private bool _isConfirmingOthers;
    [ObservableProperty] private string? _sessionsError;
    public System.Collections.ObjectModel.ObservableCollection<SessionItemViewModel> OtherSessions { get; } = [];
    public bool HasOtherSessions => OtherSessions.Count > 0;
    [ObservableProperty] private string _sessionsCountText = "";

    [RelayCommand]
    private async Task OpenSessions()
    {
        IsSessionsOpen = true;
        IsConfirmingOthers = false;
        await LoadSessionsAsync();
    }

    [RelayCommand]
    private void CloseSessions() => IsSessionsOpen = false;

    private async Task LoadSessionsAsync()
    {
        if (GetSessions is null) return;
        IsSessionsLoading = OtherSessions.Count == 0 && CurrentSession is null;
        SessionsError = null;
        try
        {
            var sessions = await Task.Run(GetSessions);
            CurrentSession = sessions.FirstOrDefault(s => s.IsCurrent) is { } cur ? new SessionItemViewModel(cur, this) : null;
            OtherSessions.Clear();
            foreach (var s in sessions.Where(s => !s.IsCurrent)) OtherSessions.Add(new SessionItemViewModel(s, this));
            SessionsCountText = Fmt.N(sessions.Count) + " " + Fmt.Plural(sessions.Count, "сеанс", "сеанса", "сеансов");
        }
        catch (Exception ex) { SessionsError = TgCheck.Core.Services.TgErrors.Describe(ex); }
        IsSessionsLoading = false;
        OnPropertyChanged(nameof(HasOtherSessions));
    }

    [RelayCommand]
    private void AskTerminateOthers() => IsConfirmingOthers = true;

    [RelayCommand]
    private void CancelTerminateOthers() => IsConfirmingOthers = false;

    [RelayCommand]
    private async Task TerminateOthers()
    {
        if (TerminateOthersAction is null) return;
        IsConfirmingOthers = false;
        try
        {
            await TerminateOthersAction();
            OtherSessions.Clear();
            OnPropertyChanged(nameof(HasOtherSessions));
        }
        catch (Exception ex) { SessionsError = TgCheck.Core.Services.TgErrors.Describe(ex); }
        await LoadSessionsAsync();
    }

    internal async Task TerminateAsync(SessionItemViewModel s)
    {
        if (TerminateSessionAction is null) return;
        try
        {
            await TerminateSessionAction(s.Info.Hash);
            OtherSessions.Remove(s);
            OnPropertyChanged(nameof(HasOtherSessions));
        }
        catch (Exception ex) { SessionsError = TgCheck.Core.Services.TgErrors.Describe(ex); }
    }
}

/// <summary>Строка сеанса: устройство, приложение, место и время; «Завершить» — с подтверждением в самой строке.</summary>
public sealed partial class SessionItemViewModel(SessionInfo info, SettingsViewModel owner) : ObservableObject
{
    public SessionInfo Info { get; } = info;
    public string Title => Info.Device.Length > 0 ? Info.Device : "Неизвестное устройство";
    public string AppText => string.Join(" ", new[] { Info.AppName, Info.AppVersion }.Where(s => s.Length > 0))
                             + (Info.IsOfficialApp ? "" : " · неофициальное");
    public string PlaceText => string.Join(" · ", new[] { Info.Ip, Info.Country }.Where(s => s.Length > 0));
    public string SystemText => string.Join(" ", new[] { Info.Platform, Info.SystemVersion }.Where(s => s.Length > 0));
    public string ActiveText => Info.IsCurrent ? "в сети" : Fmt.ChatTime(Info.ActiveUtc);
    /// <summary>Значок по системе: телефон, компьютер, браузер.</summary>
    public string Icon => (Info.Platform + " " + Info.Device + " " + Info.AppName).ToLowerInvariant() switch
    {
        var p when p.Contains("android") || p.Contains("ios") || p.Contains("iphone") || p.Contains("ipad") => "",
        var p when p.Contains("web") || p.Contains("chrome") || p.Contains("firefox") || p.Contains("safari") || p.Contains("edge") => "",
        _ => "",
    };
    public Brush IconBrush { get; } = MessageViewModel.GradientFor(info.Device, info.AppName);
    public bool IsPending => Info.IsPending;

    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand] private void AskTerminate() => IsConfirming = true;
    [RelayCommand] private void CancelTerminate() => IsConfirming = false;

    [RelayCommand]
    private async Task Terminate()
    {
        IsBusy = true;
        await owner.TerminateAsync(this);
        IsBusy = false;
        IsConfirming = false;
    }
}
