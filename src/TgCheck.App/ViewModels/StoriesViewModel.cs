using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

public sealed partial class StorySegment : ObservableObject
{
    [ObservableProperty] private double _progress;
}

/// <summary>Просмотр историй: по одному пиру за раз, авто-переход, клики влево/вправо.</summary>
public sealed partial class StoriesViewModel : ObservableObject
{
    private readonly TelegramService _tg;
    private readonly IReadOnlyList<StoryPeerViewModel> _peers;
    private readonly Action _close;
    private readonly Action<PeerRef> _openProfile;
    private int _peerIndex;
    private CancellationTokenSource? _timerCts;
    private readonly Action<string, bool>? _toast;

    public StoriesViewModel(TelegramService tg, IReadOnlyList<StoryPeerViewModel> peers, int startPeer, Action close, Action<PeerRef> openProfile,
        Action<string, bool>? toast = null)
    {
        _toast = toast;
        _openProfile = openProfile;
        _tg = tg;
        _peers = peers;
        _close = close;
        _peerIndex = startPeer;
    }

    public ObservableCollection<StorySegment> Segments { get; } = [];

    [ObservableProperty] private StoryPeerViewModel? _current;
    [ObservableProperty] private StoryInfo? _story;
    [ObservableProperty] private int _storyIndex;
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private string? _videoPath;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isPaused;

    public string TimeText => Story is null ? "" : TimeAgo(Story.DateUtc);
    public bool HasCaption => !string.IsNullOrWhiteSpace(Story?.Caption);

    partial void OnStoryChanged(StoryInfo? value)
    {
        Liked = value?.Liked == true;
        ReplyText = "";
        IsViewersOpen = false;
        Viewers.Clear();
        OnPropertyChanged(nameof(IsOwn));
        OnPropertyChanged(nameof(CanReply));
        OnPropertyChanged(nameof(CanLike));
        OnPropertyChanged(nameof(ViewsText));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(HasCaption));
    }

    // ===================================================================== Лайк, ответ, просмотры (как в Telegram)

    [ObservableProperty] private bool _liked;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendReplyCommand))] private string _replyText = "";
    [ObservableProperty] private bool _isViewersOpen;
    public ObservableCollection<StoryViewerViewModel> Viewers { get; } = [];

    /// <summary>Своя история: вместо ответа — «кто посмотрел».</summary>
    public bool IsOwn => Current?.Peer.IsSelf == true;
    /// <summary>Ответить можно на историю пользователя (не канала) — ответ уходит в личный чат.</summary>
    public bool CanReply => Current is { Peer: { IsUser: true, IsSelf: false } };
    public bool CanLike => Current is not null && !IsOwn;
    public string ViewsText => Story is null ? "" : Story.ReactionsCount > 0
        ? $"{Fmt.N(Story.ViewsCount)} просмотров · ❤ {Fmt.N(Story.ReactionsCount)}"
        : $"{Fmt.N(Story.ViewsCount)} {Fmt.Plural(Story.ViewsCount, "просмотр", "просмотра", "просмотров")}";

    [RelayCommand]
    private async Task ToggleLike()
    {
        if (Story is not { } story || Current is null || IsOwn) return;
        var like = !story.Liked;
        story.Liked = like;
        Liked = like;
        try { await _tg.LikeStoryAsync(Current.Peer, story.Id, like); }
        catch (Exception ex)
        {
            story.Liked = !like;
            if (Story == story) Liked = !like;
            _toast?.Invoke(TgErrors.Describe(ex), true);
        }
    }

    private bool CanSendReply() => CanReply && !string.IsNullOrWhiteSpace(ReplyText);

    [RelayCommand(CanExecute = nameof(CanSendReply))]
    private async Task SendReply()
    {
        if (Story is not { } story || Current is null) return;
        var text = ReplyText.Trim();
        ReplyText = "";
        IsPaused = false;
        try
        {
            await _tg.ReplyToStoryAsync(Current.Peer, story.Id, text);
            _toast?.Invoke("Ответ отправлен", false);
        }
        catch (Exception ex)
        {
            ReplyText = text;
            _toast?.Invoke(TgErrors.Describe(ex), true);
        }
    }

    /// <summary>Своя история: открыть список просмотревших (история ставится на паузу).</summary>
    [RelayCommand]
    private async Task ToggleViewers()
    {
        if (!IsOwn || Story is not { } story) return;
        IsViewersOpen = !IsViewersOpen;
        IsPaused = IsViewersOpen;
        if (!IsViewersOpen || Viewers.Count > 0) return;
        try
        {
            var (_, _, viewers) = await _tg.GetStoryViewersAsync(story.Id);
            if (Story != story) return;
            foreach (var v in viewers)
            {
                var item = new StoryViewerViewModel(v);
                item.EnsureAvatar(_tg.Media);
                Viewers.Add(item);
            }
        }
        catch (Exception ex) { _toast?.Invoke(TgErrors.Describe(ex), true); }
    }

    /// <summary>View сообщает прогресс видео (0..1), когда видео играет.</summary>
    public void ReportVideoProgress(double fraction)
    {
        if (StoryIndex < Segments.Count) Segments[StoryIndex].Progress = fraction;
    }

    public void Start() => ShowPeer(_peerIndex, firstUnread: true);

    private void ShowPeer(int index, bool firstUnread)
    {
        if (index < 0 || index >= _peers.Count) { Close(); return; }
        _peerIndex = index;
        Current = _peers[index];
        Current.EnsureAvatar(_tg.Media);
        Segments.Clear();
        foreach (var _ in Current.Stories.Stories) Segments.Add(new StorySegment());
        var start = firstUnread ? Current.Stories.Stories.ToList().FindIndex(s => s.Id > Current.Stories.MaxReadId) : 0;
        ShowStory(Math.Max(0, start));
    }

    private async void ShowStory(int index)
    {
        if (Current is null) return;
        var stories = Current.Stories.Stories;
        if (index >= stories.Count) { ShowPeer(_peerIndex + 1, firstUnread: true); return; }
        if (index < 0) { ShowPeer(_peerIndex - 1, firstUnread: false); return; }

        _timerCts?.Cancel();
        StoryIndex = index;
        for (var i = 0; i < Segments.Count; i++) Segments[i].Progress = i < index ? 1 : 0;
        var story = stories[index];
        Story = story;
        Image = null;
        VideoPath = null;
        IsLoading = true;

        if (story.Id > Current.Stories.MaxReadId)
        {
            Current.MarkRead(story.Id);
            _ = _tg.MarkStoriesReadAsync(Current.Peer, story.Id);
        }

        try
        {
            var preview = Images.FromBytes(await _tg.Media.GetMessagePreviewAsync(story));
            if (Story != story) return; // пока грузилось, перелистнули — не подменяем чужую историю
            Image = preview;
            if (story.IsVideo)
            {
                var path = await _tg.Media.GetMediaFileAsync(story);
                if (Story != story) return;
                VideoPath = path;
                IsLoading = false;
                return; // переход — по окончании видео (MediaEnded во view)
            }
            IsLoading = false;
            await RunPhotoTimerAsync(index);
        }
        catch (Exception ex)
        {
            if (Story != story) return;
            IsLoading = false;
            Logging.Write($"story {story.Id}: {TgErrors.Describe(ex)}");
        }
    }

    private async Task RunPhotoTimerAsync(int index)
    {
        var cts = _timerCts = new CancellationTokenSource();
        const double total = 6.0;
        var elapsed = 0.0;
        try
        {
            while (elapsed < total)
            {
                await Task.Delay(40, cts.Token);
                if (!IsPaused) elapsed += 0.04;
                Segments[index].Progress = elapsed / total;
            }
            Next();
        }
        catch (TaskCanceledException) { }
    }

    [RelayCommand]
    public void Next() => ShowStory(StoryIndex + 1);

    [RelayCommand]
    private void Previous() => ShowStory(StoryIndex - 1);

    [RelayCommand]
    public void Close()
    {
        _timerCts?.Cancel();
        AudioPlayer.Instance.Stop();
        MusicNow.Instance.Resume(MusicNow.Story);
        _close();
    }

    [RelayCommand]
    private void OpenProfile()
    {
        if (Current is null) return;
        var peer = Current.Peer;
        Close();
        _openProfile(peer);
    }

    /// <summary>Кто посмотрел историю — открыть его профиль.</summary>
    [RelayCommand]
    private void OpenViewer(StoryViewerViewModel? viewer)
    {
        if (viewer is null) return;
        Close();
        _openProfile(viewer.Peer);
    }

    private static string TimeAgo(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 1 ? "только что"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} мин назад"
            : d.TotalHours < 24 ? $"{(int)d.TotalHours} ч назад"
            : Fmt.DateShort(utc);
    }
}

/// <summary>Строка «кто посмотрел» своей истории.</summary>
public sealed class StoryViewerViewModel(StoryViewer viewer) : PeerAvatarViewModel(viewer.Peer)
{
    public string? Reaction => viewer.Reaction;
    public string TimeText => Fmt.ChatTime(viewer.DateUtc);
}
