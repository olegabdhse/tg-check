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
}
