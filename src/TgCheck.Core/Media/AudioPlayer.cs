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
    private Timer? _timer;

    public string? Key { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Position => _stream?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _stream?.TotalTime ?? TimeSpan.Zero;
    /// <summary>Текущая громкость 0..1 (для волн в плеере).</summary>
    public float Level { get; private set; }
    /// <summary>Повторять трек по кругу.</summary>
    public bool Loop { get; set; }

    /// <summary>Вызывается ~20 раз в секунду во время воспроизведения и при смене состояния (из фонового потока).</summary>
    public event Action? Changed;
    /// <summary>Трек доиграл до конца (ключ трека) — для автопроигрывания следующего.</summary>
    public event Action<string>? Ended;

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
                _stream = Open(path);
                _output = new WaveOutEvent { DesiredLatency = 120 };
                // Громкость на лету — для «живых» волн у обложки в плеере.
                var meter = new NAudio.Wave.SampleProviders.MeteringSampleProvider(new NAudio.Wave.SampleProviders.SampleChannel(_stream, true), _stream.WaveFormat.SampleRate / 25);
                meter.StreamVolume += (_, e) => Level = e.MaxSampleValues.Length == 0 ? 0 : e.MaxSampleValues.Max();
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
                    }
                    Notify();
                    if (finished) Ended?.Invoke(playingKey);
                };
                Key = key;
                _output.Play();
                _timer = new Timer(_ => { if (IsPlaying) Notify(); }, null, 0, 50);
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
