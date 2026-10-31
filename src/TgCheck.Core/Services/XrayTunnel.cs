using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TgCheck.Core.Services;

/// <summary>
/// Свой туннель до сервера по VLESS + Reality (Xray): для блокировок он выглядит как обычный HTTPS к www.microsoft.com,
/// а сервер на проверку без ключа отвечает настоящим сайтом. Xray встроен в exe (сжатым), распаковывается при первом
/// запуске и работает скрытым локальным SOCKS5-прокси на 127.0.0.1 — через него и подключается Telegram.
/// Настройки (ключи) передаются ему через стандартный ввод — на диск не пишутся. Закрылась программа (даже аварийно) —
/// Xray закрывается вместе с ней (задание Windows с «убить при закрытии»).
/// </summary>
internal sealed class XrayTunnel : IDisposable
{
    private const string Version = "26.10.31";
    private readonly Process _process;
    private readonly IntPtr _job;

    public int Port { get; }

    /// <summary>Адрес для подключения Telegram: socks5://127.0.0.1:порт.</summary>
    public string ProxyUrl => $"socks5://127.0.0.1:{Port}";

    public bool IsAlive => !_process.HasExited;

    private XrayTunnel(Process process, IntPtr job, int port)
    {
        _process = process;
        _job = job;
        Port = port;
    }

    /// <summary>Запустить туннель по ссылке vless://… (null — не вышло: нет Xray в сборке, ссылка не та, не запустился).</summary>
    public static XrayTunnel? Start(string link)
    {
        try
        {
            if (Exe() is not { } exe) return null;
            var config = Config(link, FreePort(), out var port);
            var process = Process.Start(new ProcessStartInfo(exe, "run -config stdin: -format json")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var job = KillWithUs(process);
            // Вывод читаем (и отбрасываем), иначе заполненный буфер остановит Xray.
            process.OutputDataReceived += (_, e) => { if (e.Data?.Contains("error", StringComparison.OrdinalIgnoreCase) == true) Logging.Write("xray: " + Mask(e.Data)); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Logging.Write("xray: " + Mask(e.Data)); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Write(config);
            process.StandardInput.Close();
            // Готов, когда начал слушать порт (обычно за доли секунды).
            for (var i = 0; i < 40 && !process.HasExited; i++)
            {
                if (Listening(port)) return new XrayTunnel(process, job, port);
                Thread.Sleep(100);
            }
            Logging.Write("xray: не запустился");
            try { process.Kill(); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            Logging.Write("xray: " + ex.Message);
            return null;
        }
    }

    // Адрес сервера в лог не пишем.
    private static string Mask(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\d+\.\d+\.\d+\.\d+", "x.x.x.x");

    // Официальный xray.exe из выпуска XTLS/Xray-core v26.10.31 (архив сверен с SHA2-256 из выпуска). Перед каждым запуском
    // файл сверяется с этой суммой: подменённый (вирус, порча диска) пересоздаётся из встроенного, а не запускается.
    private const string Sha256 = "d5b558191cccb1e92a5772b68b8cab67717b26f634b0f15153215f65ae982a7e";

    // xray.exe из сборки — в %LOCALAPPDATA%\TgCheck\xray\<версия>.
    private static string? Exe()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck", "xray", Version);
        var path = Path.Combine(dir, "xray.exe");
        if (File.Exists(path) && Genuine(path)) return path;
        using var res = System.Reflection.Assembly.GetEntryAssembly()?.GetManifestResourceStream("TgCheck.xray.exe.gz");
        if (res is null) return null;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var gz = new GZipStream(res, CompressionMode.Decompress))
        using (var file = File.Create(tmp))
            gz.CopyTo(file);
        if (!Genuine(tmp))
        {
            Logging.Write("xray: встроенный файл не совпал с официальным — не запускаю");
            File.Delete(tmp);
            return null;
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static bool Genuine(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool Listening(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(IPAddress.Loopback, port).Wait(100) && tcp.Connected;
        }
        catch { return false; }
    }

    // vless://id@host:port?security=reality&sni=…&pbk=…&sid=…&fp=…&flow=… → настройки Xray: вход SOCKS5 на 127.0.0.1, выход VLESS.
    private static string Config(string link, int localPort, out int port)
    {
        port = localPort;
        var uri = new Uri(link.Trim());
        if (uri.Scheme != "vless") throw new FormatException("ссылка Xray должна начинаться с vless://");
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var config = new
        {
            log = new { loglevel = "warning" },
            inbounds = new[] { new { listen = "127.0.0.1", port = localPort, protocol = "socks", settings = new { udp = false } } },
            outbounds = new[]
            {
                new
                {
                    protocol = "vless",
                    settings = new
                    {
                        vnext = new[]
                        {
                            new
                            {
                                address = uri.Host, port = uri.Port,
                                users = new[] { new { id = Uri.UnescapeDataString(uri.UserInfo), encryption = "none", flow = q["flow"] ?? "xtls-rprx-vision" } },
                            },
                        },
                    },
                    streamSettings = new
                    {
                        network = q["type"] ?? "tcp",
                        security = "reality",
                        realitySettings = new
                        {
                            serverName = q["sni"] ?? "",
                            fingerprint = q["fp"] ?? "chrome",
                            publicKey = q["pbk"] ?? "",
                            shortId = q["sid"] ?? "",
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(config);
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(); } catch { }
        _process.Dispose();
        if (_job != IntPtr.Zero) CloseHandle(_job);
    }

    // ---- задание Windows: Xray закрывается вместе с программой, даже если она упала

    private static IntPtr KillWithUs(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        var size = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ptr, (uint)size);
            AssignProcessToJobObject(job, process.Handle);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
