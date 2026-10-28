using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using TgCheck.Core.Config;

namespace TgCheck.Core.Services;

public sealed record UpdateInfo(Version Version, string Tag, string? Notes, string DownloadUrl, long Size);

/// <summary>
/// Обновления через GitHub Releases: берётся последний релиз репозитория, из него — ассет TgCheck.exe.
/// Новый exe скачивается рядом, после закрытия приложения подменяет старый и запускается.
/// </summary>
public sealed class UpdateService(string repo)
{
    private const string AssetName = "TgCheck.exe";
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TgCheck", BuildInfo.VersionText));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Есть ли версия новее текущей. null — обновлений нет (или GitHub недоступен).</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            if (Normalize(version) <= Normalize(BuildInfo.Version)) return null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                return new UpdateInfo(version, tag,
                    root.TryGetProperty("body", out var body) ? body.GetString() : null,
                    asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64());
            }
            return null;
        }
        catch (Exception ex)
        {
            Logging.Write("update check: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Скачивает новую версию и готовит замену. После вызова приложение нужно закрыть —
    /// скрипт дождётся выхода процесса, заменит exe и запустит новую версию.
    /// </summary>
    public async Task PrepareAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к exe.");
        var dir = Path.GetDirectoryName(exe)!;
        var newExe = Path.Combine(dir, "TgCheck.new.exe");

        using (var resp = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? update.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(newExe);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        if (update.Size > 0 && new FileInfo(newExe).Length != update.Size)
        {
            File.Delete(newExe);
            throw new IOException("Файл обновления скачан не полностью. Попробуйте ещё раз.");
        }

        var pid = Environment.ProcessId;
        var script = Path.Combine(Path.GetTempPath(), $"tgcheck_update_{pid}.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "chcp 65001 >nul\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"move /y \"{newExe}\" \"{exe}\" >nul\r\n" +
            $"start \"\" \"{exe}\"\r\n" +
            "del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = dir,
        });
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
