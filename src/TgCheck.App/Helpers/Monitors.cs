using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace TgCheck.App.Helpers;

/// <summary>
/// Мониторы: рабочая область (без панели задач) нужного монитора. SystemParameters.WorkArea знает только основной,
/// поэтому островок и его окна на втором мониторе прыгали обратно на основной.
/// </summary>
public static class Monitors
{
    [StructLayout(LayoutKind.Sequential)] public struct PointPx { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RectPx { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public RectPx Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool EnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PointPx pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointPx p);

    private static (RectPx Work, string Device) Info(IntPtr monitor)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        GetMonitorInfo(monitor, ref info);
        return (info.Work, info.Device);
    }

    /// <summary>Монитор под точкой экрана (в пикселях): рабочая область и имя устройства.</summary>
    public static (RectPx Work, string Device) At(int x, int y) => Info(MonitorFromPoint(new PointPx { X = x, Y = y }, 2 /* ближайший */));

    /// <summary>Рабочая область запомненного монитора (пиксели); если его отключили — основного.</summary>
    public static RectPx WorkArea(string? device)
    {
        if (device is not null)
        {
            RectPx? found = null;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
            {
                var (work, name) = Info(m);
                if (name != device) return true;
                found = work;
                return false;
            }, IntPtr.Zero);
            if (found is { } f) return f;
        }
        return Info(MonitorFromPoint(new PointPx(), 1 /* основной */)).Work;
    }

    /// <summary>Рабочая область (в единицах WPF) монитора, где точка окна (тоже в единицах WPF).</summary>
    public static Rect WorkAreaAt(Point point, Visual visual)
    {
        var s = VisualTreeHelper.GetDpi(visual).DpiScaleX;
        return ToDip(At((int)(point.X * s), (int)(point.Y * s)).Work, s);
    }

    /// <summary>Рабочая область монитора под курсором (в единицах WPF).</summary>
    public static Rect WorkAreaAtCursor(Visual visual)
    {
        GetCursorPos(out var c);
        return ToDip(At(c.X, c.Y).Work, VisualTreeHelper.GetDpi(visual).DpiScaleX);
    }

    private static Rect ToDip(RectPx r, double s) => new(r.Left / s, r.Top / s, (r.Right - r.Left) / s, (r.Bottom - r.Top) / s);
}
