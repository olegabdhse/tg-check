using System.Windows;

namespace TgCheck.App.Views;

/// <summary>Где открыть отдельное (независимое от основного) окно.</summary>
public static class WindowPlacement
{
    /// <summary>
    /// По центру над окном-якорем, если оно на экране; иначе (свёрнуто, в трее) — по центру монитора под курсором.
    /// Владельца не назначаем: окно живёт само по себе — своя кнопка на панели задач, не сворачивается вместе с основным.
    /// </summary>
    public static void CenterOn(Window window, Window? anchor)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        Rect area;
        if (anchor is { IsVisible: true } && anchor.WindowState != WindowState.Minimized)
        {
            area = anchor.WindowState == WindowState.Maximized
                ? Helpers.Monitors.WorkAreaAt(new Point(anchor.Left + 20, anchor.Top + 20), anchor)
                : new Rect(anchor.Left, anchor.Top, anchor.ActualWidth, anchor.ActualHeight);
        }
        else area = Helpers.Monitors.WorkAreaAtCursor(anchor ?? window);
        var w = double.IsNaN(window.Width) ? 400 : window.Width;
        var h = double.IsNaN(window.Height) ? 600 : window.Height;
        window.Left = area.Left + Math.Max(0, (area.Width - w) / 2);
        window.Top = area.Top + Math.Max(0, (area.Height - h) / 2);
    }
}
