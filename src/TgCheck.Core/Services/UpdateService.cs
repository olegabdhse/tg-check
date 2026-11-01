using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using TgCheck.Core.Config;

namespace TgCheck.Core.Services;

/// <param name="PatchUrl">Патч от текущей версии (TgCheck-&lt;версия&gt;.patch), если он есть в релизе.</param>
/// <param name="HashUrl">SHA-256 нового exe (TgCheck.exe.sha256) — собранный из патча exe сверяется с ним.</param>
public sealed record UpdateInfo(Version Version, string Tag, string? Notes, string DownloadUrl, long Size,
    string? PatchUrl = null, long PatchSize = 0, string? HashUrl = null);

/// <summary>
/// Обновления через GitHub Releases: берётся последний релиз репозитория, из него — ассет TgCheck.exe.
/// Если в релизе есть патч именно от текущей версии — качается только он (меньше мегабайта), и новый exe собирается из
/// текущего; не вышло (нет патча, сумма не сошлась) — качается полный exe. Новый exe ставится рядом и после закрытия
/// приложения подменяет старый.
/// </summary>
public sealed class UpdateService(string repo)
{
    private const string AssetName = "TgCheck.exe";
    private const string HashName = "TgCheck.exe.sha256";
    private static string PatchName => $"TgCheck-{BuildInfo.VersionText}.patch";
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
            if (!resp.IsSuccessStatusCode)
            {
                Logging.Write($"update check: GitHub ответил {(int)resp.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            if (Normalize(version) <= Normalize(BuildInfo.Version)) return null;

            string? exeUrl = null, patchUrl = null, hashUrl = null;
            long exeSize = 0, patchSize = 0;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                var size = asset.GetProperty("size").GetInt64();
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) (exeUrl, exeSize) = (url, size);
                else if (string.Equals(name, PatchName, StringComparison.OrdinalIgnoreCase)) (patchUrl, patchSize) = (url, size);
                else if (string.Equals(name, HashName, StringComparison.OrdinalIgnoreCase)) hashUrl = url;
            }
            if (exeUrl is null) return null;
            return new UpdateInfo(version, tag, root.TryGetProperty("body", out var body) ? body.GetString() : null, exeUrl, exeSize,
                hashUrl is null ? null : patchUrl, patchSize, hashUrl);
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

        if (!await TryPatchAsync(update, exe, newExe, progress, ct))
        {
            await using (var dst = File.Create(newExe))
                await DownloadAsync(update.DownloadUrl, update.Size, dst, progress, ct);
            if (update.Size > 0 && new FileInfo(newExe).Length != update.Size)
            {
                File.Delete(newExe);
                throw new IOException("Файл обновления скачан не полностью. Попробуйте ещё раз.");
            }
        }

        var pid = Environment.ProcessId;
        var script = Path.Combine(Path.GetTempPath(), $"tgcheck_update_{pid}.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "chcp 65001 >nul\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            // exe может ещё несколько секунд держать антивирус или система — повторяем замену (до 30 с), затем запускаем.
            "set n=0\r\n" +
            ":move\r\n" +
            $"move /y \"{newExe}\" \"{exe}\" >nul 2>&1 && goto run\r\n" +
            "set /a n+=1\r\n" +
            "if %n% geq 30 goto run\r\n" +
            "timeout /t 1 /nobreak >nul\r\n" +
            "goto move\r\n" +
            ":run\r\n" +
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

    // Обновиться патчем: скачать его, собрать новый exe из текущего и сверить SHA-256 с опубликованной.
    // false — патча нет или что-то не сошлось (тогда качается полный exe).
    private static async Task<bool> TryPatchAsync(UpdateInfo update, string exe, string newExe, IProgress<double>? progress, CancellationToken ct)
    {
        if (update.PatchUrl is null || update.HashUrl is null) return false;
        try
        {
            var expected = (await Http.GetStringAsync(update.HashUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            using var patch = new MemoryStream();
            await DownloadAsync(update.PatchUrl, update.PatchSize, patch, progress, ct);
            var built = await Task.Run(() => DeltaPatch.Apply(File.ReadAllBytes(exe), patch.ToArray()), ct);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(built)).ToLowerInvariant();
            if (actual != expected)
            {
                Logging.Write($"update: собранный из патча exe не совпал с релизом — качаем целиком");
                return false;
            }
            await File.WriteAllBytesAsync(newExe, built, ct);
            Logging.Write($"update: патч {update.PatchSize / 1024} КБ вместо {update.Size / 1048576} МБ");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logging.Write($"update: патч не применился ({ex.Message}) — качаем целиком");
            return false;
        }
    }

    private static async Task DownloadAsync(string url, long size, Stream dst, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? size;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
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

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
