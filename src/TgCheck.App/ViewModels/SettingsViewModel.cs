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

    // ---- темы: выбор применяется сразу, без перезапуска и анимаций

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeKind.Default, "Стандартная", "#17212B", "#2B5278", "#2AABEE"),
        new(ThemeKind.Dark, "Тёмная", "#0B1016", "#16324F", "#4A9EFF"),
        new(ThemeKind.Light, "Светлая", "#E6EAEF", "#C3DEF4", "#2277BD"),
    ];

    [RelayCommand]
    private void SetTheme(ThemeOption? option)
    {
        if (option is null) return;
        AppTheme.SwitchTo(option.Kind);
        foreach (var theme in Themes) theme.RefreshCurrent();
    }

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

    public GhostViewModel Ghost => GhostViewModel.Instance;

    // ---- удалённые и правки: отдельная очистка, кэш их не трогает

    public Func<Task<IReadOnlyList<TgCheck.Core.Services.VaultChat>>>? GetVaultChats { get; set; }
    public Func<IReadOnlyCollection<long>, Task>? ClearVaultAction { get; set; }

    [ObservableProperty] private bool _isVaultDialogOpen;
    [ObservableProperty] private string _vaultStatus = "";
    public System.Collections.ObjectModel.ObservableCollection<VaultGroupViewModel> VaultGroups { get; } = [];

    [RelayCommand]
    private async Task OpenVaultDialog()
    {
        VaultGroups.Clear();
        VaultStatus = "";
        IsVaultDialogOpen = true;
        if (GetVaultChats is null) return;
        try
        {
            var chats = await GetVaultChats();
            void Add(string title, Func<TgCheck.Core.Services.VaultChat, bool> pick)
            {
                if (chats.Where(pick).ToList() is { Count: > 0 } part) VaultGroups.Add(new VaultGroupViewModel(title, part));
            }
            Add("Личные чаты", c => c.Kind is PeerKind.User or PeerKind.Bot); // боты — тоже личные переписки
            Add("Группы", c => c.Kind == PeerKind.Group);
            Add("Каналы", c => c.Kind == PeerKind.Channel);
            if (chats.Count == 0) VaultStatus = "Удалённых сообщений и правок пока нет";
        }
        catch (Exception ex) { VaultStatus = TgCheck.Core.Services.TgErrors.Describe(ex); }
    }

    [RelayCommand]
    private void CloseVaultDialog() => IsVaultDialogOpen = false;

    [RelayCommand]
    private async Task ClearVault()
    {
        var peers = VaultGroups.Where(g => g.IsChecked).SelectMany(g => g.Chats).Select(c => c.PeerKey).ToList();
        if (peers.Count == 0 || ClearVaultAction is null) return;
        try
        {
            await ClearVaultAction(peers);
            IsVaultDialogOpen = false;
        }
        catch (Exception ex) { VaultStatus = TgCheck.Core.Services.TgErrors.Describe(ex); }
    }

    // ---- активные сеансы (как в Telegram): этот сеанс, остальные устройства; завершить один или все, кроме этого

    public Func<Task<IReadOnlyList<SessionInfo>>>? GetSessions { get; set; }
    public Func<long, Task>? TerminateSessionAction { get; set; }
    public Func<Task>? TerminateOthersAction { get; set; }

    [ObservableProperty] private bool _isSessionsOpen;
    [ObservableProperty] private bool _isSessionsLoading;
    [ObservableProperty] private SessionItemViewModel? _currentSession;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSessionConfirmOpen), nameof(SessionConfirmTitle), nameof(SessionConfirmText), nameof(SessionConfirmIcon), nameof(SessionConfirmBrush))]
    private SessionItemViewModel? _confirmSession;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSessionConfirmOpen), nameof(SessionConfirmTitle), nameof(SessionConfirmText), nameof(SessionConfirmIcon), nameof(SessionConfirmBrush))]
    private bool _confirmOthers;
    [ObservableProperty] private string? _sessionsError;
    public System.Collections.ObjectModel.ObservableCollection<SessionItemViewModel> OtherSessions { get; } = [];
    public bool HasOtherSessions => OtherSessions.Count > 0;
    [ObservableProperty] private string _sessionsCountText = "";

    [RelayCommand]
    private async Task OpenSessions()
    {
        IsSessionsOpen = true;
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

    // ---- окно подтверждения (как очистка кэша): один сеанс или все, кроме этого

    public bool IsSessionConfirmOpen => ConfirmSession is not null || ConfirmOthers;
    public string SessionConfirmTitle => ConfirmOthers ? "Завершить другие сеансы?" : "Завершить сеанс?";
    public string SessionConfirmText => ConfirmOthers
        ? $"Вы выйдете из аккаунта на всех устройствах, кроме этого ({Fmt.N(OtherSessions.Count)})."
        : ConfirmSession is { } s ? $"{s.Title}\n{s.AppText}" + (s.PlaceText.Length > 0 ? "\n" + s.PlaceText : "") : "";
    public string SessionConfirmIcon => ConfirmSession?.Icon ?? "";
    public Brush SessionConfirmBrush => ConfirmSession?.IconBrush ?? SessionItemViewModel.DangerFill;
    [ObservableProperty] private bool _isTerminating;

    [RelayCommand]
    private void AskTerminateOthers() => ConfirmOthers = true;

    internal void AskTerminate(SessionItemViewModel s) => ConfirmSession = s;

    [RelayCommand]
    private void CancelSessionConfirm()
    {
        if (IsTerminating) return;
        ConfirmSession = null;
        ConfirmOthers = false;
    }

    [RelayCommand]
    private async Task ConfirmTerminate()
    {
        if (IsTerminating) return;
        IsTerminating = true;
        SessionsError = null;
        try
        {
            if (ConfirmOthers && TerminateOthersAction is not null)
            {
                await TerminateOthersAction();
                OtherSessions.Clear();
            }
            else if (ConfirmSession is { } s && TerminateSessionAction is not null)
            {
                await TerminateSessionAction(s.Info.Hash);
                OtherSessions.Remove(s);
            }
        }
        catch (Exception ex) { SessionsError = TgCheck.Core.Services.TgErrors.Describe(ex); }
        IsTerminating = false;
        ConfirmSession = null;
        ConfirmOthers = false;
        OnPropertyChanged(nameof(HasOtherSessions));
        await LoadSessionsAsync();
    }
}

/// <summary>Раздел окна очистки удалённых: галочка на весь раздел.</summary>
public sealed partial class VaultGroupViewModel(string title, IReadOnlyList<TgCheck.Core.Services.VaultChat> chats) : ObservableObject
{
    public string Title { get; } = title;
    public IReadOnlyList<TgCheck.Core.Services.VaultChat> Chats { get; } = chats;
    public string CountText => string.Join(" · ", new[]
    {
        Chats.Sum(c => c.Deleted) is > 0 and var deleted ? $"удалённых: {Fmt.N(deleted)}" : null,
        Chats.Sum(c => c.Edited) is > 0 and var edited ? $"правок: {Fmt.N(edited)}" : null,
    }.Where(s => s is not null));

    [ObservableProperty] private bool _isChecked;
}

/// <summary>Карточка темы в настройках: мини-образец (фон, сообщение, акцент) и название.</summary>
public sealed class ThemeOption(ThemeKind kind, string name, string surface, string bubble, string accent) : ObservableObject
{
    public void RefreshCurrent() => OnPropertyChanged(nameof(IsCurrent));

    public ThemeKind Kind { get; } = kind;
    public string Name { get; } = name;
    public Brush Surface { get; } = (Brush)new BrushConverter().ConvertFromString(surface)!;
    public Brush Bubble { get; } = (Brush)new BrushConverter().ConvertFromString(bubble)!;
    public Brush Accent { get; } = (Brush)new BrushConverter().ConvertFromString(accent)!;
    public bool IsCurrent => AppTheme.Current == kind;
}

/// <summary>Строка сеанса: устройство, приложение, место и время; крестик — окно «Завершить сеанс?».</summary>
public sealed partial class SessionItemViewModel(SessionInfo info, SettingsViewModel owner) : ObservableObject
{
    public SessionInfo Info { get; } = info;
    public string Title => Info.Device.Length > 0 ? Info.Device : "Неизвестное устройство";
    public string AppText => string.Join(" ", new[] { Info.AppName, Info.AppVersion }.Where(s => s.Length > 0))
                             + (Info.IsOfficialApp ? "" : " · неофициальное");
    public string PlaceText => string.Join(" · ", new[] { Info.Ip, Info.Country }.Where(s => s.Length > 0));
    public string SystemText => string.Join(" ", new[] { Info.Platform, Info.SystemVersion }.Where(s => s.Length > 0));
    public string ActiveText => Info.IsCurrent ? "в сети" : Fmt.ChatTime(Info.ActiveUtc);
    public bool IsPending => Info.IsPending;

    private enum Kind { Android, Apple, Web, Desktop }

    private Kind DeviceKind => (Info.Platform + " " + Info.Device + " " + Info.AppName + " " + Info.SystemVersion).ToLowerInvariant() switch
    {
        var p when p.Contains("android") => Kind.Android,
        var p when p.Contains("ios") || p.Contains("iphone") || p.Contains("ipad") || p.Contains("macos") || p.Contains("mac os") => Kind.Apple,
        var p when p.Contains("web") || p.Contains("chrome") || p.Contains("firefox") || p.Contains("safari") || p.Contains("edge") => Kind.Web,
        _ => Kind.Desktop,
    };

    /// <summary>Значок: телефон, браузер или компьютер.</summary>
    public string Icon => DeviceKind switch
    {
        Kind.Android or Kind.Apple when !Info.Platform.Contains("mac", StringComparison.OrdinalIgnoreCase) => "",
        Kind.Web => "",
        _ => "",
    };

    /// <summary>Однотонный круг по типу устройства, как в Telegram: Android — зелёный, Apple — серый, браузер — оранжевый, компьютер — синий.</summary>
    public Brush IconBrush => DeviceKind switch
    {
        Kind.Android => AndroidFill,
        Kind.Apple => AppleFill,
        Kind.Web => WebFill,
        _ => DesktopFill,
    };

    private static Brush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush AndroidFill = Solid(0x4C, 0xAF, 0x50);
    private static readonly Brush AppleFill = Solid(0x8E, 0x8E, 0x93);
    private static readonly Brush WebFill = Solid(0xF2, 0x99, 0x4A);
    private static readonly Brush DesktopFill = Solid(0x3E, 0x8E, 0xDE);
    internal static readonly Brush DangerFill = Solid(0xE5, 0x53, 0x4B);

    [RelayCommand] private void AskTerminate() => owner.AskTerminate(this);
}
