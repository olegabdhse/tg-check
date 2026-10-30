using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;

namespace TgCheck.App.ViewModels;

/// <summary>
/// Что сейчас играет (голосовое, аудио или кружок со звуком) — для панели плеера над лентой, как в Telegram Desktop.
/// Кружок, ушедший за край экрана, показывается закреплённым сверху (через VisualBrush того же видео),
/// так что его можно смотреть дальше и листать чат.
/// </summary>
public sealed partial class NowPlaying : ObservableObject
{
    public static NowPlaying Instance { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsMusicActive), nameof(ShowTopBar), nameof(ShowMusicBar))] private string? _title;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _timeText = "";
    /// <summary>Музыка (песня), а не голосовое / кружок: для неё — отдельный плеер под списком чатов и в островке.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMusicActive), nameof(ShowTopBar), nameof(ShowMusicBar))] private bool _isMusic;
    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private Brush? _coverBrush;
    /// <summary>Громкость сейчас (0..1) — для волн вокруг обложки.</summary>
    [ObservableProperty] private double _level;
    [ObservableProperty] private bool _loop;
    public bool IsMusicActive => IsMusic && IsActive;
    /// <summary>Плеер музыки на экране (при закрытии сначала уезжает он, а песня останавливается следом).</summary>
    public bool ShowMusicBar => IsMusicActive && !_closingMusic;
    private bool _closingMusic;

    /// <summary>Закрыть плеер музыки: убрать его с экрана и только потом остановить песню.</summary>
    public async void CloseMusic()
    {
        if (_closingMusic) return;
        _closingMusic = true;
        OnPropertyChanged(nameof(ShowMusicBar));
        await Task.Delay(260);
        _closingMusic = false;
        Close();
        OnPropertyChanged(nameof(ShowMusicBar));
    }

    [RelayCommand]
    private void SeekStart() => Seek(0);

    [RelayCommand]
    private void SeekEnd() => Seek(0.999);
    /// <summary>Панель над лентой чата — для голосовых и кружков (у музыки свой плеер под чатами и в островке).</summary>
    public bool ShowTopBar => IsActive && !IsMusic;

    partial void OnLoopChanged(bool value) => AudioPlayer.Instance.Loop = value;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowFloatingRound))] private Brush? _roundBrush;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowFloatingRound))] private bool _roundOutOfView;

    public bool IsActive => Title is not null;

    /// <summary>Голосовое, которое выбрали последним (пока файл качается, могли выбрать другое).</summary>
    public string? RequestedAudio { get; set; }
    public bool ShowFloatingRound => RoundBrush is not null && RoundOutOfView;

    private Action? _toggle;
    private Action? _stop;
    private Action<double>? _seek;

    private NowPlaying()
    {
        AudioPlayer.Instance.Changed += () => Anim.Ui(SyncAudio);
        // Голосовое / кружок доиграли и больше играть нечего — панель убирается; музыка остаётся (можно включить снова).
        AudioPlayer.Instance.Ended += key => Anim.Ui(async () =>
        {
            await Task.Delay(400); // за это время чат может включить следующее голосовое
            if (_audioKey == key && !IsMusic && !AudioPlayer.Instance.IsPlaying) Close();
        });
    }

    // ---- голосовые и аудио (NAudio)
    private string? _audioKey;

    /// <summary>Песня: обложка (или градиент песни), название и исполнитель — для плеера музыки.</summary>
    public void StartMusic(string key, string title, string? performer, ImageSource? cover, Brush coverBrush)
    {
        StartAudio(key, title, performer ?? "");
        IsMusic = true;
        Cover = cover;
        CoverBrush = coverBrush;
    }

    public void StartAudio(string key, string title, string subtitle)
    {
        IsMusic = false;
        Cover = null;
        // Играл кружок со звуком — выключаем его звук (одновременно играет что-то одно).
        if (RoundBrush is not null) _stop?.Invoke();
        _audioKey = key;
        RoundBrush = null;
        Set(title, subtitle,
            toggle: () => AudioPlayer.Instance.Toggle(key, ""),
            stop: () => AudioPlayer.Instance.Stop(),
            seek: f => AudioPlayer.Instance.Seek(f));
        SyncAudio();
    }

    private void SyncAudio()
    {
        if (_audioKey is null) return;
        var player = AudioPlayer.Instance;
        if (player.Key != _audioKey)
        {
            // Плеер остановлен или переключился на другое сообщение без нас.
            if (player.Key is null) Clear();
            return;
        }
        IsPlaying = player.IsPlaying;
        Level = player.IsPlaying ? player.Level : 0;
        var total = player.Duration.TotalSeconds;
        Progress = total > 0 ? player.Position.TotalSeconds / total : 0;
        TimeText = $"{Fmt.Duration(player.Position.TotalSeconds)} / {Fmt.Duration(total)}";
    }

    // ---- кружки (MediaElement в VideoNoteView)
    public void StartRound(string title, Brush video, Action toggle, Action stop)
    {
        // Играл другой кружок со звуком — выключаем его: одновременно звучит только одно.
        if (RoundBrush is not null && !ReferenceEquals(RoundBrush, video)) _stop?.Invoke();
        RequestedAudio = null; // голосовое, которое ещё качается, после загрузки не включится
        _audioKey = null;
        if (AudioPlayer.Instance.Key is not null) AudioPlayer.Instance.Stop();
        Set(title, "Видеосообщение", toggle, stop, null);
        RoundBrush = video;
        IsPlaying = true;
    }

    public void UpdateRound(double position, double total, bool playing, bool outOfView)
    {
        IsPlaying = playing;
        Progress = total > 0 ? position / total : 0;
        TimeText = $"{Fmt.Duration(position)} / {Fmt.Duration(total)}";
        RoundOutOfView = outOfView;
    }

    /// <summary>Кружок закончился или выключен звук — убрать панель, если она про него.</summary>
    public void EndRound(Brush video)
    {
        if (ReferenceEquals(RoundBrush, video)) Clear();
    }

    private void Set(string title, string subtitle, Action toggle, Action stop, Action<double>? seek)
    {
        _toggle = toggle;
        _stop = stop;
        _seek = seek;
        Subtitle = subtitle;
        Title = title;
    }

    private void Clear()
    {
        _audioKey = null;
        _toggle = _stop = null;
        _seek = null;
        RoundBrush = null;
        RoundOutOfView = false;
        Title = null;
        IsMusic = false;
        Cover = null;
        Level = 0;
        IsPlaying = false;
        Progress = 0;
    }

    [RelayCommand]
    private void Toggle() => _toggle?.Invoke();

    [RelayCommand]
    private void Close()
    {
        var stop = _stop;
        Clear();
        stop?.Invoke();
    }

    public void Seek(double fraction) => _seek?.Invoke(fraction);

    [RelayCommand]
    private void ToggleLoop() => Loop = !Loop;
}
