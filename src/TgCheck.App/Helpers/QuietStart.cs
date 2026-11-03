using System.Diagnostics;
using System.Globalization;
using System.Windows;

namespace TgCheck.App.Helpers;

/// <summary>
/// Запуск новой версии после фонового обновления: окно остаётся там, где было (в трее или свёрнутым), с прежним размером.
/// Старая копия передаёт это в командной строке и сразу завершается; новая сначала дожидается её выхода.
/// </summary>
/// <param name="Visible">Нажали «Обновить»: окно показывается сразу, как при обычном запуске.</param>
public sealed record QuietStart(int PreviousPid, bool Minimized, bool Maximized, Rect? Bounds, bool Visible = false)
{
    private const string Prefix = "--quiet-start=";
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public string ToArgs()
    {
        var bounds = Bounds is { } b ? string.Create(Culture, $"{b.Left},{b.Top},{b.Width},{b.Height}") : "";
        return $"{Prefix}{PreviousPid};{(Visible ? "show" : Minimized ? "min" : "tray")};{(Maximized ? "max" : "normal")};{bounds}";
    }

    public static QuietStart? Parse(string[] args)
    {
        if (args.FirstOrDefault(a => a.StartsWith(Prefix, StringComparison.Ordinal)) is not { } arg) return null;
        var parts = arg[Prefix.Length..].Split(';');
        if (parts.Length != 4 || !int.TryParse(parts[0], out var pid)) return null;
        Rect? bounds = null;
        var b = parts[3].Split(',');
        if (b.Length == 4 && b.All(v => double.TryParse(v, NumberStyles.Float, Culture, out _)))
            bounds = new Rect(double.Parse(b[0], Culture), double.Parse(b[1], Culture), double.Parse(b[2], Culture), double.Parse(b[3], Culture));
        return new QuietStart(pid, parts[1] == "min", parts[2] == "max", bounds, parts[1] == "show");
    }

    /// <summary>Сессию и общие файлы держит прежняя копия: новая начинает работу только после её выхода.</summary>
    public void WaitForPrevious()
    {
        try
        {
            using var previous = Process.GetProcessById(PreviousPid);
            previous.WaitForExit(15000);
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException) { } // уже завершилась, а её номер мог занять чужой процесс
    }
}
