using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Режим призрака: ползунок над списком чатов, Ctrl+G и раздел настроек — одно и то же состояние.</summary>
public sealed partial class GhostViewModel : ObservableObject
{
    public static GhostViewModel Instance { get; } = new();

    [ObservableProperty] private bool _isEnabled = GhostMode.Enabled;
    [ObservableProperty] private bool _hideRead = GhostMode.HideRead;
    [ObservableProperty] private bool _hideTyping = GhostMode.HideTyping;
    [ObservableProperty] private bool _hideListened = GhostMode.HideListened;
    [ObservableProperty] private bool _hideStoryViews = GhostMode.HideStoryViews;
    [ObservableProperty] private bool _delaySends = GhostMode.DelaySends;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HideNoticeNextTime))] private bool _showDelayNotice = GhostMode.ShowDelayNotice;
    [ObservableProperty] private bool _isNoticeOpen;

    public string DelayCaption => $"Сообщения уходят через {GhostMode.MessageDelay.TotalSeconds:0} секунд, файлы и видео — через {GhostMode.FileDelay.TotalSeconds:0}";
    public string NoticeText => $"Все сообщения будут отправляться отложенно: текст, стикеры и голосовые — через {GhostMode.MessageDelay.TotalSeconds:0} секунд, файлы и видео — через {GhostMode.FileDelay.TotalSeconds:0} секунд.";

    /// <summary>Галочка в окне: обратная сторона настройки «показывать окно».</summary>
    public bool HideNoticeNextTime
    {
        get => !ShowDelayNotice;
        set => ShowDelayNotice = !value;
    }

    partial void OnDelaySendsChanged(bool value) => GhostMode.DelaySends = value;
    partial void OnShowDelayNoticeChanged(bool value) => GhostMode.ShowDelayNotice = value;
    partial void OnHideReadChanged(bool value) => GhostMode.HideRead = value;
    partial void OnHideTypingChanged(bool value) => GhostMode.HideTyping = value;
    partial void OnHideListenedChanged(bool value) => GhostMode.HideListened = value;
    partial void OnHideStoryViewsChanged(bool value) => GhostMode.HideStoryViews = value;

    partial void OnIsEnabledChanged(bool value)
    {
        GhostMode.Enabled = value;
        IsNoticeOpen = value && DelaySends && ShowDelayNotice;
    }

    [RelayCommand]
    private void Toggle() => IsEnabled = !IsEnabled;

    [RelayCommand]
    private void CloseNotice() => IsNoticeOpen = false;
}
