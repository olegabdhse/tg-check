using System.IO;
using System.Media;

namespace TgCheck.App.Helpers;

/// <summary>Звук уведомления: системный «Windows Notify Messaging», если есть, иначе стандартный сигнал.</summary>
public static class Notifier
{
    private static readonly Lazy<SoundPlayer?> Player = new(() =>
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Messaging.wav");
        if (!File.Exists(path)) return null;
        var player = new SoundPlayer(path);
        try { player.Load(); } catch { return null; }
        return player;
    });

    private static DateTime _lastPlayed;

    public static void PlaySound()
    {
        // Пачка сообщений подряд — один звук.
        if ((DateTime.UtcNow - _lastPlayed).TotalSeconds < 1.5) return;
        _lastPlayed = DateTime.UtcNow;
        try
        {
            if (Player.Value is { } p) p.Play();
            else SystemSounds.Asterisk.Play();
        }
        catch { /* звук не критичен */ }
    }
}
