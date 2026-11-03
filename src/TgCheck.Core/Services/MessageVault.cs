using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

/// <summary>
/// Хранилище сообщений, которые программа уже видела: Telegram сообщает об удалении только номером, поэтому содержимое
/// нужно сохранить заранее. Лежит вне кэша: его очистка удалённые и правки не трогает. Сообщения и старые тексты зашифрованы
/// DPAPI, как и кэш переписки. Запись идёт одним фоновым потоком пачками, чтобы не задерживать поток обновлений.
/// </summary>
internal sealed class MessageVault : IDisposable
{
    public sealed record Stored(Message Message, DateTime? DeletedAtUtc);
    public sealed record Edit(DateTime AtUtc, string Text);
    public sealed record PeerCount(long PeerKey, int Deleted, int Edited);

    private const long ChannelKeyBase = -1_000_000_000_000;
    private static readonly byte[] Entropy = "TgCheck.Vault.v1"u8.ToArray();
    // Обычные сообщения нужны, только пока их могут удалить; удалённые, правки и одноразовые лежат, пока их не очистят.
    private static readonly TimeSpan KeepSeen = TimeSpan.FromDays(30);
    private const int MaxBatch = 500;

    private readonly string _path;
    private readonly BlockingCollection<Action<SqliteConnection>> _queue = [];
    private readonly Thread _writer;
    private readonly SqliteConnection _read;
    private readonly SqliteConnection _write; // только в потоке записи
    private readonly object _readLock = new();
    private readonly List<Action> _afterCommit = []; // только в потоке записи

    public MessageVault(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var broken = SetAsideIfBroken();
        using (var setup = Open())
        {
            Run(setup, """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS messages(
                    peer INTEGER NOT NULL, id INTEGER NOT NULL, seen_at INTEGER NOT NULL, deleted_at INTEGER,
                    view_once INTEGER NOT NULL, doc_id INTEGER NOT NULL, photo_id INTEGER NOT NULL,
                    edit_date INTEGER NOT NULL, text_hash BLOB NOT NULL, blob BLOB NOT NULL, PRIMARY KEY(peer, id)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS messages_id ON messages(id);
                CREATE INDEX IF NOT EXISTS messages_deleted ON messages(peer, deleted_at) WHERE deleted_at IS NOT NULL;
                CREATE TABLE IF NOT EXISTS edits(peer INTEGER NOT NULL, id INTEGER NOT NULL, at INTEGER NOT NULL, blob BLOB NOT NULL);
                CREATE INDEX IF NOT EXISTS edits_message ON edits(peer, id);
                """);
            if (broken is not null) Salvage(broken, setup);
            Prune(setup);
        }
        _read = Open();
        _write = Open();
        Run(_write, "PRAGMA synchronous=FULL");
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "TgCheck.Vault" };
        _writer.Start();
    }

    private SqliteConnection Open(bool readOnly = false, string? path = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path ?? _path,
            Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        return connection;
    }

    // База могла пострадать (сбой диска, внезапное выключение): программа не должна из-за этого остаться без удалённых навсегда.
    // Повреждённый файл откладывается в сторону, а новая база создаётся заново.
    private string? SetAsideIfBroken()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            using var check = Open();
            using var command = check.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1)";
            if (command.ExecuteScalar() as string == "ok") return null;
        }
        catch (SqliteException ex) { Logging.Write("vault: проверка базы: " + ex.Message); }
        var broken = _path + ".broken";
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(broken + suffix);
            if (File.Exists(_path + suffix)) File.Move(_path + suffix, broken + suffix);
        }
        Logging.Write("vault: база повреждена, отложена в сторону; переносим то, что читается");
        return broken;
    }

    // Переносим всё, что ещё читается, до первой испорченной страницы. Обычные сообщения не нужны: они придут снова.
    private static void Salvage(string broken, SqliteConnection target)
    {
        int messages = 0, edits = 0;
        try
        {
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = broken, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
            source.Open();
            Run(target, "BEGIN");
            try
            {
                using (var read = source.CreateCommand())
                {
                    read.CommandText = "SELECT peer, id, seen_at, deleted_at, view_once, doc_id, photo_id, edit_date, text_hash, blob FROM messages WHERE deleted_at IS NOT NULL OR view_once=1";
                    using var row = read.ExecuteReader();
                    while (row.Read())
                    {
                        using var save = Command(target, "INSERT OR IGNORE INTO messages VALUES($a,$b,$c,$d,$e,$f,$g,$h,$i,$j)",
                            ("$a", row[0]), ("$b", row[1]), ("$c", row[2]), ("$d", row[3]), ("$e", row[4]), ("$f", row[5]), ("$g", row[6]), ("$h", row[7]), ("$i", row[8]), ("$j", row[9]));
                        save.ExecuteNonQuery();
                        messages++;
                    }
                }
            }
            catch (SqliteException ex) { Logging.Write("vault: удалённые перенесены не все: " + ex.Message); }
            try
            {
                using var read = source.CreateCommand();
                read.CommandText = "SELECT peer, id, at, blob FROM edits";
                using var row = read.ExecuteReader();
                while (row.Read())
                {
                    using var save = Command(target, "INSERT INTO edits(peer, id, at, blob) VALUES($a,$b,$c,$d)", ("$a", row[0]), ("$b", row[1]), ("$c", row[2]), ("$d", row[3]));
                    save.ExecuteNonQuery();
                    edits++;
                }
            }
            catch (SqliteException ex) { Logging.Write("vault: правки перенесены не все: " + ex.Message); }
            Run(target, "COMMIT");
        }
        catch (SqliteException ex) { Logging.Write("vault: повреждённая база не читается: " + ex.Message); }
        Logging.Write($"vault: восстановлено удалённых и одноразовых {messages}, правок {edits}");
    }

    private static void Run(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void WriteLoop()
    {
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            try
            {
                Run(_write, "BEGIN");
                RunOne(first, _write);
                for (var i = 1; i < MaxBatch && _queue.TryTake(out var next); i++) RunOne(next, _write);
                Run(_write, "COMMIT");
            }
            catch (Exception ex)
            {
                Logging.Write("vault: " + ex.Message);
                try { Run(_write, "ROLLBACK"); } catch (SqliteException) { /* транзакции уже нет */ }
            }
            // Ждущие продолжают только после фиксации: иначе чтение следом за записью её бы ещё не увидело.
            foreach (var done in _afterCommit) done();
            _afterCommit.Clear();
        }
    }

    // Одна неудачная запись не должна отменять всю пачку.
    private static void RunOne(Action<SqliteConnection> action, SqliteConnection connection)
    {
        try { action(connection); }
        catch (Exception ex) { Logging.Write("vault: " + ex.Message); }
    }

    /// <summary>Завершается, когда записано всё, что поставлено в очередь до этого вызова.</summary>
    public Task WhenWritten()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(_ => _afterCommit.Add(() => done.TrySetResult()));
        return done.Task;
    }

    private void Enqueue(Action<SqliteConnection> action)
    {
        try { _queue.Add(action); } catch (InvalidOperationException) { /* программа закрывается */ }
    }

    // ===================================================================== Запись

    /// <summary>Сообщение пришло от сервера: запомнить, а если текст изменился — записать прежний в историю правок.</summary>
    public void Seen(Message message)
    {
        if (IsExpired(message)) return; // одноразовое уже без файла — храним ту версию, где файл был
        var peer = PeerRef.KeyOf(message.peer_id);
        var seenAt = DateTime.UtcNow.Ticks;
        Enqueue(c =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(message.message ?? ""))[..8];
            var editDate = message.flags.HasFlag(Message.Flags.has_edit_date) ? message.edit_date.Ticks : 0;
            byte[]? oldHash = null, oldBlob = null;
            using (var find = Command(c, "SELECT text_hash, blob FROM messages WHERE peer=$p AND id=$i", ("$p", peer), ("$i", message.id)))
            using (var row = find.ExecuteReader())
                if (row.Read()) (oldHash, oldBlob) = ((byte[])row[0], (byte[])row[1]);

            if (oldHash is not null && !oldHash.AsSpan().SequenceEqual(hash) && Decode(oldBlob!) is { } old)
            {
                using var edit = Command(c, "INSERT INTO edits(peer, id, at, blob) VALUES($p, $i, $a, $b)",
                    ("$p", peer), ("$i", message.id), ("$a", editDate != 0 ? editDate : seenAt),
                    ("$b", ProtectedData.Protect(Encoding.UTF8.GetBytes(old.message ?? ""), Entropy, DataProtectionScope.CurrentUser)));
                edit.ExecuteNonQuery();
            }
            else if (oldHash is not null)
            {
                // Текст тот же: шифровать и переписывать незачем. Раз сервер его вернул — сообщение не удалено.
                using var touch = Command(c, "UPDATE messages SET seen_at=$s, deleted_at=NULL WHERE peer=$p AND id=$i", ("$s", seenAt), ("$p", peer), ("$i", message.id));
                touch.ExecuteNonQuery();
                return;
            }
            using var save = Command(c, """
                INSERT OR REPLACE INTO messages(peer, id, seen_at, deleted_at, view_once, doc_id, photo_id, edit_date, text_hash, blob)
                VALUES($p, $i, $s, NULL, $v, $d, $f, $e, $h, $b)
                """,
                ("$p", peer), ("$i", message.id), ("$s", seenAt), ("$v", IsViewOnce(message) ? 1 : 0),
                ("$d", (message.media as MessageMediaDocument)?.document?.ID ?? 0L), ("$f", (message.media as MessageMediaPhoto)?.photo?.ID ?? 0L),
                ("$e", editDate), ("$h", hash), ("$b", Encode(message)));
            save.ExecuteNonQuery();
        });
    }

    /// <summary>Отметить удалёнными. peer = null — личные чаты и обычные группы: там номера сообщений общие на весь аккаунт.</summary>
    public Task<IReadOnlyList<Message>> MarkDeletedAsync(long? peer, IReadOnlyCollection<int> ids)
    {
        var done = new TaskCompletionSource<IReadOnlyList<Message>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var at = DateTime.UtcNow.Ticks;
        Enqueue(c =>
        {
            var found = new List<Message>();
            _afterCommit.Add(() => done.TrySetResult(found));
            foreach (var id in ids)
            {
                using (var mark = Command(c, $"UPDATE messages SET deleted_at=$a WHERE id=$i AND deleted_at IS NULL AND {(peer is null ? "peer>$b" : "peer=$p")}",
                    ("$a", at), ("$i", id), ("$b", ChannelKeyBase), ("$p", peer ?? 0)))
                    if (mark.ExecuteNonQuery() == 0) continue;
                using var read = Command(c, $"SELECT blob FROM messages WHERE id=$i AND {(peer is null ? "peer>$b" : "peer=$p")}", ("$i", id), ("$b", ChannelKeyBase), ("$p", peer ?? 0));
                if (read.ExecuteScalar() is byte[] blob && Decode(blob) is { } message) found.Add(message);
            }
        });
        return done.Task;
    }

    /// <summary>Историю чата очистили здесь: всё, что не удалено другими, в чат не вернётся.</summary>
    public void ForgetLive(long peer) => Enqueue(c =>
    {
        using var forget = Command(c, "DELETE FROM messages WHERE peer=$p AND deleted_at IS NULL", ("$p", peer));
        forget.ExecuteNonQuery();
    });

    /// <summary>Очистить удалённые и правки выбранных чатов.</summary>
    public Task ClearAsync(IReadOnlyCollection<long> peers)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(c =>
        {
            _afterCommit.Add(() => done.TrySetResult());
            foreach (var peer in peers)
            {
                using (var messages = Command(c, "DELETE FROM messages WHERE peer=$p AND deleted_at IS NOT NULL", ("$p", peer))) messages.ExecuteNonQuery();
                using (var edits = Command(c, "DELETE FROM edits WHERE peer=$p", ("$p", peer))) edits.ExecuteNonQuery();
            }
        });
        return done.Task;
    }

    /// <summary>Выход из аккаунта: ничего от него не остаётся.</summary>
    public void Wipe() => Enqueue(c =>
    {
        Run(c, "DELETE FROM messages");
        Run(c, "DELETE FROM edits");
    });

    private static void Prune(SqliteConnection c)
    {
        using var prune = Command(c, "DELETE FROM messages WHERE deleted_at IS NULL AND view_once=0 AND seen_at<$t", ("$t", (DateTime.UtcNow - KeepSeen).Ticks));
        prune.ExecuteNonQuery();
    }

    // ===================================================================== Чтение

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T?> map, params (string Name, object? Value)[] args) where T : class
    {
        var result = new List<T>();
        lock (_readLock)
        {
            using var command = Command(_read, sql, args);
            using var row = command.ExecuteReader();
            while (row.Read())
                if (map(row) is { } item) result.Add(item);
        }
        return result;
    }

    /// <summary>Удалённые сообщения чата в диапазоне номеров.</summary>
    public IReadOnlyList<Stored> Deleted(long peer, int minId, int maxId) =>
        Query("SELECT blob, deleted_at FROM messages WHERE peer=$p AND deleted_at IS NOT NULL AND id>=$a AND id<=$b", ToStored,
            ("$p", peer), ("$a", minId), ("$b", maxId));

    /// <summary>Все удалённые чата, сначала недавно удалённые.</summary>
    public IReadOnlyList<Stored> DeletedLatest(long peer, int limit) =>
        Query("SELECT blob, deleted_at FROM messages WHERE peer=$p AND deleted_at IS NOT NULL ORDER BY deleted_at DESC, id DESC LIMIT $l", ToStored, ("$p", peer), ("$l", limit));

    public Message? Find(long peer, int id) =>
        Query("SELECT blob FROM messages WHERE peer=$p AND id=$i", row => Decode((byte[])row[0]), ("$p", peer), ("$i", id)).FirstOrDefault();

    /// <summary>Какие сообщения диапазона правили.</summary>
    public HashSet<int> EditedIds(long peer, int minId, int maxId) =>
        Query("SELECT DISTINCT id FROM edits WHERE peer=$p AND id>=$a AND id<=$b", row => (object)row.GetInt32(0), ("$p", peer), ("$a", minId), ("$b", maxId))
            .Cast<int>().ToHashSet();

    /// <summary>Прежние тексты, сначала последняя правка.</summary>
    public IReadOnlyList<Edit> Edits(long peer, int id) =>
        Query("SELECT at, blob FROM edits WHERE peer=$p AND id=$i ORDER BY at DESC, rowid DESC", row =>
        {
            try { return new Edit(new DateTime(row.GetInt64(0), DateTimeKind.Utc), Encoding.UTF8.GetString(ProtectedData.Unprotect((byte[])row[1], Entropy, DataProtectionScope.CurrentUser))); }
            catch (CryptographicException) { return null; }
        }, ("$p", peer), ("$i", id));

    /// <summary>Чаты, где что-то хранится: удалённые и правленые сообщения.</summary>
    public IReadOnlyList<PeerCount> Counts() =>
        Query("""
            SELECT peer, SUM(deleted), SUM(edited) FROM (
                SELECT peer, COUNT(*) AS deleted, 0 AS edited FROM messages WHERE deleted_at IS NOT NULL GROUP BY peer
                UNION ALL SELECT peer, 0, COUNT(DISTINCT id) FROM edits GROUP BY peer)
            GROUP BY peer
            """, row => new PeerCount(row.GetInt64(0), row.GetInt32(1), row.GetInt32(2)));

    /// <summary>Файлы удалённых и одноразовых: кэш не должен их выбрасывать.</summary>
    public (HashSet<long> Docs, HashSet<long> Photos) KeptMedia()
    {
        var docs = new HashSet<long>();
        var photos = new HashSet<long>();
        Query("SELECT doc_id, photo_id FROM messages WHERE (deleted_at IS NOT NULL OR view_once=1) AND (doc_id<>0 OR photo_id<>0)", row =>
        {
            if (row.GetInt64(0) != 0) docs.Add(row.GetInt64(0));
            if (row.GetInt64(1) != 0) photos.Add(row.GetInt64(1));
            return (object?)null;
        });
        return (docs, photos);
    }

    private static Stored? ToStored(SqliteDataReader row) =>
        Decode((byte[])row[0]) is { } message ? new Stored(message, row.IsDBNull(1) ? null : new DateTime(row.GetInt64(1), DateTimeKind.Utc)) : null;

    // ===================================================================== Формат

    internal static bool IsViewOnce(Message m) => m.media switch
    {
        MessageMediaPhoto p => p.flags.HasFlag(MessageMediaPhoto.Flags.has_ttl_seconds),
        MessageMediaDocument d => d.flags.HasFlag(MessageMediaDocument.Flags.has_ttl_seconds),
        _ => false,
    };

    /// <summary>Одноразовое, файл которого сервер уже убрал.</summary>
    internal static bool IsExpired(Message m) => m.media is MessageMediaPhoto { photo: null } or MessageMediaDocument { document: null };

    private static byte[] Encode(Message message)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            writer.WriteTLObject(message);
        return ProtectedData.Protect(ms.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }

    private static Message? Decode(byte[] blob)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser)));
            return reader.ReadTLObject() as Message;
        }
        catch (Exception ex)
        {
            Logging.Write("vault: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (_writer.Join(TimeSpan.FromSeconds(3))) _write.Dispose();
        lock (_readLock) _read.Dispose();
    }
}
