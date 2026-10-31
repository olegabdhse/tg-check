using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;
using TgCheck.Core.Media;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.Views;

/// <summary>Фото и видео чата в отдельном окне: листание, видео, сохранение. Не зависит от основного окна.</summary>
public sealed partial class MediaWindowViewModel(MediaService media, IReadOnlyList<MessageViewModel> list) : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsVideo), nameof(Counter), nameof(HasPrev), nameof(HasNext))] private MessageViewModel? _current;
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private string? _videoPath;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _videoFailed;
    [ObservableProperty] private double _progress;

    public bool IsVideo => Current?.Item.Media is MediaKind.Video or MediaKind.Gif;
    private int Index => Current is { } m ? IndexOf(m) : -1;
    public string Counter => list.Count > 1 && Index >= 0 ? $"{Index + 1} из {list.Count}" : "";
    public bool HasPrev => Index > 0;
    public bool HasNext => Index >= 0 && Index < list.Count - 1;

    private int IndexOf(MessageViewModel m)
    {
        for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], m)) return i;
        return -1;
    }

    public Task Prev() => HasPrev ? Show(list[Index - 1]) : Task.CompletedTask;
    public Task Next() => HasNext ? Show(list[Index + 1]) : Task.CompletedTask;

    public async Task Show(MessageViewModel m)
    {
        Current = m;
        m.EnsurePreview(media);
        Image = m.Preview;
        VideoFailed = false;
        VideoPath = null;
        IsLoading = true;
        Progress = 0;
        try
        {
            if (m.Item.Media is MediaKind.Photo)
            {
                var bytes = await media.GetFullPhotoAsync(m.Item);
                if (Current == m) Image = (bytes is null ? null : await Task.Run(() => Images.FromBytes(bytes))) ?? m.Preview;
            }
            else
            {
                var path = await media.GetMediaFileAsync(m.Item, new Progress<double>(p => Progress = p));
                if (Current == m) VideoPath = path;
            }
        }
        catch (Exception ex) { Logging.Write("media window: " + TgErrors.Describe(ex)); }
        finally { if (Current == m) IsLoading = false; }
        // Соседние фото — заранее, листание мгновенное.
        var i = Index;
        foreach (var n in new[] { i + 1, i - 1 })
            if (n >= 0 && n < list.Count && list[n].Item.Media is MediaKind.Photo)
                _ = media.GetFullPhotoAsync(list[n].Item, priority: false);
    }
}

public partial class MediaWindow : Window
{
    // Своя причина паузы музыки: просмотр в чате и это окно могут быть открыты одновременно.
    private const string MusicReason = "media-window";

    private readonly MediaWindowViewModel _vm;
    private readonly Func<MessageViewModel, Task> _save;
    private readonly DispatcherTimer _videoTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _videoPlaying;

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new LightWindowAutomationPeer(this);

    public MediaWindow(string title, MediaService media, IReadOnlyList<MessageViewModel> list, MessageViewModel current, Func<MessageViewModel, Task> save)
    {
        InitializeComponent();
        Title = title;
        _save = save;
        _vm = new MediaWindowViewModel(media, list);
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MediaWindowViewModel.VideoPath)) OnVideoPath(); };
        _videoTimer.Tick += (_, _) => UpdateVideoTime();
        SourceInitialized += (_, _) => WindowEffects.ApplyDarkTitleBar(this, Color.FromRgb(0x05, 0x09, 0x0E));
        Loaded += (_, _) => _ = _vm.Show(current);
        Closed += (_, _) =>
        {
            _videoTimer.Stop();
            VideoPlayer.Stop();
            MusicNow.Instance.Resume(MusicReason);
        };
        PreviewKeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: _ = _vm.Prev(); break;
            case Key.Right: _ = _vm.Next(); break;
            case Key.Space when _vm.IsVideo: PlayPause_Click(this, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnVideoPath()
    {
        if (_vm.VideoPath is null)
        {
            VideoPlayer.Stop();
            _videoTimer.Stop();
            _videoPlaying = false;
            MusicNow.Instance.Resume(MusicReason);
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            VideoPlayer.Play();
            SetPlaying(true);
        });
    }

    private void SetPlaying(bool playing)
    {
        _videoPlaying = playing;
        PlayPauseButton.Content = playing ? "" : "";
        if (playing) _videoTimer.Start();
    }

    private void VideoPlayer_MediaOpened(object? sender, EventArgs e)
    {
        AudioPlayer.Instance.Stop();
        MusicNow.Instance.Interrupt(MusicReason);
        VideoPlayer.Play();
        SetPlaying(true);
    }

    private void VideoPlayer_MediaFailed(object? sender, string error)
    {
        _videoTimer.Stop();
        _videoPlaying = false;
        Logging.Write($"media window video failed {_vm.VideoPath}: {error}");
        _vm.VideoFailed = true;
    }

    private void VideoPlayer_MediaEnded(object? sender, EventArgs e)
    {
        if (_vm.Current?.IsGif == true) { VideoPlayer.Play(); return; } // GIF — по кругу
        VideoPlayer.Pause();
        SetPlaying(false);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPlaying) VideoPlayer.Pause();
        else VideoPlayer.Play();
        SetPlaying(!_videoPlaying);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        VideoPlayer.IsMuted = !VideoPlayer.IsMuted;
        MuteButton.Content = VideoPlayer.IsMuted ? "" : "";
    }

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (VideoPlayer.Duration is { } duration)
            VideoPlayer.Position = TimeSpan.FromSeconds(SeekSlider.Value * duration.TotalSeconds);
    }

    private void UpdateVideoTime()
    {
        if (VideoPlayer.Duration is not { } duration) return;
        var total = duration.TotalSeconds;
        var pos = VideoPlayer.Position.TotalSeconds;
        if (!SeekSlider.IsMouseCaptureWithin) SeekSlider.Value = total > 0 ? pos / total : 0;
        VideoTime.Text = $"{Fmt.Duration(pos)} / {Fmt.Duration(total)}";
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => _ = _vm.Prev();
    private void Next_Click(object sender, RoutedEventArgs e) => _ = _vm.Next();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Current is { } m) _ = _save(m);
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        TopmostButton.Foreground = Topmost ? (Brush)FindResource("AccentBrush") : Brushes.White;
    }

    private void OpenExternally_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.VideoPath is not { } path || !System.IO.File.Exists(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Logging.Write("media window: " + ex.Message); }
    }
}
