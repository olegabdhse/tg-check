using System;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TgCheck.Setup;

/// <summary>Окно установки (и удаления, если запущено с /uninstall) в цветах приложения.</summary>
internal sealed class SetupForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0x17, 0x21, 0x2B);
    private static readonly Color Surface = Color.FromArgb(0x20, 0x2B, 0x36);
    private static readonly Color Accent = Color.FromArgb(0x2A, 0xAB, 0xEE);
    private static readonly Color TextColor = Color.FromArgb(0xF5, 0xF7, 0xFA);
    private static readonly Color Muted = Color.FromArgb(0x8C, 0x9B, 0xAB);

    private readonly CheckBox _desktop = new() { Text = "Ярлык на рабочем столе", Checked = true };
    private readonly CheckBox _pin = new() { Text = "Закрепить на панели задач", Checked = true };
    private readonly CheckBox _removeData = new() { Text = "Удалить также вход в аккаунт и кэш", Checked = false };
    private readonly ProgressBar _progress = new() { Height = 6, Visible = false, Style = ProgressBarStyle.Continuous };
    private readonly Button _main = new();
    private readonly Button _cancel = new() { Text = "Отмена" };
    private readonly Label _status = new() { AutoSize = false, Height = 22 };
    private bool _done;

    public SetupForm()
    {
        var uninstall = Installer.UninstallMode;
        Text = uninstall ? "Удаление TG Check" : "Установка TG Check";
        Font = new Font("Segoe UI", 10f);
        BackColor = Bg;
        ForeColor = TextColor;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 330);
        try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

        var logo = new PictureBox { Size = new Size(72, 72), Location = new Point(28, 28), SizeMode = PictureBoxSizeMode.Zoom };
        using (var png = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
            if (png is not null) logo.Image = Image.FromStream(png);
        var title = new Label
        {
            Text = "TG Check " + Installer.Version, Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(116, 32),
        };
        var subtitle = new Label
        {
            Text = uninstall ? "Программа и ярлыки будут удалены с этого компьютера." : "Telegram-клиент для Windows: чаты, профиль и подарки.",
            ForeColor = Muted, AutoSize = false, Size = new Size(340, 44), Location = new Point(118, 66),
        };
        var path = new Label
        {
            Text = "Папка: " + Installer.InstallDir, ForeColor = Muted, Font = new Font("Segoe UI", 8.5f),
            AutoSize = false, Size = new Size(424, 34), Location = new Point(28, 118), AutoEllipsis = true,
        };
        Controls.AddRange([logo, title, subtitle, path]);

        var y = 160;
        foreach (var box in uninstall ? new[] { _removeData } : new[] { _desktop, _pin })
        {
            box.ForeColor = TextColor;
            box.AutoSize = true;
            box.Location = new Point(28, y);
            Controls.Add(box);
            y += 30;
        }

        _status.Location = new Point(28, 234);
        _status.Width = 424;
        _status.ForeColor = Muted;
        _progress.Location = new Point(28, 258);
        _progress.Width = 424;
        Controls.Add(_status);
        Controls.Add(_progress);

        Style(_main, primary: true);
        _main.Text = uninstall ? "Удалить" : Installer.IsInstalled ? "Обновить" : "Установить";
        _main.Location = new Point(332, 280);
        _main.Click += async (_, _) => await (uninstall ? UninstallAsync() : InstallAsync());
        Style(_cancel, primary: false);
        _cancel.Location = new Point(216, 280);
        _cancel.Click += (_, _) => Close();
        Controls.Add(_main);
        Controls.Add(_cancel);
        AcceptButton = _main;
    }

    private static void Style(Button b, bool primary)
    {
        b.Size = new Size(120, 36);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = primary ? Accent : Surface;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI Semibold", 10f);
        b.Cursor = Cursors.Hand;
    }

    // Установка → программа сразу открывается, установщик закрывается (без лишних кликов).
    private async Task InstallAsync()
    {
        if (Installer.RunningApp().Length > 0 &&
            MessageBox.Show(this, "TG Check сейчас запущен — закрыть его и продолжить?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        Busy("Скачиваю последнюю версию TG Check…");
        var desktop = _desktop.Checked;
        var pin = _pin.Checked;
        try
        {
            var progress = new Progress<int>(p => _progress.Value = Math.Min(100, p));
            await Task.Run(() => Installer.Install(desktop, progress));
            var pinned = pin && Installer.PinToTaskbar();
            _status.Text = "Готово, открываю TG Check…";
            Installer.Launch();
            if (pin && !pinned)
                MessageBox.Show(this,
                    "TG Check установлен и открыт.\n\nWindows не разрешает программам закреплять себя на панели задач. " +
                    "Чтобы закрепить: нажмите правой кнопкой по значку TG Check на панели задач → «Закрепить на панели задач».",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    private async Task UninstallAsync()
    {
        if (_done) { Close(); return; }
        Busy("Удаление…");
        var removeData = _removeData.Checked;
        try
        {
            await Task.Run(() => Installer.Uninstall(removeData));
            _done = true;
            _progress.Value = 100;
            _status.Text = "TG Check удалён.";
            _main.Text = "Закрыть";
            _main.Enabled = true;
            _cancel.Visible = false;
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    private void Busy(string text)
    {
        _status.Text = text;
        _progress.Visible = true;
        _main.Enabled = false;
        _cancel.Enabled = false;
        _desktop.Enabled = _pin.Enabled = _removeData.Enabled = false;
    }

    private void Failed(Exception ex)
    {
        _status.Text = "Не получилось: " + ex.Message;
        _main.Enabled = true;
        _cancel.Enabled = true;
        _progress.Visible = false;
    }
}
