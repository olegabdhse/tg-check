namespace TgCheck.Core.Media;

/// <summary>
/// Обёртка «без перемотки»: библиотека Telegram тогда пишет файл строго по порядку (без пропусков),
/// и его можно читать, пока он качается.
/// </summary>
internal sealed class SequentialStream(Stream inner) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        inner.Flush(); // сразу на диск — читающий поток видит новые данные
    }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
        await inner.FlushAsync(ct);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        await inner.FlushAsync(ct);
    }
}

/// <summary>
/// Чтение файла, который ещё качается: длина — полный размер; если читаем дальше скачанного — ждём новые данные
/// (или конца загрузки). Перемотка вперёд за скачанное тоже ждёт.
/// </summary>
internal sealed class GrowingFileStream : Stream
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
        while (_file.Length < _file.Position + want && !_done.IsCompleted) Thread.Sleep(30);
        return _file.Read(buffer, offset, (int)Math.Min(want, Math.Max(0, _file.Length - _file.Position)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
