using System.Text.Json;

namespace TgCheck.Core.Services;

/// <summary>
/// Режим призрака: что не отправляется в Telegram. Настройки читаются с диска при первом обращении, до подключения:
/// после перезапуска программы статус «в сети» не уйдёт даже случайно.
/// </summary>
public static class GhostMode
{
    private sealed class Data
    {
        public bool Enabled { get; set; }
        public bool ShowRead { get; set; }
        public bool ShowTyping { get; set; }
        public bool ShowListened { get; set; }
        public bool ShowStoryViews { get; set; }
        public bool SendNow { get; set; }
        public bool HideDelayNotice { get; set; }
    }

    /// <summary>Через сколько сервер публикует отложенное: файлы и видео обрабатываются дольше.</summary>
    public static readonly TimeSpan MessageDelay = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan FileDelay = TimeSpan.FromSeconds(30);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck", "ghost.json");
    private static readonly Data Current = Load();

    /// <summary>Включили или выключили режим.</summary>
    public static event Action? Changed;

    /// <summary>«В сети» не отправляется никогда: этого в режиме призрака не отключить.</summary>
    public static bool Enabled
    {
        get => Current.Enabled;
        set
        {
            if (Current.Enabled == value) return;
            Current.Enabled = value;
            Save();
            Changed?.Invoke();
        }
    }

    public static bool HideRead { get => !Current.ShowRead; set { Current.ShowRead = !value; Save(); } }
    public static bool HideTyping { get => !Current.ShowTyping; set { Current.ShowTyping = !value; Save(); } }
    public static bool HideListened { get => !Current.ShowListened; set { Current.ShowListened = !value; Save(); } }
    public static bool HideStoryViews { get => !Current.ShowStoryViews; set { Current.ShowStoryViews = !value; Save(); } }

    /// <summary>Отправлять отложенно: сообщение публикует сам сервер, а обычная отправка включила бы «в сети».</summary>
    public static bool DelaySends { get => !Current.SendNow; set { Current.SendNow = !value; Save(); } }

    /// <summary>Показывать при включении окно об отложенной отправке.</summary>
    public static bool ShowDelayNotice { get => !Current.HideDelayNotice; set { Current.HideDelayNotice = !value; Save(); } }

    internal static bool DelaysNow => Enabled && DelaySends;
    internal static bool BlocksRead => Enabled && HideRead;
    internal static bool BlocksTyping => Enabled && HideTyping;
    internal static bool BlocksListened => Enabled && HideListened;
    internal static bool BlocksStoryViews => Enabled && HideStoryViews;

    private static Data Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex)
        {
            Logging.Write("ghost: " + ex.Message);
            return new();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current));
        }
        catch (Exception ex) { Logging.Write("ghost: " + ex.Message); }
    }
}
