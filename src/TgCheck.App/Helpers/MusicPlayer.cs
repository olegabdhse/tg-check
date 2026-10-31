using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using NAudio.Wave;
using TgCheck.Core.Media;
using TgCheck.Core.Services;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace TgCheck.App.Helpers;

/// <summary>
/// Проигрыватель песен на встроенном VLC: понимает любые форматы (в Telegram песни бывают и «mp3», который внутри
/// фрагментированный MP4 — системный декодер такие не играл, песня сразу вставала) и играет прямо по мере скачивания.
/// Звук VLC отдаёт нам, а мы выводим его сами — так видна громкость и басы для волн у обложки.
/// </summary>
public sealed class MusicPlayer
{
    public static MusicPlayer Instance { get; } = new();

    private const int Rate = 44100, Channels = 2;
    private readonly object _lock = new();
    private VlcPlayer? _player;
    private Media? _media;
    private Stream? _source;
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private Timer? _timer;
    private long _lengthMs;
    private bool _ended;
    // Что выбрал пользователь (играть или пауза). Состояние VLC меняется с задержкой — по нему кнопка показывала «пауза» у остановленной песни.
    private bool _paused;

    public string? Key { get; private set; }
    public bool IsPlaying => _player is not null && !_paused && !_ended;
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, _player?.Time ?? 0));
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(_lengthMs, _player?.Length ?? 0));
    public float Level { get; private set; }
    public float Bass { get; private set; }
    public bool Loop { get; set; }
    /// <summary>Песня открывается или догружается — в плеере вместо «стоп» показывается загрузка.</summary>
    public bool IsBuffering =>
        _player is { State: VLCState.Opening or VLCState.Buffering } || (_source as GrowingFileStream)?.IsWaiting == true;

    /// <summary>Состояние изменилось (~20 раз в секунду, пока играет). Из фонового потока.</summary>
    public event Action? Changed;
    /// <summary>Песня доиграла до конца (ключ). Из фонового потока.</summary>
    public event Action<string>? Ended;

    /// <summary>Играть файл (path) или продолжить ту же песню (тот же ключ).</summary>
    public void Play(string key, string path)
    {
        lock (_lock)
        {
            if (Key == key && _player is { } p)
            {
                _paused = false;
                if (_ended) Restart(p);
                else p.SetPause(false);
                Notify();
                return;
            }
        }
        if (!string.IsNullOrEmpty(path)) Open(key, path, null);
    }

    /// <summary>Играть из потока (песня, которая ещё качается; готовый файл — по пути).</summary>
    public void PlayStream(string key, Stream source)
    {
        if (source is FileStream file)
        {
            var path = file.Name;
            file.Dispose();
            Open(key, path, null);
            return;
        }
        Open(key, null, source);
    }

    private void Open(string key, string? path, Stream? source)
    {
        var vlc = VlcRuntime.GetAsync().GetAwaiter().GetResult() ?? throw new InvalidOperationException("Видеодвижок не загрузился.");
        lock (_lock)
        {
            StopInternal();
            _buffer = new BufferedWaveProvider(new WaveFormat(Rate, 16, Channels))
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
            };
            _output = new WaveOutEvent { DesiredLatency = 150 };
            _output.Init(new LevelMeter(_buffer.ToSampleProvider(), (level, bass) => { Level = level; Bass = bass; }));
            _output.Play();

            _player = new VlcPlayer(vlc);
            // Звук — нам: PCM 16 бит стерео, дальше сами выводим и меряем громкость.
            _player.SetAudioFormat("S16N", Rate, Channels);
            _player.SetAudioCallbacks(OnAudio, OnPause, OnResume, OnFlush, OnDrain);
            _player.EndReached += OnEndReached;
            // VLC сам сменил состояние (догрузил, встал на паузу, остановился) — плеер на экране обновляется сразу.
            _player.Playing += OnVlcState;
            _player.Paused += OnVlcState;
            _player.Stopped += OnVlcState;
            _player.LengthChanged += (_, e) => _lengthMs = e.Length;
            _media = source is not null ? new Media(vlc, new StreamMediaInput(source)) : new Media(vlc, path!, FromType.FromPath);
            _source = source;
            _ended = false;
            _paused = false;
            _lengthMs = 0;
            Key = key;
            _player.Play(_media);
            // Песня не скачалась (ошибка сети, устаревшая ссылка) — плеер закрывается, а не крутит загрузку вечно.
            if (source is GrowingFileStream growing)
                _ = growing.Done.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result is not null) return;
                    Logging.Write("music: песня не скачалась");
                    lock (_lock) { if (Key != key) return; StopInternal(); }
                    Notify();
                });
            _timer = new Timer(_ => { if (IsPlaying || IsBuffering) Notify(); }, null, 0, 50);
        }
        Notify();
    }

    // ---- звук от VLC
    private void OnAudio(IntPtr data, IntPtr samples, uint count, long pts)
    {
        var buffer = _buffer;
        if (buffer is null) return;
        var bytes = (int)count * Channels * 2;
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            Marshal.Copy(samples, chunk, 0, bytes);
            buffer.AddSamples(chunk, 0, bytes);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(chunk); }
    }

    private void OnPause(IntPtr data, long pts) { _output?.Pause(); Level = Bass = 0; }
    private void OnResume(IntPtr data, long pts) => _output?.Play();
    private void OnFlush(IntPtr data, long pts) => _buffer?.ClearBuffer();
    private void OnDrain(IntPtr data) { }

    private void OnVlcState(object? sender, EventArgs e) => ThreadPool.QueueUserWorkItem(_ => Notify());

    private void OnEndReached(object? sender, EventArgs e)
    {
        var key = Key;
        // Из обработчика событий VLC нельзя управлять плеером — в пуле потоков.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_lock)
            {
                if (Key != key || _player is not { } p) return;
                if (Loop) { Restart(p); return; }
                _ended = true;
                Level = Bass = 0;
            }
            Notify();
            if (key is not null) Ended?.Invoke(key);
        });
    }

    private void Restart(VlcPlayer p)
    {
        _ended = false;
        _buffer?.ClearBuffer();
        if (_source is { CanSeek: true } s) s.Position = 0;
        p.Stop();
        p.Play(_media!);
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_player is null) return;
            _paused = true;
            _player.SetPause(true);
        }
        Notify();
    }

    public void Toggle(string key, string path)
    {
        if (Key == key && IsPlaying) Pause();
        else Play(key, path);
    }

    /// <summary>Перемотка: fraction 0..1.</summary>
    public void Seek(double fraction)
    {
        lock (_lock)
        {
            if (_player is not { } p) return;
            if (_ended) { Restart(p); }
            p.Position = (float)Math.Clamp(fraction, 0, 0.999);
        }
        Notify();
    }

    public void Stop()
    {
        lock (_lock) StopInternal();
        Notify();
    }

    private void StopInternal()
    {
        _timer?.Dispose();
        _timer = null;
        var player = _player;
        var media = _media;
        var source = _source;
        _player = null;
        _media = null;
        _source = null;
        Key = null;
        Level = Bass = 0;
        _output?.Dispose();
        _output = null;
        _buffer = null;
        if (player is null) { source?.Dispose(); return; }
        player.EndReached -= OnEndReached;
        player.Playing -= OnVlcState;
        player.Paused -= OnVlcState;
        player.Stopped -= OnVlcState;
        // Остановка VLC может занять время (ждёт свои потоки) — не в потоке интерфейса.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { player.Stop(); } catch { }
            player.Dispose();
            media?.Dispose();
            source?.Dispose();
        });
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logging.Write("music: " + ex.Message); }
    }
}
