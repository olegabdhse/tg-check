using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;
using TgCheck.Core.Services;

namespace TgCheck.Core.Media;

/// <summary>
/// Проигрыватель голосовых (Ogg/Opus, декодируется Concentus) и аудиофайлов (mp3/m4a через Media Foundation).
/// Одновременно звучит один трек; Key — что сейчас играет (например, id сообщения).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    public static AudioPlayer Instance { get; } = new();

    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private WaveStream? _stream;
    private Stream? _source;
    private Timer? _timer;

    public string? Key { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Position => _stream?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _stream?.TotalTime ?? TimeSpan.Zero;
    /// <summary>Текущая громкость 0..1 (для волн в плеере).</summary>
    public float Level { get; private set; }
    /// <summary>Повторять трек по кругу.</summary>
    public bool Loop { get; set; }
    /// <summary>Сила басов сейчас 0..1 (низкие частоты) — обложка в плеере «толкается» на басах.</summary>
    public float Bass { get; private set; }

    /// <summary>Вызывается ~20 раз в секунду во время воспроизведения и при смене состояния (из фонового потока).</summary>
    public event Action? Changed;
    /// <summary>Трек доиграл до конца (ключ трека) — для автопроигрывания следующего.</summary>
    public event Action<string>? Ended;

    /// <summary>Играть из потока (песня, которая ещё качается).</summary>
    public void PlayStream(string key, Stream source)
    {
        lock (_lock)
        {
            StopInternal();
            try
            {
                Start(key, new StreamMediaFoundationReader(source));
                _source = source;
            }
            catch (Exception ex)
            {
                Logging.Write("audio stream: " + ex.Message);
                StopInternal();
                source.Dispose();
                throw new InvalidOperationException("Не удалось воспроизвести аудио: " + ex.Message, ex);
            }
        }
        Notify();
    }

    public void Play(string key, string path)
    {
        lock (_lock)
        {
            if (Key == key && _output is not null)
            {
                if (_output.PlaybackState == PlaybackState.Paused) _output.Play();
                else if (_output.PlaybackState == PlaybackState.Stopped)
                {
                    _stream!.Position = 0;
                    _output.Play();
                }
                Notify();
                return;
            }

            StopInternal();
            try
            {
                Start(key, Open(path));
            }
            catch (Exception ex)
            {
                Logging.Write("audio: " + ex.Message);
                StopInternal();
                throw new InvalidOperationException("Не удалось воспроизвести аудио: " + ex.Message, ex);
            }
        }
        Notify();
    }

    // Под замком: новый трек — вывод, замер громкости для волн, повтор и «доиграло».
    private void Start(string key, WaveStream stream)
    {
        _stream = stream;
        _output = new WaveOutEvent { DesiredLatency = 120 };
        // Громкость на лету — для «живых» волн у обложки в плеере.
        var meter = new LevelMeter(new NAudio.Wave.SampleProviders.SampleChannel(_stream, true), (level, bass) => { Level = level; Bass = bass; });
        _output.Init(meter);
        var playingKey = key;
        _output.PlaybackStopped += (_, _) =>
        {
            var finished = false;
            lock (_lock)
            {
                if (_stream is not null && _stream.Position >= _stream.Length)
                {
                    _stream.Position = 0;
                    finished = Key == playingKey;
                    // Повтор: доиграло — сначала (плеер не закрывается).
                    if (finished && Loop && _output is { } o) { o.Play(); finished = false; }
                }
                Level = 0;
                Bass = 0;
            }
            Notify();
            if (finished) Ended?.Invoke(playingKey);
        };
        Key = key;
        _output.Play();
        _timer = new Timer(_ => { if (IsPlaying) Notify(); }, null, 0, 50);
    }

    public void Pause()
    {
        lock (_lock) _output?.Pause();
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
            if (_stream is null) return;
            var pos = (long)(_stream.Length * Math.Clamp(fraction, 0, 1));
            _stream.Position = pos - pos % _stream.WaveFormat.BlockAlign;
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
        _output?.Dispose();
        _output = null;
        _stream?.Dispose();
        _stream = null;
        _source?.Dispose(); // файл песни, которая ещё качалась
        _source = null;
        Key = null;
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch { /* подписчик не должен ронять плеер */ }
    }

    private static WaveStream Open(string path)
    {
        if (IsOgg(path)) return DecodeOpus(path);
        return new MediaFoundationReader(path);
    }

    private static bool IsOgg(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return fs.Read(head) == 4 && head.SequenceEqual("OggS"u8);
    }

    // Голосовые короткие — декодируем целиком в PCM 48 кГц моно.
    private static WaveStream DecodeOpus(string path)
    {
        using var fs = File.OpenRead(path);
        var decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        var ogg = new OpusOggReadStream(decoder, fs);
        using var pcm = new MemoryStream();
        while (ogg.HasNextPacket)
        {
            var packet = ogg.DecodeNextPacket();
            if (packet is null) continue;
            var bytes = new byte[packet.Length * 2];
            Buffer.BlockCopy(packet, 0, bytes, 0, bytes.Length);
            pcm.Write(bytes);
        }
        return new RawSourceWaveStream(new MemoryStream(pcm.ToArray()), new WaveFormat(48000, 16, 1));
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Замер громкости на лету (каждые ~40 мс): пик всего сигнала и пик басов — после простого фильтра низких частот (~150 Гц).
/// </summary>
internal sealed class LevelMeter(ISampleProvider source, Action<float, float> report) : ISampleProvider
{
    private readonly int _block = source.WaveFormat.SampleRate * source.WaveFormat.Channels / 25;
    private readonly float _alpha = 1 - MathF.Exp(-2 * MathF.PI * 150f / source.WaveFormat.SampleRate);
    private float _low, _peak, _bassPeak;
    private int _count;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            var s = buffer[offset + i];
            _low += _alpha * (s - _low);
            var a = MathF.Abs(s);
            if (a > _peak) _peak = a;
            var b = MathF.Abs(_low);
            if (b > _bassPeak) _bassPeak = b;
            if (++_count < _block) continue;
            report(_peak, _bassPeak);
            _peak = _bassPeak = 0;
            _count = 0;
        }
        return read;
    }
}
