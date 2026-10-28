using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Комментарии к посту канала (обсуждение в привязанной группе) — боковая панель в чате.</summary>
public sealed partial class CommentsViewModel : ObservableObject
{
    private readonly TelegramService _tg;
    private readonly PeerRef _channel;
    private readonly Action _close;
    private readonly Action<string, bool> _toast;
    private PeerRef? _discussion;
    private int _topId;

    public CommentsViewModel(TelegramService tg, PeerRef channel, MessageViewModel post, Action close, Action<string, bool> toast)
    {
        _tg = tg;
        _channel = channel;
        Post = post;
        _close = close;
        _toast = toast;
    }

    public MessageViewModel Post { get; }
    public ObservableCollection<MessageViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _draft = "";
    public string Title => Items.Count == 0 ? "Комментарии" : $"Комментарии · {Items.Count}";
    public bool IsEmpty => !IsLoading && Error is null && Items.Count == 0;

    /// <summary>View: прокрутить к последнему комментарию.</summary>
    public event Action? ScrollToBottomRequested;

    public async Task LoadAsync()
    {
        try
        {
            var (discussion, topId, comments) = await _tg.GetCommentsAsync(_channel, Post.Id);
            _discussion = discussion;
            _topId = topId;
            foreach (var m in comments) Items.Add(Create(m));
            ScrollToBottomRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Error = TgErrors.Describe(ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private MessageViewModel Create(MessageItem m)
    {
        var vm = new MessageViewModel(m, _discussion ?? _channel);
        vm.EnsurePreview(_tg.Media);
        vm.EnsureSenderAvatar(_tg.Media);
        return vm;
    }

    private bool CanSend() => _discussion is not null && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        if (_discussion is null) return;
        var text = Draft.Trim();
        Draft = "";
        try
        {
            var sent = await _tg.SendCommentAsync(_discussion, _topId, text);
            Items.Add(Create(sent));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsEmpty));
            ScrollToBottomRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Draft = text;
            _toast(TgErrors.Describe(ex), true);
        }
    }

    [RelayCommand]
    private void Close() => _close();
}
