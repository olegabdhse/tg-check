using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace TgCheck.App.Helpers;

public static class Images
{
    /// <summary>Средний цвет картинки (по уменьшенной копии) — для фона под обложкой.</summary>
    public static Color? AverageColor(BitmapSource source)
    {
        try
        {
            var small = new TransformedBitmap(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0),
                new ScaleTransform(16.0 / Math.Max(1, source.PixelWidth), 16.0 / Math.Max(1, source.PixelHeight)));
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);
            long r = 0, g = 0, b = 0, n = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                b += px[i]; g += px[i + 1]; r += px[i + 2]; n++;
            }
            return n == 0 ? null : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        catch { return null; }
    }

    /// <summary>Декодирует jpeg/png в замороженный BitmapImage (можно вызывать из фонового потока).</summary>
    public static BitmapImage? FromBytes(byte[]? bytes, int? decodeWidth = null)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var bi = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth is { } w) bi.DecodePixelWidth = w;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Картинка не шире maxWidth (меньшие — как есть): превью в ленте декодируются под экран, а не в полный размер —
    /// фото 1280 px занимало в памяти ~5 МБ, хотя на экране оно до 360 px (отсюда сотни мегабайт на чатах с фото).
    /// </summary>
    public static BitmapImage? FromBytesMax(byte[]? bytes, int maxWidth)
    {
        if (bytes is null || bytes.Length == 0) return null;
        int width;
        try
        {
            using var ms = new MemoryStream(bytes);
            width = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0].PixelWidth;
        }
        catch { width = 0; }
        return FromBytes(bytes, width > maxWidth ? maxWidth : null);
    }

    public static BitmapImage? Qr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12, [0x0E, 0x16, 0x21, 0xFF], [0xFF, 0xFF, 0xFF, 0xFF], drawQuietZones: false);
        return FromBytes(png);
    }

    /// <summary>
    /// Логотип из папки logo\ (встраивается при сборке как Assets/applogo.png). null — если картинки не было.
    /// </summary>
    public static ImageSource? AppLogo { get; } = LoadLogo();

    private static ImageSource? LoadLogo()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/applogo.png"));
            if (info is null) return null;
            using var s = info.Stream;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return FromBytes(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    public static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

public static class Fmt
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string N(long value) => value.ToString("N0", Ru);
    public static string N(decimal value) => value.ToString("#,0.##", Ru);

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMMM yyyy, HH:mm", Ru);
    public static string DateShort(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", Ru);

    /// <summary>Время в списке чатов: сегодня — 14:05, на неделе — «пн», раньше — 12.03.26.</summary>
    public static string ChatTime(DateTime utc)
    {
        var t = utc.ToLocalTime();
        var today = DateTime.Today;
        if (t.Date == today) return t.ToString("HH:mm", Ru);
        if (t.Date > today.AddDays(-7)) return t.ToString("ddd", Ru);
        return t.ToString("dd.MM.yy", Ru);
    }

    /// <summary>Разделитель дат в чате: «Сегодня», «Вчера», «12 марта», «12 марта 2024».</summary>
    public static string DayHeader(DateTime localDate)
    {
        var today = DateTime.Today;
        if (localDate == today) return "Сегодня";
        if (localDate == today.AddDays(-1)) return "Вчера";
        return localDate.ToString(localDate.Year == today.Year ? "d MMMM" : "d MMMM yyyy", Ru);
    }

    public static string Duration(double seconds)
    {
        var s = (int)Math.Max(0, Math.Round(seconds));
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ",
    };

    public static string LastSeen(TgCheck.Core.Models.Presence p, DateTime? seenUtc) => p switch
    {
        TgCheck.Core.Models.Presence.Online => "в сети",
        TgCheck.Core.Models.Presence.Offline when seenUtc is { } t => t.ToLocalTime().Date == DateTime.Today
            ? $"был(а) в {t.ToLocalTime():HH:mm}"
            : $"был(а) {Date(t)}",
        TgCheck.Core.Models.Presence.Recently => "был(а) недавно",
        TgCheck.Core.Models.Presence.LastWeek => "был(а) на этой неделе",
        TgCheck.Core.Models.Presence.LastMonth => "был(а) в этом месяце",
        _ => "был(а) давно",
    };

    /// <summary>1 звезда, 2 звезды, 5 звёзд.</summary>
    public static string Stars(long n) => $"{N(n)} {Plural(n, "звезда", "звезды", "звёзд")}";

    public static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n) % 100;
        var n1 = n % 10;
        if (n is > 10 and < 20) return many;
        if (n1 is > 1 and < 5) return few;
        return n1 == 1 ? one : many;
    }
}

public static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* нет обработчика — молча игнорируем */ }
    }
}

public static class WindowEffects
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static System.Drawing.Icon? _exeIcon;

    /// <summary>
    /// Иконка окна прямо из exe. Панель задач спрашивает иконку у окна при показе/разворачивании, и если поток
    /// в этот момент занят отрисовкой, ставит пустой значок окна и сама его не обновляет. Поэтому иконку
    /// переустанавливаем после показа — каждый раз новым дескриптором (тот же самый Windows пропускает как «без изменений»).
    /// </summary>
    public static void ApplyExeIcon(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || Environment.ProcessPath is not { } exe) return;
        System.Drawing.Icon? icon = null;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(exe); } catch { }
        if (icon is null) return;
        const int WM_SETICON = 0x80;
        SendMessage(hwnd, WM_SETICON, (IntPtr)1, icon.Handle); // большая — панель задач, Alt+Tab
        SendMessage(hwnd, WM_SETICON, (IntPtr)0, icon.Handle);
        _exeIcon?.Dispose();
        _exeIcon = icon;
    }

    /// <summary>Переустановить иконку, когда окно уже показано и поток свободен.</summary>
    public static void RefreshExeIconWhenIdle(Window window) =>
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => ApplyExeIcon(window));

    /// <summary>Тёмный заголовок окна Windows 10/11 в цвет фона приложения.</summary>
    public static void ApplyDarkTitleBar(Window window, Color caption)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // старые сборки Windows 10
        var colorRef = caption.R | caption.G << 8 | caption.B << 16;
        DwmSetWindowAttribute(hwnd, 35, ref colorRef, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11)
    }

    /// <summary>
    /// Скруглённые углы и системная тень Windows 11 для окна без рамки — вместо прозрачного (layered) окна
    /// с эффектом тени, которое перерисовывается целиком на каждое изменение и мерцает.
    /// </summary>
    public static void ApplyRoundedCorners(Window window, Color border)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        var colorRef = border.R | border.G << 8 | border.B << 16;
        DwmSetWindowAttribute(hwnd, 34, ref colorRef, sizeof(int)); // DWMWA_BORDER_COLOR
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // тёмная тема рамки
    }
}
