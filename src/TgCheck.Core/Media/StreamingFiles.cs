namespace TgCheck.Core.Media;

/// <summary>
/// Файл для загрузки, который можно читать на ходу: библиотека Telegram пишет куски параллельно и вразнобой
/// (с перемоткой), а на диск они ложатся строго подряд — пришедшие раньше времени ждут в памяти своей очереди.
/// Так у читающего (песня, которая играет во время загрузки) никогда нет «дыр». Раньше была обёртка без перемотки —
/// библиотека с ней писала уже после закрытия файла, и загрузка песни падала.
/// </summary>
internal sealed class OrderedFileStream(FileStream file) : Stream
{
    private readonly object _lock = new();
    private readonly SortedDictionary<long, byte[]> _pending = [];
    private long _position, _flushed;

    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length { get { lock (_lock) return Math.Max(_flushed, _pending.Count == 0 ? 0 : _pending.Max(p => p.Key + p.Value.Length)); } }
    public override long Position { get { lock (_lock) return _position; } set { lock (_lock) _position = value; } }
    public override void Flush() { lock (_lock) file.Flush(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) { }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_lock)
            return _position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => Length + offset };
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            var at = _position;
            _position += count;
            if (at + count <= _flushed) return; // уже записано (повтор)
            if (at > _flushed)
            {
                _pending[at] = buffer.AsSpan(offset, count).ToArray(); // пришло раньше своей очереди — подождёт
                return;
            }
            var skip = (int)(_flushed - at);
            file.Write(buffer, offset + skip, count - skip);
            _flushed += count - skip;
            // Подтягиваем дождавшиеся куски.
            while (_pending.Count > 0)
            {
                var first = _pending.First();
                if (first.Key > _flushed) break;
                _pending.Remove(first.Key);
                var tail = (int)(_flushed - first.Key);
                if (tail < first.Value.Length)
                {
                    file.Write(first.Value, tail, first.Value.Length - tail);
                    _flushed += first.Value.Length - tail;
                }
            }
            file.Flush(); // сразу на диск — читающий видит новые данные
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Write(buffer.ToArray(), 0, buffer.Length);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        // Файл закрывает владелец; здесь только дописываем всё, что успело прийти.
        if (disposing) lock (_lock) { foreach (var (at, data) in _pending) { file.Position = at; file.Write(data); } _pending.Clear(); file.Flush(); }
        base.Dispose(disposing);
    }
}
/// <summary>
/// Чтение файла, который ещё качается: длина — полный размер; если читаем дальше скачанного — ждём новые данные
/// (или конца загрузки). Перемотка вперёд за скачанное тоже ждёт.
/// </summary>
public sealed class GrowingFileStream : Stream, IStreamingSource
{
    private readonly FileStream _file;
    private readonly long _total;
    private readonly Task<string?> _done;

    public GrowingFileStream(string path, long total, Task<string?> done)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _total = total;
        _done = done;
    }

    /// <summary>Ждёт ещё не скачанные данные.</summary>
    public bool IsWaiting { get; private set; }

    /// <summary>Конец загрузки: путь к готовому файлу (null — не скачалось).</summary>
    public Task<string?> Done => _done;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _total;
    public override long Position { get => _file.Position; set => _file.Position = value; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) =>
        origin == SeekOrigin.End ? _file.Seek(_total + offset, SeekOrigin.Begin) : _file.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var want = Math.Min(count, _total - _file.Position);
        if (want <= 0) return 0;
        // Ждём, пока нужное докачается (или загрузка закончится — тогда отдаём что есть).
        while (_file.Length < _file.Position + want && !_done.IsCompleted)
        {
            IsWaiting = true; // проигрыватель догнал загрузку — в плеере показывается «загрузка»
            Thread.Sleep(30);
        }
        IsWaiting = false;
        return _file.Read(buffer, offset, (int)Math.Min(want, Math.Max(0, _file.Length - _file.Position)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Последние картинки в памяти (LRU) с потолком по размеру: старые вытесняются.</summary>
internal sealed class BytesCache(long capacity)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Bytes)>> _map = [];
    private readonly LinkedList<(string Key, byte[] Bytes)> _order = new();
    private long _size;

    public byte[]? Get(string key)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Bytes;
        }
    }

    public void Clear()
    {
        lock (_lock) { _map.Clear(); _order.Clear(); _size = 0; }
    }

    public void Put(string key, byte[] bytes)
    {
        if (bytes.Length > capacity / 8) return; // огромное не кэшируем — вытеснило бы всё
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var old))
            {
                _order.Remove(old);
                _size -= old.Value.Bytes.Length;
            }
            _map[key] = _order.AddFirst((key, bytes));
            _size += bytes.Length;
            while (_size > capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _size -= last.Value.Bytes.Length;
            }
        }
    }
}

/// <summary>Ссылка на файл в Telegram устарела — нужна свежая (сообщение или профиль запросить заново).</summary>
internal sealed class FileReferenceExpiredException() : Exception("FILE_REFERENCE_EXPIRED");
