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

    partial void OnIsEnabledChanged(bool value) => GhostMode.Enabled = value;
    partial void OnHideReadChanged(bool value) => GhostMode.HideRead = value;
    partial void OnHideTypingChanged(bool value) => GhostMode.HideTyping = value;
    partial void OnHideListenedChanged(bool value) => GhostMode.HideListened = value;
    partial void OnHideStoryViewsChanged(bool value) => GhostMode.HideStoryViews = value;

    [RelayCommand]
    private void Toggle() => IsEnabled = !IsEnabled;
}
