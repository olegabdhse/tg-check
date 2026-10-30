using CommunityToolkit.Mvvm.ComponentModel;
using TgCheck.App.Helpers;

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
}
