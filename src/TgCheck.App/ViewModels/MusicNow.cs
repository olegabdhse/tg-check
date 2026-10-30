using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;

namespace TgCheck.App.ViewModels;

/// <summary>
/// Играющая песня — плеер музыки под списком чатов и в островке. У музыки свой проигрыватель
/// (<see cref="MusicPlayer"/> на VLC): просмотр фото её не трогает, а голосовое, кружок, видео или история
/// ставят её на паузу — когда они закончились, песня продолжается сама.
/// </summary>
public sealed partial class MusicNow : ObservableObject
{
    public static MusicNow Instance { get; } = new();

    // Что прерывает музыку (может быть несколько сразу; продолжаем, когда не осталось ни одного).
    public const string Voice = "voice", Round = "round", Video = "video", Story = "story", Recording = "recording";

    private static MusicPlayer Player => MusicPlayer.Instance;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsActive), nameof(ShowMusicBar), nameof(ShowIslandMusic))] private string? _title;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isPlaying;
    /// <summary>Песня догружается (открывается или проигрыватель догнал загрузку) — вместо «стоп» показываем загрузку.</summary>
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private Brush? _coverBrush;
    /// <summary>Громкость сейчас (0..1) — для волн вокруг обложки.</summary>
    [ObservableProperty] private double _level;
    [ObservableProperty] private bool _loop;
    /// <summary>Плеер музыки в островке включён в настройках.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowIslandMusic))] private bool _islandMusicEnabled = UiSettings.ShowIslandMusic;

    public bool IsActive => Title is not null;
    /// <summary>Плеер музыки на экране (при закрытии сначала уезжает он, а песня останавливается следом).</summary>
    public bool ShowMusicBar => IsActive && !_closing;
    public bool ShowIslandMusic => ShowMusicBar && IslandMusicEnabled;

    partial void OnLoopChanged(bool value) => Player.Loop = value;

    private string? _key;
    // Выбрали песню, а она ещё открывается (качается начало): плеер уже показывает её, старая сразу остановлена.
    private string? _pendingKey;
    private bool _closing;
    private readonly HashSet<string> _interrupts = [];
    private bool _resumeAfter;

    private MusicNow()
    {
        Player.Changed += () => Anim.Ui(Sync);
        // Голосовое играет — музыка ждёт; остановили или доиграло — продолжается.
        AudioPlayer.Instance.Changed += () => Anim.Ui(async () =>
        {
            if (AudioPlayer.Instance.IsPlaying) { Interrupt(Voice); return; }
            if (!_interrupts.Contains(Voice)) return;
            // Между голосовыми подряд (следующее включается само) музыка не должна успеть «вклиниться».
            await Task.Delay(600);
            if (!AudioPlayer.Instance.IsPlaying) Resume(Voice);
        });
    }

    /// <summary>Смена трека — сразу на экране, не дожидаясь загрузки.</summary>
    public void ShowPending(string key, string title, string? performer, ImageSource? cover, Brush coverBrush)
    {
        _pendingKey = key;
        Start(key, title, performer, cover, coverBrush);
        IsPlaying = false;
        Progress = 0;
        Level = 0;
        TimeText = "Загрузка…";
        Player.Stop(); // прошлая песня замолкает сразу
        IsBuffering = true;
        _resumeAfter = false;
    }

    /// <summary>Песня не открылась — убрать её из плеера.</summary>
    public void CancelPending(string key)
    {
        if (_pendingKey != key) return;
        _pendingKey = null;
        if (_key == key) Clear();
    }

    /// <summary>Песня: обложка (или градиент песни), название и исполнитель.</summary>
    public void Start(string key, string title, string? performer, ImageSource? cover, Brush coverBrush)
    {
        _key = key;
        Subtitle = performer ?? "";
        Cover = cover;
        CoverBrush = coverBrush;
        Title = title;
        Sync();
    }

    private void Sync()
    {
        if (_key is null) return;
        if (Player.Key != _key)
        {
            if (_key == _pendingKey) return; // новая песня ещё открывается — она уже на экране
            if (Player.Key is null) Clear();
            return;
        }
        if (_pendingKey == _key) _pendingKey = null;
        IsPlaying = Player.IsPlaying;
        IsBuffering = Player.IsBuffering;
        Level = Player.IsPlaying ? Player.Level : 0;
        var total = Player.Duration.TotalSeconds;
        Progress = total > 0 ? Player.Position.TotalSeconds / total : 0;
        TimeText = $"{Fmt.Duration(Player.Position.TotalSeconds)} / {Fmt.Duration(total)}";
    }

    /// <summary>Зазвучало что-то другое (голосовое, кружок, видео, история, запись) — песня на паузу.</summary>
    public void Interrupt(string reason)
    {
        if (!_interrupts.Add(reason) || _interrupts.Count > 1) return;
        _resumeAfter = Player.IsPlaying;
        if (_resumeAfter) Player.Pause();
    }

    /// <summary>То, что прерывало, закончилось — песня продолжается (если играла до этого).</summary>
    public void Resume(string reason)
    {
        if (!_interrupts.Remove(reason) || _interrupts.Count > 0 || !_resumeAfter) return;
        _resumeAfter = false;
        if (_key is not null && Player.Key == _key) Player.Play(_key, "");
    }

    /// <summary>Закрыть плеер музыки: убрать его с экрана и только потом остановить песню.</summary>
    public async void CloseMusic()
    {
        if (_closing) return;
        _closing = true;
        OnPropertyChanged(nameof(ShowMusicBar));
        OnPropertyChanged(nameof(ShowIslandMusic));
        await Task.Delay(260);
        _closing = false;
        Clear();
        Player.Stop();
        OnPropertyChanged(nameof(ShowMusicBar));
        OnPropertyChanged(nameof(ShowIslandMusic));
    }

    private void Clear()
    {
        _key = null;
        _resumeAfter = false;
        Title = null;
        Cover = null;
        Level = 0;
        IsBuffering = false;
        IsPlaying = false;
        Progress = 0;
    }

    [RelayCommand]
    private void Toggle()
    {
        if (_key is null) return;
        _resumeAfter = false; // сами нажали — дальше решает пользователь
        Player.Toggle(_key, "");
    }

    public void Seek(double fraction) => Player.Seek(fraction);

    [RelayCommand]
    private void SeekStart() => Seek(0);

    [RelayCommand]
    private void SeekEnd() => Seek(0.999);

    [RelayCommand]
    private void ToggleLoop() => Loop = !Loop;
}
