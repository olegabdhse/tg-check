using System.IO;
using System.Text.Json;
using System.Windows;

namespace TgCheck.App.Helpers;

/// <summary>Небольшие настройки интерфейса (положение мини-окна и т.п.) в %LOCALAPPDATA%\TgCheck\ui.json.</summary>
public static class UiSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck", "ui.json");

    private sealed class Data
    {
        public double? MiniLeft { get; set; }
        public double? MiniTop { get; set; }
        public bool HideIsland { get; set; }
        public bool NotificationsOff { get; set; }
        public bool SoundOff { get; set; }
        public bool ArchiveNotificationsOff { get; set; }
        public bool HideStories { get; set; }
        public string? IslandEdge { get; set; }
        public List<long>? RecentSearch { get; set; }
        public double? IslandAlong { get; set; }
    }

    private static Data _data = Load();

    /// <summary>Сохранённое положение мини-окна, если оно всё ещё на экране.</summary>
    public static Point? MiniChatPosition
    {
        get
        {
            if (_data.MiniLeft is not { } l || _data.MiniTop is not { } t) return null;
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            return screen.Contains(new Point(l + 100, t + 40)) ? new Point(l, t) : null;
        }
        set
        {
            _data.MiniLeft = value?.X;
            _data.MiniTop = value?.Y;
            Save();
        }
    }

    /// <summary>Островок с чатами при сворачивании окна.</summary>
    public static bool ShowIsland { get => !_data.HideIsland; set { _data.HideIsland = !value; Save(); } }

    /// <summary>Всплывающие уведомления о новых сообщениях.</summary>
    public static bool Notifications { get => !_data.NotificationsOff; set { _data.NotificationsOff = !value; Save(); } }

    /// <summary>Звук уведомления.</summary>
    public static bool NotificationSound { get => !_data.SoundOff; set { _data.SoundOff = !value; Save(); } }

    /// <summary>Сторона экрана, к которой притянут островок (Top, Bottom, Left, Right), и место вдоль неё (0..1).</summary>
    public static (string Edge, double Along) IslandPlace
    {
        get => (_data.IslandEdge ?? "Top", _data.IslandAlong ?? 0.5);
        set { _data.IslandEdge = value.Edge; _data.IslandAlong = value.Along; Save(); }
    }

    /// <summary>История поиска: чаты, открытые из поиска (новые первыми).</summary>
    public static IReadOnlyList<long> RecentSearch
    {
        get => _data.RecentSearch ?? [];
        set { _data.RecentSearch = [.. value]; Save(); }
    }

    /// <summary>Полоса историй над списком чатов.</summary>
    public static bool ShowStories { get => !_data.HideStories; set { _data.HideStories = !value; Save(); } }

    /// <summary>Уведомлять и о чатах из архива (по умолчанию — да: в Telegram архив на уведомления не влияет, молчат только чаты без звука).</summary>
    public static bool NotifyArchived { get => !_data.ArchiveNotificationsOff; set { _data.ArchiveNotificationsOff = !value; Save(); } }

    private static Data Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data));
        }
        catch { /* не критично */ }
    }
}
