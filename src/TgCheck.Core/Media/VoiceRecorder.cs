using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.Wave;

namespace TgCheck.Core.Media;

/// <summary>Готовое голосовое: файл Ogg/Opus, длительность и волна (как требует Telegram).</summary>
public sealed record VoiceRecording(string Path, double DurationSeconds, byte[] Waveform);

/// <summary>
/// Запись голосового с микрофона: 48 кГц моно. Звук копится в памяти — запись можно остановить, прослушать,
/// обрезать с краёв и только потом закодировать в Opus/Ogg (тот же формат, что у официальных клиентов).
/// Попутно собирается громкость: для живой волны во время записи и для волны в сообщении.
/// </summary>
public sealed class VoiceRecorder : IDisposable
{
    private const int SampleRate = 48000;
    /// <summary>Пиков в секунду: один пик — 50 мс звука.</summary>
    public const int PeaksPerSecond = 20;

    private WaveInEvent? _input;
    private readonly MemoryStream _pcm = new(); // 16 бит, моно
    private long _samples;
    private readonly List<float> _peaks = [];
    private float _blockPeak;
    private int _blockSamples;
    private readonly object _lock = new();
    private WaveOutEvent? _output;

    public bool IsRecording => _input is not null;
    public double Seconds => (double)Interlocked.Read(ref _samples) / SampleRate;

    /// <summary>Текущая громкость 0..1 — для «пульсации» кнопки записи.</summary>
    public float Level { get; private set; }

    /// <summary>Громкость по 50 мс с начала записи (копия) — для волны.</summary>
    public float[] Peaks()
    {
        lock (_lock) return [.. _peaks];
    }

    public void Start()
    {
        if (_input is not null) return;
        lock (_lock)
        {
            _pcm.SetLength(0);
            _peaks.Clear();
            _blockPeak = 0;
            _blockSamples = 0;
        }
        Interlocked.Exchange(ref _samples, 0);
        _input = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50 };
        _input.DataAvailable += OnData;
        _input.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var count = e.BytesRecorded / 2;
        lock (_lock)
        {
            _pcm.Write(e.Buffer, 0, count * 2);
            var level = 0f;
            for (var i = 0; i < count; i++)
            {
                var a = Math.Abs(BitConverter.ToInt16(e.Buffer, i * 2) / 32768f);
                if (a > level) level = a;
                if (a > _blockPeak) _blockPeak = a;
                if (++_blockSamples >= SampleRate / PeaksPerSecond)
                {
                    _peaks.Add(_blockPeak);
                    _blockPeak = 0;
                    _blockSamples = 0;
                }
            }
            Level = level;
        }
        Interlocked.Add(ref _samples, count);
    }

    /// <summary>Остановить запись с микрофона (звук остаётся — его можно прослушать, обрезать и отправить).</summary>
    public void StopCapture()
    {
        if (_input is null) return;
        _input.DataAvailable -= OnData;
        try { _input.StopRecording(); } catch { }
        _input.Dispose();
        _input = null;
        Level = 0;
    }

    /// <summary>Остановить и сразу закодировать всё; null — запись слишком короткая или отменена.</summary>
    public VoiceRecording? Stop(bool cancel = false)
    {
        StopCapture();
        return cancel ? null : Encode(0, Seconds);
    }

    /// <summary>Закодировать часть записи [from; to] в секундах. null — короче 0,7 с.</summary>
    public VoiceRecording? Encode(double from, double to)
    {
        StopPlayback();
        var (start, count) = Range(from, to);
        if (count < SampleRate * 0.7) return null;
        var samples = new short[count];
        lock (_lock) Buffer.BlockCopy(_pcm.GetBuffer(), start * 2, samples, 0, count * 2);

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TgCheck", "voice");
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"voice_{DateTime.Now:yyyyMMdd_HHmmss_fff}.ogg");
        using (var file = File.Create(path))
        {
            var encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 32000;
            var ogg = new OpusOggWriteStream(encoder, file);
            ogg.WriteSamples(samples, 0, count);
            ogg.Finish();
        }
        float[] peaks;
        lock (_lock)
        {
            var p0 = Math.Clamp(start * PeaksPerSecond / SampleRate, 0, _peaks.Count);
            var p1 = Math.Clamp((start + count) * PeaksPerSecond / SampleRate, p0, _peaks.Count);
            peaks = _peaks.GetRange(p0, p1 - p0).ToArray();
        }
        return new VoiceRecording(path, (double)count / SampleRate, EncodeWaveform(peaks));
    }

    private (int Start, int Count) Range(double from, double to)
    {
        long total;
        lock (_lock) total = _pcm.Length / 2;
        var start = (int)Math.Clamp(from * SampleRate, 0, total);
        var end = (int)Math.Clamp(to * SampleRate, start, total);
        return (start, end - start);
    }

    // ---- прослушивание перед отправкой

    /// <summary>Сколько секунд от начала куска уже проиграно (null — не играет).</summary>
    public double? PlaybackSeconds => _output is { } o && _playback is { } s ? (double)s.Position / (SampleRate * 2) : null;
    private RawSourceWaveStream? _playback;

    /// <summary>Проиграть часть записи [from; to]; onEnd — доиграло до конца (вызывается из фонового потока).</summary>
    public void Play(double from, double to, Action onEnd)
    {
        StopPlayback();
        var (start, count) = Range(from, to);
        if (count <= 0) return;
        byte[] bytes;
        lock (_lock) bytes = _pcm.GetBuffer().AsSpan(start * 2, count * 2).ToArray();
        _playback = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(SampleRate, 16, 1));
        var output = _output = new WaveOutEvent { DesiredLatency = 120 };
        output.Init(_playback);
        output.PlaybackStopped += (_, _) => { if (ReferenceEquals(_output, output)) onEnd(); };
        output.Play();
    }

    public void StopPlayback()
    {
        var output = _output;
        _output = null;
        if (output is null) return;
        try { output.Stop(); } catch { }
        output.Dispose();
        _playback?.Dispose();
        _playback = null;
    }

    // Волна Telegram: 100 значений по 5 бит, упакованных подряд (младшие биты первыми).
    private static byte[] EncodeWaveform(IReadOnlyList<float> peaks)
    {
        const int bars = 100;
        var values = new int[bars];
        if (peaks.Count > 0)
        {
            var max = Math.Max(peaks.Max(), 0.05f);
            for (var i = 0; i < bars; i++)
            {
                var from = i * peaks.Count / bars;
                var to = Math.Max(from + 1, (i + 1) * peaks.Count / bars);
                var peak = 0f;
                for (var j = from; j < to && j < peaks.Count; j++) peak = Math.Max(peak, peaks[j]);
                values[i] = (int)Math.Round(Math.Clamp(peak / max, 0, 1) * 31);
            }
        }
        var bytes = new byte[(bars * 5 + 7) / 8];
        for (var i = 0; i < bars; i++)
        {
            var bit = i * 5;
            var v = values[i] & 31;
            bytes[bit / 8] |= (byte)(v << (bit % 8));
            if (bit % 8 > 3 && bit / 8 + 1 < bytes.Length) bytes[bit / 8 + 1] |= (byte)(v >> (8 - bit % 8));
        }
        return bytes;
    }

    public void Dispose()
    {
        StopPlayback();
        StopCapture();
    }
}
