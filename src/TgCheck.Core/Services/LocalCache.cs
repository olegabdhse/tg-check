using System.Security.Cryptography;
using TL;

namespace TgCheck.Core.Services;

/// <summary>
/// Локальный кэш списка чатов и последних сообщений: объекты Telegram сериализуются в двоичный TL-формат
/// и шифруются DPAPI текущего пользователя (переписка не лежит на диске в открытом виде).
/// </summary>
internal sealed class LocalCache(string dir)
{
    private static readonly byte[] Entropy = "TgCheck.Cache.v1"u8.ToArray();

    public void Save<T>(string name, T obj) where T : IObject
    {
        try
        {
            Directory.CreateDirectory(dir);
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                writer.WriteTLObject(obj);
            var data = ProtectedData.Protect(ms.ToArray(), Entropy, DataProtectionScope.CurrentUser);
            var path = Path.Combine(dir, name + ".bin");
            var tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logging.Write($"cache save {name}: {ex.Message}");
        }
    }

    public T? Load<T>(string name) where T : class
    {
        var path = Path.Combine(dir, name + ".bin");
        if (!File.Exists(path)) return null;
        try
        {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            using var reader = new BinaryReader(new MemoryStream(data));
            return reader.ReadTLObject() as T;
        }
        catch (Exception ex)
        {
            Logging.Write($"cache load {name}: {ex.Message}");
            try { File.Delete(path); } catch { /* повреждённый кэш просто выбрасываем */ }
            return null;
        }
    }

    public void Clear()
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* не критично */ }
    }
}
