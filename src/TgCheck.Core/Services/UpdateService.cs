using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TgCheck.Core.Config;

namespace TgCheck.Core.Services;

/// <param name="PatchUrl">Патч от текущей версии (TgCheck-&lt;версия&gt;.patch), если он есть в релизе.</param>
/// <param name="HashUrl">SHA-256 нового exe (TgCheck.exe.sha256) — скачанный или собранный из патча exe сверяется с ним.</param>
public sealed record UpdateInfo(Version Version, string Tag, string? Notes, string DownloadUrl, long Size,
    string? PatchUrl = null, long PatchSize = 0, string? HashUrl = null);

/// <summary>Установленное обновление, о котором ещё не рассказали: версия и описание релиза.</summary>
public sealed record InstalledUpdate(string Version, string? Notes);

/// <summary>
/// Обновления через GitHub Releases: берётся последний релиз репозитория, из него — ассет TgCheck.exe.
/// Если в релизе есть патч именно от текущей версии — качается только он (меньше мегабайта), и новый exe собирается из
/// текущего; не вышло (нет патча, сумма не сошлась) — качается полный exe. Всё идёт в фоне: запущенный exe можно
/// переименовать, поэтому новая версия сразу встаёт на его место и работает со следующего запуска (кнопка на плашке или тихий перезапуск).
/// </summary>
public sealed class UpdateService(string repo)
{
    private const string AssetName = "TgCheck.exe";
    private const string HashName = "TgCheck.exe.sha256";
    private const string OldExeName = "TgCheck.old.exe";
    private const string NewExeName = "TgCheck.new.exe";
    // Файл рядом с exe отключает обновления: так живут тестовые сборки, которые нельзя подменять выпущенной версией.
    private const string NoUpdateMarker = "TgCheck.noupdate";
    private static string PatchName => $"TgCheck-{BuildInfo.VersionText}.patch";
    private static readonly HttpClient Http = CreateHttp();
    private static readonly string InstalledFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck", "installed_update.json");

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
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, NoUpdateMarker))) return null;
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
    /// Скачивает новую версию и ставит её на место exe. Программа продолжает работать: старый код остаётся в памяти,
    /// а файл под прежним именем — уже новый. Ошибка ничего не ломает: exe остаётся прежним, в следующий раз попробуем снова.
    /// </summary>
    public async Task InstallAsync(UpdateInfo update, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к exe.");
        var dir = Path.GetDirectoryName(exe)!;
        var newExe = Path.Combine(dir, NewExeName);
        var oldExe = Path.Combine(dir, OldExeName);
        try
        {
            if (!await TryPatchAsync(update, exe, newExe, ct)) await DownloadWholeAsync(update, newExe, ct);
            File.Move(exe, oldExe, overwrite: true);
            try { File.Move(newExe, exe); }
            catch
            {
                File.Move(oldExe, exe);
                throw;
            }
        }
        finally
        {
            try { File.Delete(newExe); } catch { }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false); // оба exe читались в память целиком
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstalledFile)!);
            await File.WriteAllTextAsync(InstalledFile, JsonSerializer.Serialize(new InstalledUpdate(update.Version.ToString(3), update.Notes)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logging.Write("update: описание не сохранилось: " + ex.Message); }
    }

    /// <summary>Запустить уже установленную версию. Вызывающий сразу после этого завершается.</summary>
    public static void StartInstalled(string args)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к exe.");
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>Убрать прежний exe, оставшийся от обновления (к новому запуску он уже никем не занят).</summary>
    public static void CleanUpAfterUpdate()
    {
        try
        {
            if (Path.GetDirectoryName(Environment.ProcessPath) is { } dir && File.Exists(Path.Combine(dir, OldExeName)))
                File.Delete(Path.Combine(dir, OldExeName));
        }
        catch (Exception ex) { Logging.Write("update: прежний exe не удалён: " + ex.Message); }
    }

    /// <summary>
    /// Описание обновления, которым сейчас работает программа; показывается один раз. Пока работает ещё старый код, файл
    /// с описанием более новой версии не трогаем — его заберёт уже она.
    /// </summary>
    public static InstalledUpdate? TakeInstalled()
    {
        try
        {
            if (!File.Exists(InstalledFile)) return null;
            var installed = JsonSerializer.Deserialize<InstalledUpdate>(File.ReadAllText(InstalledFile));
            if (installed is not null && Version.TryParse(installed.Version, out var version) && Normalize(version) > Normalize(BuildInfo.Version)) return null;
            File.Delete(InstalledFile);
            if (installed is null || installed.Version != BuildInfo.VersionText) return null;
            // Раздел про установку нужен на странице релиза, внутри программы он ни к чему.
            return installed with { Notes = installed.Notes is { } notes ? Regex.Replace(notes, @"(?ms)^##\s*Установка.*\z", "").Trim() : null };
        }
        catch (Exception ex)
        {
            Logging.Write("update: описание не прочитано: " + ex.Message);
            return null;
        }
    }

    // Обновиться патчем: скачать его, собрать новый exe из текущего и сверить SHA-256 с опубликованной.
    // false — патча нет или что-то не сошлось (тогда качается полный exe).
    private static async Task<bool> TryPatchAsync(UpdateInfo update, string exe, string newExe, CancellationToken ct)
    {
        if (update.PatchUrl is null || update.HashUrl is null) return false;
        try
        {
            var expected = await GetExpectedHashAsync(update.HashUrl, ct);
            using var patch = new MemoryStream();
            await DownloadAsync(update.PatchUrl, patch, ct);
            var built = await Task.Run(() => DeltaPatch.Apply(File.ReadAllBytes(exe), patch.ToArray()), ct);
            var actual = Convert.ToHexString(SHA256.HashData(built)).ToLowerInvariant();
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

    // Подмена идёт без участия человека, поэтому и полный exe проверяется по SHA-256, а не только по размеру.
    private static async Task DownloadWholeAsync(UpdateInfo update, string newExe, CancellationToken ct)
    {
        await using (var dst = File.Create(newExe))
            await DownloadAsync(update.DownloadUrl, dst, ct);
        if (update.Size > 0 && new FileInfo(newExe).Length != update.Size)
            throw new IOException("Файл обновления скачан не полностью.");
        if (update.HashUrl is null) return;
        var expected = await GetExpectedHashAsync(update.HashUrl, ct);
        await using var file = File.OpenRead(newExe);
        if (Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant() != expected)
            throw new IOException("Сумма файла обновления не совпала с релизом.");
    }

    private static async Task<string> GetExpectedHashAsync(string url, CancellationToken ct) =>
        (await Http.GetStringAsync(url, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();

    private static async Task DownloadAsync(string url, Stream dst, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await src.CopyToAsync(dst, ct);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
