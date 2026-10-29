using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using Microsoft.Win32;

namespace TgCheck.Setup;

/// <summary>
/// Установка в папку пользователя (без прав администратора): exe, ярлыки «Пуск» и рабочего стола,
/// запись в «Приложения» Windows для удаления. Автообновление программы потом заменяет exe в этой же папке.
/// </summary>
internal static class Installer
{
    public const string AppName = "TG Check";
    private const string ExeName = "TgCheck.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TGCheck";

    /// <summary>Последний релиз программы на GitHub (репозиторий — из настроек сборки, как у автообновления).</summary>
    private static string DownloadUrl
    {
        get
        {
            var repo = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value;
            if (string.IsNullOrEmpty(repo)) throw new InvalidOperationException("Установщик собран без адреса репозитория (UpdateRepo).");
            return $"https://github.com/{repo}/releases/latest/download/{ExeName}";
        }
    }

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);
    private static string UninstallerPath => Path.Combine(InstallDir, "Uninstall TG Check.exe");
    private static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    /// <summary>Запущена ли программа (её нужно закрыть, иначе exe не заменить).</summary>
    public static Process[] RunningApp() => Process.GetProcessesByName("TgCheck");

    public static void Install(bool desktopShortcut, IProgress<int> progress)
    {
        foreach (var p in RunningApp())
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { /* уже закрылась */ }
        }
        Directory.CreateDirectory(InstallDir);
        progress.Report(5);

        // Программа — последняя версия из релизов GitHub (установщик поэтому весит килобайты).
        var tmp = InstalledExe + ".new";
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var request = (HttpWebRequest)WebRequest.Create(DownloadUrl);
        request.UserAgent = "TgCheck-Setup";
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var source = response.GetResponseStream()!)
        using (var target = File.Create(tmp))
        {
            var total = response.ContentLength;
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, read);
                done += read;
                if (total > 0) progress.Report(5 + (int)(80 * done / total));
            }
        }
        if (new FileInfo(tmp).Length < 1024 * 1024)
        {
            File.Delete(tmp);
            throw new InvalidOperationException("Не удалось скачать программу — проверьте интернет и попробуйте ещё раз.");
        }
        if (File.Exists(InstalledExe)) File.Delete(InstalledExe);
        File.Move(tmp, InstalledExe);

        // Сам установщик — он же удаляет программу из «Приложений».
        var self = Assembly.GetExecutingAssembly().Location;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallerPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, UninstallerPath, overwrite: true);
        progress.Report(90);

        CreateShortcut(StartMenuShortcut);
        if (desktopShortcut) CreateShortcut(DesktopShortcut);
        else if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);
        progress.Report(95);

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", AppName);
            key.SetValue("DisplayIcon", InstalledExe + ",0");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{UninstallerPath}\" /uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }
        progress.Report(100);
    }

    /// <summary>
    /// Закрепить на панели задач. Windows не даёт программам делать это официально; пробуем команду проводника
    /// «Закрепить на панели задач» у ярлыка. false — не вышло (тогда пользователь закрепит значок сам).
    /// </summary>
    public static bool PinToTaskbar()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace(Path.GetDirectoryName(StartMenuShortcut));
            dynamic item = folder?.ParseName(Path.GetFileName(StartMenuShortcut));
            if (item is null) return false;
            foreach (dynamic verb in item.Verbs())
            {
                string name = ((string)verb.Name).Replace("&", "");
                if (name.IndexOf("панели задач", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Открепить", StringComparison.OrdinalIgnoreCase) < 0
                    || name.Equals("Pin to taskbar", StringComparison.OrdinalIgnoreCase))
                {
                    verb.DoIt();
                    return true;
                }
            }
        }
        catch { /* команды нет (Windows 11) — закрепят вручную */ }
        return false;
    }

    // Ярлык с иконкой самой программы (через WScript.Shell — есть в любой Windows).
    private static void CreateShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = InstalledExe;
        link.WorkingDirectory = InstallDir;
        link.IconLocation = InstalledExe + ",0";
        link.Description = "Telegram-клиент TG Check";
        link.Save();
    }

    /// <summary>Удалить программу, ярлыки и запись в «Приложениях»; данные (вход, кэш) — по желанию.</summary>
    public static void Uninstall(bool removeUserData)
    {
        foreach (var p in RunningApp())
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
        foreach (var file in new[] { StartMenuShortcut, DesktopShortcut, InstalledExe })
            if (File.Exists(file)) File.Delete(file);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        if (removeUserData)
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck");
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
        }
        // Сам деинсталлятор запущен из папки программы — удаляем его и папку после выхода.
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c timeout /t 2 /nobreak > nul & del /f /q \"{UninstallerPath}\" & rmdir /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static void Launch() => Process.Start(new ProcessStartInfo(InstalledExe) { WorkingDirectory = InstallDir, UseShellExecute = true });

    public static string[] Args { get; set; } = [];
    public static bool UninstallMode => Args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
}
