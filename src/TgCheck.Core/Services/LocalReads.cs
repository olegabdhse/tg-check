using System.Text.Json;

namespace TgCheck.Core.Services;

/// <summary>
/// Прочитанное в режиме призрака: сервер об этом не знает и после сверки снова прислал бы прежний счётчик непрочитанных,
/// поэтому отметки лежат на диске. Для каждого чата — до какого сообщения прочитано и сколько из серверного счётчика этим закрыто.
/// </summary>
internal sealed class LocalReads(string file)
{
    private sealed record Entry(int MaxId, int Hidden);

    private readonly object _lock = new();
    private Dictionary<long, Entry>? _entries;

    private Dictionary<long, Entry> Entries
    {
        get
        {
            if (_entries is not null) return _entries;
            try { _entries = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<long, Entry>>(File.ReadAllText(file)) : null; }
            catch (Exception ex) { Logging.Write("local reads: " + ex.Message); }
            return _entries ??= [];
        }
    }

    public void Note(long peer, int maxId, int nowRead)
    {
        lock (_lock)
        {
            var old = Entries.GetValueOrDefault(peer);
            Entries[peer] = new Entry(Math.Max(maxId, old?.MaxId ?? 0), (old?.Hidden ?? 0) + Math.Max(0, nowRead));
            Save();
        }
    }

    /// <summary>Счётчик и граница прочитанного с учётом отметок. Сервер уже знает о прочтении (другое устройство, призрак выключен) — отметка больше не нужна.</summary>
    public (int Unread, int ReadInboxMaxId) Apply(long peer, int topMessage, int unread, int readInboxMaxId)
    {
        lock (_lock)
        {
            if (!Entries.TryGetValue(peer, out var entry)) return (unread, readInboxMaxId);
            if (readInboxMaxId >= entry.MaxId)
            {
                Entries.Remove(peer);
                Save();
                return (unread, readInboxMaxId);
            }
            return (topMessage <= entry.MaxId ? 0 : Math.Max(0, unread - entry.Hidden), entry.MaxId);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Entries.Clear();
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(Entries));
        }
        catch (Exception ex) { Logging.Write("local reads: " + ex.Message); }
    }
}
