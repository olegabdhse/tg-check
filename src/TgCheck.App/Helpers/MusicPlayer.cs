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
    private StreamMediaInput? _input;
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private Timer? _timer;
    private long _lengthMs;
    private bool _ended;
    // Что выбрал пользователь (играть или пауза). Состояние VLC меняется с задержкой — по нему кнопка показывала «пауза» у остановленной песни.
    private bool _paused;

    public string? Key { get; private set; }
    public bool IsPlaying => _player is not null && !_paused && !_ended;
    /// <summary>Песня играет или стоит на паузе.</summary>
    public bool HasSong => _player is not null && !_ended;
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, _player?.Time ?? 0));
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(_lengthMs, _player?.Length ?? 0));
    public float Level { get; private set; }
    public float Bass { get; private set; }
    public bool Loop { get; set; }
    /// <summary>Песня открывается или догружается — в плеере вместо «стоп» показывается загрузка.</summary>
    public bool IsBuffering
    {
        get
        {
            var waiting = _player is { State: VLCState.Opening or VLCState.Buffering } || (_source as IStreamingSource)?.IsWaiting == true;
            if (!waiting || _paused || _player is not { } p) return false;
            // VLC порой так и остаётся в «буферизации», хотя звук давно идёт, — загрузку показываем, только если позиция
            // не двигается (за последние 0,7 с).
            var time = p.Time;
            var now = Environment.TickCount64;
            if (time != _lastTime) { _lastTime = time; _lastAdvance = now; }
            return time <= 0 || now - _lastAdvance > 700;
        }
    }

    private long _lastTime = -1, _lastAdvance;

    /// <summary>Состояние изменилось (~4 раза в секунду, пока играет). Из фонового потока.</summary>
    public event Action? Changed;
    /// <summary>Песня доиграла до конца (ключ). Из фонового потока.</summary>
    public event Action<string>? Ended;
    /// <summary>Песня не доиграла: файл не скачался или VLC не смог её играть (ключ). Плеер уже остановлен. Из фонового потока.</summary>
    public event Action<string>? Failed;

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
            _paused = false;
            StartOutput();

            _player = new VlcPlayer(vlc);
            // Звук — нам: PCM 16 бит стерео, дальше сами выводим и меряем громкость.
            _player.SetAudioFormat("S16N", Rate, Channels);
            _player.SetAudioCallbacks(OnAudio, OnPause, OnResume, OnFlush, OnDrain);
            _player.EndReached += OnEndReached;
            _player.EncounteredError += OnVlcError;
            // VLC сам сменил состояние (догрузил, встал на паузу, остановился) — плеер на экране обновляется сразу.
            _player.Playing += OnVlcState;
            _player.Paused += OnVlcState;
            _player.Stopped += OnVlcState;
            _player.LengthChanged += (_, e) => _lengthMs = e.Length;
            _input = source is not null ? new StreamMediaInput(source) : null;
            _media = _input is not null ? new Media(vlc, _input) : new Media(vlc, path!, path!.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? FromType.FromLocation : FromType.FromPath);
            _media.AddOption(":no-video"); // только звук: вшитую в песню обложку VLC иначе показывал в своём окне (Direct3D11 output)
            if (source is null && path!.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _media.AddOption(":network-caching=500"); // по сети — начинать играть быстрее
            _source = source;
            _ended = false;
            _paused = false;
            _lengthMs = 0;
            _heardMs = 0;
            _earlyEnds = 0;
            Key = key;
            _player.Play(_media);
            // Песня не скачалась (ошибка сети, устаревшая ссылка) — плеер не крутит загрузку вечно, очередь идёт дальше.
            if (source is IStreamingSource growing)
                _ = growing.Done.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result is not null) return;
                    Fail(key, "песня не скачалась");
                });
            _timer = new Timer(_ =>
            {
                if (_player is { Time: > 0 and var time }) _heardMs = time;
                if (IsPlaying || IsBuffering) Notify();
            }, null, 0, 250);
        }
        Notify();
    }

    // ---- вывод звука
    // Устройство пропадает: после сна его какое-то время нет (NoDriver), наушники отключили. Вывод пересоздаётся, пока устройство не вернётся.
    private const int FastOutputRetries = 40;
    private bool _recoveringOutput;

    private MusicPlayer()
    {
        AudioDeviceWatcher.DefaultOutputChanged += OnDefaultOutputChanged;
        AudioDeviceWatcher.Start();
    }

    // Сменилось устройство по умолчанию — переводим звук на него, песня продолжается с того же места.
    private void OnDefaultOutputChanged()
    {
        lock (_lock)
        {
            if (_player is null || _recoveringOutput) return;
            DisposeOutput(_output);
            _output = null;
            StartOutput();
        }
        Logging.Write("music: звук переведён на устройство по умолчанию");
    }

    // Под _lock. Не вышло — песня продолжает идти, а вывод восстанавливается в фоне.
    private void StartOutput()
    {
        if (_buffer is not { } buffer) return;
        var output = new WaveOutEvent { DesiredLatency = 150 };
        try
        {
            output.PlaybackStopped += (_, e) => { if (e.Exception is { } ex) OnOutputFailed(output, ex); };
            output.Init(new LevelMeter(buffer.ToSampleProvider(), (level, bass) => { Level = level; Bass = bass; }));
            if (!_paused) output.Play();
            _output = output;
        }
        catch (Exception ex)
        {
            DisposeOutput(output);
            OnOutputFailed(null, ex);
        }
    }

    private static void DisposeOutput(WaveOutEvent? output)
    {
        try { output?.Dispose(); }
        catch (Exception ex) { Logging.Write("music: закрытие вывода звука: " + ex.Message); }
    }

    private void OnOutputFailed(WaveOutEvent? failed, Exception ex)
    {
        lock (_lock)
        {
            if (_recoveringOutput || (failed is not null && !ReferenceEquals(failed, _output))) return;
            _recoveringOutput = true;
        }
        Logging.Write("music: вывод звука недоступен: " + ex.Message);
        _ = Task.Run(RecoverOutputAsync);
    }

    private async Task RecoverOutputAsync()
    {
        // Пока песня открыта: устройство может вернуться и через час, а песня без вывода так и шла бы молча.
        for (var attempt = 1; ; attempt++)
        {
            await Task.Delay(attempt <= FastOutputRetries ? 1500 : 5000);
            lock (_lock)
            {
                if (_player is null) { _recoveringOutput = false; return; }
                DisposeOutput(_output);
                _output = null;
                StartOutput();
                if (_output is null) continue;
                _recoveringOutput = false;
                Logging.Write($"music: вывод звука восстановлен (попытка {attempt})");
                return;
            }
        }
    }

    // ---- звук от VLC
    // Вызовы идут из VLC: исключение отсюда нельзя поймать выше, оно завершает всю программу.
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
        catch (Exception ex) { Logging.Write("music: " + ex.Message); }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(chunk); }
    }

    private void OnPause(IntPtr data, long pts)
    {
        var output = _output;
        try { output?.Pause(); }
        catch (Exception ex) { OnOutputFailed(output, ex); }
        Level = Bass = 0;
    }

    private void OnResume(IntPtr data, long pts)
    {
        var output = _output;
        try { output?.Play(); }
        catch (Exception ex) { OnOutputFailed(output, ex); }
    }

    private void OnFlush(IntPtr data, long pts)
    {
        try { _buffer?.ClearBuffer(); }
        catch (Exception ex) { Logging.Write("music: " + ex.Message); }
    }

    private void OnDrain(IntPtr data) { }

    private void OnVlcState(object? sender, EventArgs e) => ThreadPool.QueueUserWorkItem(_ => Notify());

    private void OnVlcError(object? sender, EventArgs e)
    {
        var key = Key;
        // Из обработчика событий VLC нельзя управлять плеером — в пуле потоков.
        if (key is not null) ThreadPool.QueueUserWorkItem(_ => Fail(key, "VLC не смог играть песню"));
    }

    private void Fail(string key, string why)
    {
        lock (_lock) { if (Key != key) return; StopInternal(); }
        Logging.Write($"music: {key}: {why}");
        Notify();
        Failed?.Invoke(key);
    }

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
                // До конца ещё далеко: чтение оборвалось, а не песня кончилась. Продолжаем с того же места, а не переключаем на следующую.
                if (_lengthMs - _heardMs > EarlyEndMs && _earlyEnds < MaxEarlyEnds && _media is { } media)
                {
                    _earlyEnds++;
                    Logging.Write($"music: {key} оборвалась на {_heardMs / 1000} с из {_lengthMs / 1000} — продолжаем");
                    media.AddOption($":start-time={_heardMs / 1000.0:0.0}".Replace(',', '.'));
                    Restart(p);
                    return;
                }
                _ended = true;
                Level = Bass = 0;
            }
            Notify();
            if (key is not null) Ended?.Invoke(key);
        });
    }

    private const long EarlyEndMs = 5000;
    private const int MaxEarlyEnds = 2;
    private long _heardMs;
    private int _earlyEnds;

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
        var input = _input;
        _player = null;
        _media = null;
        _source = null;
        _input = null;
        Key = null;
        Level = Bass = 0;
        DisposeOutput(_output);
        _output = null;
        _buffer = null;
        if (player is null) { source?.Dispose(); return; }
        player.EndReached -= OnEndReached;
        player.EncounteredError -= OnVlcError;
        player.Playing -= OnVlcState;
        player.Paused -= OnVlcState;
        player.Stopped -= OnVlcState;
        // Остановка VLC может занять время (ждёт свои потоки) — не в потоке интерфейса.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            source?.Dispose(); // сначала поток: VLC мог ждать в нём ещё не скачанный кусок, и остановка ждала бы загрузку
            try { player.Stop(); } catch { }
            player.Dispose();
            media?.Dispose();
            input?.Dispose(); // иначе вход VLC держит себя в памяти вместе с потоком и загрузкой — все прослушанные песни оставались
        });
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logging.Write("music: " + ex.Message); }
    }
}
