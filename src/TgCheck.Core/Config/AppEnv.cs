using System.Globalization;

namespace TgCheck.Core.Config;

/// <summary>Ошибка конфигурации (.env отсутствует или заполнен неверно).</summary>
public sealed class EnvConfigException(string message) : Exception(message);

/// <summary>
/// Настройки приложения из файла .env (и/или переменных окружения — они имеют приоритет).
/// </summary>
public sealed class AppEnv
{
    public const string FileName = ".env";

    public required int ApiId { get; init; }
    public required string ApiHash { get; init; }

    /// <summary>Номер, который подставится в поле входа (необязательно).</summary>
    public string? DefaultPhone { get; init; }

    /// <summary>
    /// Прокси (необязательно): MTProxy-ссылка (https://t.me/proxy?... или tg://proxy?...)
    /// или socks5://[логин:пароль@]хост:порт.
    /// </summary>
    public string? Proxy { get; init; }

    /// <summary>Свой прокси, заданный в программе (null — встроенный или без прокси). Встроенный наружу не показывается.</summary>
    public string? UserProxy { get; init; }

    /// <summary>В сборку зашит прокси (TG_PROXY).</summary>
    public bool HasBuiltInProxy { get; init; }

    /// <summary>Подключаемся через встроенный прокси.</summary>
    public bool UsesBuiltInProxy => Proxy is not null && UserProxy is null;

    /// <summary>ID пользователей, которых приложение полностью игнорирует (TG_IGNORE_IDS).</summary>
    public IReadOnlySet<long> IgnoredUserIds { get; init; } = new HashSet<long>();

    /// <summary>GitHub-репозиторий для обновлений (owner/repo): TG_UPDATE_REPO или значение, заданное при сборке.</summary>
    public string? UpdateRepo { get; init; }

    /// <summary>Папка для сессии, кэша и логов.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Путь к найденному .env (null — если значения взяты из переменных окружения).</summary>
    public string? SourcePath { get; init; }

    public static AppEnv Load()
    {
        var searched = new List<string>();
        var path = FindEnvFile(searched);
        // Основа — ключи, зашитые в exe при сборке; .env рядом с программой их переопределяет.
        var values = ReadEmbedded();
        if (path is not null)
            foreach (var (k, v) in Parse(File.ReadAllLines(path))) values[k] = v;

        string? Get(string key)
        {
            var env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        }

        var where = path ?? "не найден. Искал в:\n  " + string.Join("\n  ", searched.Distinct());

        var apiIdRaw = Get("TG_API_ID");
        var apiHash = Get("TG_API_HASH");
        if (apiIdRaw is null || apiHash is null)
            throw new EnvConfigException(
                "Не заданы TG_API_ID и/или TG_API_HASH.\n\n" +
                $"Файл {FileName}: {where}\n\n" +
                "Создайте .env рядом с TgCheck.exe (образец — .env.example) и укажите ключи с https://my.telegram.org → API development tools.");

        if (!int.TryParse(apiIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var apiId) || apiId <= 0)
            throw new EnvConfigException($"TG_API_ID должен быть положительным числом, сейчас: «{apiIdRaw}».\nФайл: {where}");

        if (apiHash.Length != 32 || !apiHash.All(Uri.IsHexDigit))
            throw new EnvConfigException($"TG_API_HASH должен состоять из 32 hex-символов (0-9, a-f).\nФайл: {where}");

        var dataDir = Get("TG_DATA_DIR") is { } custom
            ? Environment.ExpandEnvironmentVariables(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck");
        Directory.CreateDirectory(dataDir);

        // Прокси, заданный в самой программе (экран обрыва соединения), важнее встроенного TG_PROXY; "none" — без прокси.
        var builtIn = Get("TG_PROXY");
        var proxy = builtIn;
        string? userProxy = null;
        var userProxyFile = Path.Combine(dataDir, UserProxyFileName);
        if (File.Exists(userProxyFile) && File.ReadAllText(userProxyFile).Trim() is { Length: > 0 } user)
        {
            if (user.Equals("none", StringComparison.OrdinalIgnoreCase)) proxy = null;
            else if (IsSupportedProxy(user)) proxy = userProxy = user;
        }
        if (proxy is not null && !IsSupportedProxy(proxy))
            throw new EnvConfigException(
                $"TG_PROXY не распознан: «{proxy}».\n\nПоддерживается MTProxy (https://t.me/proxy?server=…&port=…&secret=… или tg://proxy?…) " +
                $"и SOCKS5 (socks5://хост:порт или socks5://логин:пароль@хост:порт).\nФайл: {where}");

        return new AppEnv
        {
            Proxy = proxy,
            UserProxy = userProxy,
            HasBuiltInProxy = builtIn is not null,
            IgnoredUserIds = ParseIds(Get("TG_IGNORE_IDS")),
            UpdateRepo = Get("TG_UPDATE_REPO") ?? BuildInfo.UpdateRepo,
            ApiId = apiId,
            ApiHash = apiHash.ToLowerInvariant(),
            DefaultPhone = Get("TG_PHONE"),
            DataDirectory = dataDir,
            SourcePath = path,
        };
    }

    /// <summary>Список ID через запятую/пробел; можно вставлять ссылки вида tg://user?id=123.</summary>
    internal static HashSet<long> ParseIds(string? raw)
    {
        var set = new HashSet<long>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw ?? "", @"\d{3,}"))
            if (long.TryParse(m.Value, out var id)) set.Add(id);
        return set;
    }

    private const string UserProxyFileName = "proxy.txt";

    /// <summary>Запомнить прокси, выбранный в программе (null — без прокси). Действует со следующего подключения.</summary>
    public static void SaveUserProxy(string dataDirectory, string? proxy)
    {
        var file = Path.Combine(dataDirectory, UserProxyFileName);
        if (proxy == BuiltInProxy) File.Delete(file);
        else File.WriteAllText(file, string.IsNullOrWhiteSpace(proxy) ? "none" : proxy.Trim());
    }

    /// <summary>Значение для SaveUserProxy: вернуться к встроенному прокси.</summary>
    public const string BuiltInProxy = "builtin";

    /// <summary>Похоже ли на ссылку прокси, которую понимает программа (MTProxy или SOCKS5).</summary>
    public static bool IsValidProxy(string proxy) => IsSupportedProxy(proxy.Trim());

    internal static bool IsMtProxy(string proxy) =>
        proxy.StartsWith("tg://proxy", StringComparison.OrdinalIgnoreCase) ||
        proxy.Contains("t.me/proxy", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSupportedProxy(string proxy) =>
        IsMtProxy(proxy) ||
        (Uri.TryCreate(proxy, UriKind.Absolute, out var u) && u.Scheme is "socks5" or "socks5h" && u.Host.Length > 0);

    /// <summary>Ищет .env рядом с exe, затем вверх по папкам (удобно при запуске из bin\Debug), затем в текущей папке.</summary>
    private static string? FindEnvFile(List<string> searched)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir is not null && i < 7; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            searched.Add(dir.FullName);
            if (File.Exists(candidate)) return candidate;
        }

        var cwd = Path.Combine(Environment.CurrentDirectory, FileName);
        searched.Add(Environment.CurrentDirectory);
        return File.Exists(cwd) ? cwd : null;
    }

    /// <summary>Ключи, зашитые в exe при сборке (ресурс TgCheck.embedded.env), или пустой набор.</summary>
    private static Dictionary<string, string> ReadEmbedded()
    {
        try
        {
            using var stream = System.Reflection.Assembly.GetEntryAssembly()?.GetManifestResourceStream("TgCheck.embedded.env");
            if (stream is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Формат — как в build\embed-env.ps1: IV (16 байт) + AES-256-CBC.
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var blob = ms.ToArray();
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = System.Security.Cryptography.SHA256.HashData("TgCheck.EmbeddedEnv.v1|7f3c9a1e"u8);
            aes.IV = blob[..16];
            var plain = aes.CreateDecryptor().TransformFinalBlock(blob, 16, blob.Length - 16);
            return Parse(System.Text.Encoding.UTF8.GetString(plain).Split('\n'));
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var c and >= 0)
                value = value[..c].TrimEnd();

            result[key] = value;
        }
        return result;
    }
}
