using System.IO;
using System.IO.Compression;
using LibVLCSharp.Shared;

namespace TgCheck.App.Helpers;

/// <summary>
/// Движок VLC для видео. Встроен в exe архивом; при первом обращении распаковывается в
/// %LOCALAPPDATA%\TgCheck\vlc\&lt;версия&gt; (один раз, ~45 МБ) и инициализируется.
/// </summary>
public static class VlcRuntime
{
    private const string Version = "3.0.21";
    private static Task<LibVLC?>? _init;

    /// <summary>Готовый движок (null — не удалось распаковать/загрузить).</summary>
    public static Task<LibVLC?> GetAsync() => _init ??= Task.Run(Init);

    /// <summary>Подготовить заранее, в простое — первое видео откроется без ожидания распаковки.</summary>
    public static void Prewarm() => _ = GetAsync();

    private static LibVLC? Init()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck", "vlc", Version);
            var marker = Path.Combine(dir, ".ready");
            if (!File.Exists(marker))
            {
                using var zip = typeof(VlcRuntime).Assembly.GetManifestResourceStream("TgCheck.vlc.zip");
                if (zip is null) return null;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
                File.WriteAllText(marker, Version);
            }
            LibVLCSharp.Shared.Core.Initialize(dir);
            return new LibVLC("--quiet", "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--no-stats");
        }
        catch (Exception ex)
        {
            TgCheck.Core.Services.Logging.Write("vlc init: " + ex.Message);
            return null;
        }
    }
}
