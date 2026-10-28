using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.Wave;

namespace TgCheck.Core.Media;

/// <summary>Готовое голосовое: файл Ogg/Opus, длительность и волна (как требует Telegram).</summary>
public sealed record VoiceRecording(string Path, double DurationSeconds, byte[] Waveform);

/// <summary>
/// Запись голосового с микрофона: 48 кГц моно → Opus в Ogg (тот же формат, что у официальных клиентов),
/// попутно собирается громкость для волны и индикатора записи.
/// </summary>
public sealed class VoiceRecorder : IDisposable
{
    private const int SampleRate = 48000;
    private WaveInEvent? _input;
    private OpusOggWriteStream? _ogg;
    private FileStream? _file;
    private string? _path;
    private long _samples;
    private readonly List<float> _peaks = [];
    private float _blockPeak;
    private int _blockSamples;
    private readonly object _lock = new();

    public bool IsRecording => _input is not null;
    public double Seconds => (double)Interlocked.Read(ref _samples) / SampleRate;

    /// <summary>Текущая громкость 0..1 — для «пульсации» кнопки записи.</summary>
    public float Level { get; private set; }

    public void Start()
    {
        if (_input is not null) return;
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TgCheck", "voice");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, $"voice_{DateTime.Now:yyyyMMdd_HHmmss}.ogg");
        _file = File.Create(_path);
        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 32000;
        _ogg = new OpusOggWriteStream(encoder, _file);
        _samples = 0;
        _peaks.Clear();
        _blockPeak = 0;
        _blockSamples = 0;

        _input = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 60 };
        _input.DataAvailable += OnData;
        _input.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var count = e.BytesRecorded / 2;
        var samples = new short[count];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, count * 2);
        lock (_lock)
        {
            if (_ogg is null) return;
            _ogg.WriteSamples(samples, 0, count);
            var level = 0f;
            foreach (var s in samples)
            {
                var a = Math.Abs(s / 32768f);
                if (a > level) level = a;
                if (a > _blockPeak) _blockPeak = a;
                // Пик каждые ~50 мс — из них потом строится волна из 100 столбиков.
                if (++_blockSamples >= SampleRate / 20)
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

    /// <summary>Остановить; null — запись слишком короткая или отменена.</summary>
    public VoiceRecording? Stop(bool cancel = false)
    {
        if (_input is null) return null;
        _input.DataAvailable -= OnData;
        try { _input.StopRecording(); } catch { }
        _input.Dispose();
        _input = null;
        lock (_lock)
        {
            try { _ogg?.Finish(); } catch { }
            _ogg = null;
            _file?.Dispose();
            _file = null;
        }
        Level = 0;
        var seconds = Seconds;
        if (cancel || seconds < 0.7 || _path is null)
        {
            try { if (_path is not null) File.Delete(_path); } catch { }
            return null;
        }
        return new VoiceRecording(_path, seconds, EncodeWaveform(_peaks));
    }

    // Волна Telegram: 100 значений по 5 бит, упакованных подряд (младшие биты первыми).
    private static byte[] EncodeWaveform(List<float> peaks)
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

    public void Dispose() => Stop(cancel: true);
}
