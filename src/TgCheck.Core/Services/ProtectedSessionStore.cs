using System.Security.Cryptography;

namespace TgCheck.Core.Services;

/// <summary>
/// Хранит сессию Telegram на диске, зашифрованную DPAPI текущего пользователя Windows:
/// скопированный на другой компьютер/аккаунт файл расшифровать не получится.
/// </summary>
internal sealed class ProtectedSessionStore(string path)
{
    private static readonly byte[] Entropy = "TgCheck.Session.v1"u8.ToArray();
    private readonly object _lock = new();

    public byte[]? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(path)) return null;
            // Файл может быть на мгновение занят записью (замена через .tmp) — несколько попыток,
            // прежде чем считать сессию потерянной: иначе пользователь зря попадает на экран входа.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                }
                catch (Exception ex) when (ex is IOException or CryptographicException && attempt < 5)
                {
                    Thread.Sleep(150);
                }
                catch (CryptographicException)
                {
                    // Файл повреждён или создан другим пользователем — начинаем с чистой сессии.
                    Logging.Write("session: не удалось расшифровать session.dat, нужен повторный вход");
                    return null;
                }
            }
        }
    }

    public void Save(byte[] data)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
            File.Move(tmp, path, overwrite: true);
        }
    }

    public void Delete()
    {
        lock (_lock)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
