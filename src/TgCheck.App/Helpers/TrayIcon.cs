using Forms = System.Windows.Forms;

namespace TgCheck.App.Helpers;

/// <summary>
/// Значок в области уведомлений: приложение продолжает работать (получать сообщения и показывать
/// уведомления), даже когда главное окно закрыто.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action open, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть TG Check", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Text = "TG Check",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    // Иконка exe (из папки logo при сборке); если её нет — стандартная иконка приложения.
    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Подсказка над значком (один раз — при первом сворачивании в трей).</summary>
    public void ShowHint(string title, string text) => _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
