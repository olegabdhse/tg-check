using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;

namespace TgCheck.App.ViewModels;

/// <summary>
/// Что сейчас играет из сообщений (голосовое или кружок со звуком) — для панели над лентой, как в Telegram Desktop.
/// Кружок, ушедший за край экрана, показывается закреплённым сверху (через VisualBrush того же видео),
/// так что его можно смотреть дальше и листать чат. Музыка — отдельно (<see cref="MusicNow"/>): на время голосового
/// или кружка она встаёт на паузу и потом продолжается.
/// </summary>
public sealed partial class NowPlaying : ObservableObject
{
    public static NowPlaying Instance { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsActive), nameof(ShowTopBar))] private string? _title;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowFloatingRound))] private Brush? _roundBrush;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowFloatingRound))] private bool _roundOutOfView;

    public bool IsActive => Title is not null;
    /// <summary>Панель над лентой чата (голосовые и кружки).</summary>
    public bool ShowTopBar => IsActive;

    /// <summary>Голосовое, которое выбрали последним (пока файл качается, могли выбрать другое).</summary>
    public string? RequestedAudio { get; set; }
    public bool ShowFloatingRound => RoundBrush is not null && RoundOutOfView;

    private Action? _toggle;
    private Action? _stop;
    private Action<double>? _seek;

    private NowPlaying()
    {
        AudioPlayer.Instance.Changed += () => Anim.Ui(SyncAudio);
        // Голосовое доиграло и больше играть нечего — панель убирается.
        AudioPlayer.Instance.Ended += key => Anim.Ui(async () =>
        {
            await Task.Delay(400); // за это время чат может включить следующее голосовое
            if (_audioKey == key && !AudioPlayer.Instance.IsPlaying) Close();
        });
    }

    // ---- голосовые (NAudio)
    private string? _audioKey;

    public void StartAudio(string key, string title, string subtitle)
    {
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
        MusicNow.Instance.Interrupt(MusicNow.Round); // музыка — на паузу, пока кружок звучит
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
        var wasRound = RoundBrush is not null;
        _audioKey = null;
        _toggle = _stop = null;
        _seek = null;
        RoundBrush = null;
        RoundOutOfView = false;
        Title = null;
        IsPlaying = false;
        Progress = 0;
        if (wasRound) MusicNow.Instance.Resume(MusicNow.Round); // кружок кончился — музыка продолжается
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
}
