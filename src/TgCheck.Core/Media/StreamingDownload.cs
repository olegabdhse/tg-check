using TgCheck.Core.Services;
using TL;

namespace TgCheck.Core.Media;

/// <summary>Источник, который ещё качается: проигрыватель показывает «загрузку» и закрывается, если файл не скачался.</summary>
public interface IStreamingSource
{
    /// <summary>Ждёт ещё не скачанные данные.</summary>
    bool IsWaiting { get; }

    /// <summary>Конец загрузки: путь к готовому файлу (null — не скачалось).</summary>
    Task<string?> Done { get; }
}

/// <summary>
/// Загрузка файла по требованию проигрывателя — как потоковое воспроизведение в Telegram Desktop. Файл делится на кусочки
/// по 128 КБ: то, что проигрыватель читает прямо сейчас (начало, служебный блок в конце m4a, место перемотки), качается
/// первым и отдельно; остальное — следом, от текущей позиции, блоками по 1 МБ (меньше запросов — выше скорость).
/// Песня начинает играть, как только пришли нужные кусочки, а не после всего файла.
/// </summary>
internal sealed class StreamingDownload
{
    private const int Unit = 128 * 1024;            // кусочек, который можно запросить отдельно
    private const int Block = 1024 * 1024;          // крупный запрос (8 кусочков) — когда спешить некуда
    private const int UnitsPerBlock = Block / Unit;
    private const int Parallel = 12;

    private readonly Func<WTelegram.Client> _client;
    private readonly Func<Task<Document?>> _refresh;
    private readonly Func<bool> _shouldYield;
    private readonly Func<bool> _cancelled;
    private readonly string _part, _final;
    private readonly long _size;
    private readonly bool[] _have, _inflight;
    private readonly Stack<int> _urgent = new();
    private readonly object _lock = new();
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _wake = new(0);
    private FileStream? _file;
    private InputDocumentFileLocation _location;
    private int _dcId;
    private int _cursor, _left;
    private int _readers;
    private volatile bool _keep;
    private bool _failed;

    public StreamingDownload(Func<WTelegram.Client> client, Document doc, string part, string final,
        Func<Task<Document?>> refresh, Func<bool> shouldYield, Func<bool> cancelled)
    {
        _client = client;
        _refresh = refresh;
        _shouldYield = shouldYield;
        _cancelled = cancelled;
        _part = part;
        _final = final;
        _size = doc.size;
        _dcId = doc.dc_id;
        _location = Location(doc);
        var units = (int)((_size + Unit - 1) / Unit);
        _have = new bool[units];
        _inflight = new bool[units];
        _left = units;
    }

    public long Size => _size;
    public Task<string?> Done => _done.Task;
    public string PartPath => _part;

    private static InputDocumentFileLocation Location(Document d) =>
        new() { id = d.id, access_hash = d.access_hash, file_reference = d.file_reference, thumb_size = "" };

    public void Start()
    {
        _file = new FileStream(_part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _file.SetLength(_size);
        _ = Task.Run(RunAsync);
    }

    // ---- для читателя

    // Читатели (проигрыватели). Никто не читает — загрузка на паузе: при листании историй брошенные загрузки
    // иначе продолжали качаться и забирали скорость у текущей. Вернулись — продолжается с того же места.
    public void AddReader() { Interlocked.Increment(ref _readers); _wake.Release(); }
    public void RemoveReader() => Interlocked.Decrement(ref _readers);

    /// <summary>Сейчас кто-то читает и файл ещё не докачан.</summary>
    public bool IsReading => Volatile.Read(ref _readers) > 0 && !_done.Task.IsCompleted;

    /// <summary>Файл нужен целиком (сохранить, открыть) — качать до конца, даже если его никто не слушает.</summary>
    public void Keep() { _keep = true; _wake.Release(); }

    // Предзагрузка следующей песни: сколько кусочков с начала (и последний — служебный блок m4a) качать, пока никто не слушает.
    private int _prefetchUnits;

    /// <summary>Тихо подкачать первые bytes байт (и конец файла), даже если никто не слушает; не больше двух запросов разом.</summary>
    public void Prefetch(long bytes)
    {
        var units = (int)Math.Min(_have.Length, (bytes + Unit - 1) / Unit);
        lock (_lock) if (units > _prefetchUnits) _prefetchUnits = units;
        _wake.Release();
    }

    // Следующий кусочек для предзагрузки (под замком); null — всё нужное уже есть или качается.
    private int? PrefetchUnit()
    {
        if (_prefetchUnits == 0) return null;
        var inflight = 0;
        for (var i = 0; i < _inflight.Length; i++) if (_inflight[i]) inflight++;
        if (inflight >= 2) return null;
        for (var i = 0; i < _prefetchUnits; i++)
            if (!_have[i] && !_inflight[i]) return i;
        var last = _have.Length - 1;
        return !_have[last] && !_inflight[last] ? last : null;
    }

    private bool Idle => Volatile.Read(ref _readers) <= 0 && !_keep;


    /// <summary>Есть ли кусочек с этим смещением; если нет — он идёт в загрузку первым.</summary>
    public bool Ensure(long position)
    {
        var unit = (int)(position / Unit);
        lock (_lock)
        {
            if (unit >= _have.Length || _have[unit]) return true;
            _urgent.Push(unit);
            _cursor = unit;
        }
        _wake.Release();
        return false;
    }

    /// <summary>Сколько байт подряд уже есть начиная с position.</summary>
    public long Available(long position)
    {
        lock (_lock)
        {
            var unit = (int)(position / Unit);
            var end = unit;
            while (end < _have.Length && _have[end]) end++;
            return Math.Min(_size, (long)end * Unit) - position;
        }
    }

    /// <summary>Ждать новый кусочек (с ограничением по времени). false — загрузка провалилась.</summary>
    public bool WaitForData(int ms)
    {
        lock (_lock)
        {
            if (_failed) return false;
            Monitor.Wait(_lock, ms);
            return !_failed;
        }
    }

    // ---- загрузка

    private async Task RunAsync()
    {
        var slots = new SemaphoreSlim(Parallel);
        try
        {
            var dc = await _client().GetClientForDC(-_dcId, true);
            while (true)
            {
                if (_cancelled()) throw new OperationCanceledException();
                lock (_lock)
                    if (_left == 0 || _failed) break;
                // Видео истории в просмотрщике — ему вся скорость; срочное (проигрыватель ждёт) не откладываем.
                if (Idle)
                {
                    // Никто не слушает: только предзагрузка начала (если просили), иначе пауза.
                    if (_shouldYield()) { await _wake.WaitAsync(250); continue; } // предзагрузка — после превью и истории
                    int? unit;
                    lock (_lock) { unit = PrefetchUnit(); if (unit is { } u) _inflight[u] = true; }
                    if (unit is { } p) { _ = FetchAsync(dc, p, 1).ContinueWith(_ => _wake.Release(), TaskScheduler.Default); continue; }
                    await _wake.WaitAsync(250);
                    continue;
                }
                bool urgentWaiting;
                lock (_lock) urgentWaiting = _urgent.Count > 0;
                // Уступаем (смотрят историю, грузятся превью) — не встаём совсем, а качаем по одному запросу: раньше полная пауза
                // на всё время загрузки превью (в разделе музыки они грузятся почти всегда) «вешала» видео, кружки и песни.
                if (!urgentWaiting && _shouldYield() && slots.CurrentCount < Parallel) { await _wake.WaitAsync(100); continue; }

                await slots.WaitAsync();
                var request = Next();
                if (request is null)
                {
                    slots.Release();
                    await _wake.WaitAsync(200); // нечего просить, пока идут запросы, — ждём срочного или завершения
                    continue;
                }
                var (first, count) = request.Value;
                _ = FetchAsync(dc, first, count).ContinueWith(_ => { slots.Release(); _wake.Release(); }, TaskScheduler.Default);
            }
            lock (_lock)
                if (_failed) throw new IOException("часть файла не скачалась");
            await _file!.FlushAsync();
            _file.Dispose();
            File.Move(_part, _final, overwrite: true);
            _done.TrySetResult(_final);
        }
        catch (Exception ex)
        {
            Logging.Write($"stream {_location.id}: {ex.Message}");
            Fail();
        }
    }

    // Что просить дальше: срочное (то, что ждёт проигрыватель) — одним кусочком; иначе по порядку от позиции чтения,
    // целым мегабайтом, если он весь ещё не качается.
    private (int First, int Count)? Next()
    {
        lock (_lock)
        {
            while (_urgent.Count > 0)
            {
                var u = _urgent.Pop();
                if (_have[u] || _inflight[u]) continue;
                _inflight[u] = true;
                return (u, 1);
            }
            var start = FirstMissing(_cursor) ?? FirstMissing(0);
            if (start is not { } s) return null;
            if (s % UnitsPerBlock == 0 && s + UnitsPerBlock <= _have.Length
                && Enumerable.Range(s, UnitsPerBlock).All(i => !_have[i] && !_inflight[i]))
            {
                for (var i = s; i < s + UnitsPerBlock; i++) _inflight[i] = true;
                return (s, UnitsPerBlock);
            }
            _inflight[s] = true;
            return (s, 1);
        }
    }

    private int? FirstMissing(int from)
    {
        for (var i = from; i < _have.Length; i++)
            if (!_have[i] && !_inflight[i]) return i;
        return null;
    }

    private async Task FetchAsync(WTelegram.Client dc, int first, int count)
    {
        var offset = (long)first * Unit;
        var limit = count * Unit;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (await dc.Upload_GetFile(_location, offset, limit) is not Upload_File file) throw new IOException("файл отдаётся через CDN");
                lock (_lock)
                {
                    _file!.Position = offset;
                    _file.Write(file.bytes);
                    _file.Flush();
                    for (var i = first; i < first + count; i++)
                    {
                        _inflight[i] = false;
                        if (!_have[i] && (long)i * Unit < offset + file.bytes.Length) { _have[i] = true; _left--; }
                    }
                    Monitor.PulseAll(_lock); // читатель ждёт этот кусочек
                }
                return;
            }
            catch (RpcException ex) when (ex.Message.StartsWith("FILE_REFERENCE") && attempt == 1)
            {
                // Ссылка устарела (песня из кэша истории или профиля) — свежая и повтор.
                if (await _refresh() is { } fresh) { _location = Location(fresh); _dcId = fresh.dc_id; }
            }
            catch (Exception ex) when (attempt < 3 && ex is not OperationCanceledException)
            {
                await Task.Delay(500 * attempt);
            }
            catch (Exception ex)
            {
                Logging.Write($"stream part {first}: {ex.Message}");
                Fail();
                return;
            }
        }
    }

    private void Fail()
    {
        lock (_lock)
        {
            _failed = true;
            Monitor.PulseAll(_lock);
        }
        try { _file?.Dispose(); } catch { }
        try { File.Delete(_part); } catch { }
        _done.TrySetResult(null);
    }
}

/// <summary>Чтение песни, которая ещё качается: нужное место запрашивается первым, проигрыватель ждёт только его.</summary>
internal sealed class StreamingReadStream : Stream, IStreamingSource
{
    private readonly StreamingDownload _download;
    private readonly FileStream _file;
    private long _position;

    public StreamingReadStream(StreamingDownload download)
    {
        _download = download;
        download.AddReader();
        // Без буфера: кусочки дописываются в файл на ходу, буфер мог бы отдать старые нули вместо пришедших данных.
        _file = new FileStream(download.PartPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
    }

    public bool IsWaiting { get; private set; }
    public Task<string?> Done => _download.Done;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _download.Size;
    public override long Position { get => _position; set => _position = Math.Clamp(value, 0, Length); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var want = (int)Math.Min(count, Length - _position);
        if (want <= 0) return 0;
        // Нужного кусочка нет — он идёт в загрузку первым, ждём только его (проигрыватель показывает «загрузку»).
        while (!_download.Ensure(_position))
        {
            if (_closed) return 0; // проигрыватель останавливают — не ждём кусок
            if (_download.Done.IsCompleted && _download.Done.Result is null) return 0;
            IsWaiting = true;
            if (!_download.WaitForData(200)) { IsWaiting = false; return 0; }
        }
        IsWaiting = false;
        var available = (int)Math.Min(want, _download.Available(_position));
        if (available <= 0) return 0;
        if (_closed) return 0;
        int read;
        try
        {
            _file.Position = _position;
            read = _file.Read(buffer, offset, available);
        }
        catch (ObjectDisposedException) { return 0; } // закрыли, пока читали
        _position += read;
        return read;
    }

    private volatile bool _closed;

    protected override void Dispose(bool disposing)
    {
        if (!_closed) _download.RemoveReader();
        _closed = true;
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
