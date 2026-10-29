using WTelegram;

namespace TgCheck.Core.Services;

/// <summary>Пишет предупреждения и ошибки WTelegram и приложения в файл logs\tgcheck.log.</summary>
public static class Logging
{
    private const long MaxSize = 5 * 1024 * 1024;
    private static readonly object Lock = new();
    private static string? _file;

    public static void Init(string dir)
    {
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "tgcheck.log");
        lock (Lock) { try { File.AppendAllLines(_file, Pending); } catch { } Pending.Clear(); } // записанное до инициализации
        // 0..1 — отладочный шум, 2 — info, 3+ — предупреждения/ошибки.
        Helpers.Log = (level, message) => { if (level >= 3) Write(message); };
    }

    /// <summary>Замер времени от запуска процесса (для поиска медленных мест).</summary>
    public static void Perf(string what) =>
        Write($"[perf] {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds,6:0} ms  {what}");

    private static readonly List<string> Pending = []; // до Init (замеры запуска) — потом дописываются в файл

    public static void Write(string message)
    {
        if (_file is null) { lock (Lock) if (Pending.Count < 200) Pending.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}"); return; }
        lock (Lock)
        {
            try
            {
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > MaxSize) File.Move(_file, _file + ".old", overwrite: true);
                File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch { /* логирование не должно ронять приложение */ }
        }
    }
}
