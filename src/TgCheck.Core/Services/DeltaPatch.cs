using ZstdSharp;
using ZstdSharp.Unsafe;

namespace TgCheck.Core.Services;

/// <summary>
/// Патч обновления: новый exe, сжатый zstd с прошлым exe в качестве словаря (на сервере: zstd --patch-from). Внутри exe
/// каждая библиотека сжата отдельно, поэтому неизменившиеся части (среда .NET, VLC, Xray) совпадают байт в байт и в патч
/// не попадают — он весит меньше мегабайта вместо ~115 МБ.
/// </summary>
public static class DeltaPatch
{
    // Окно должно покрывать весь прошлый exe (~115 МБ): до 2^28 = 256 МБ.
    private const int WindowLogMax = 28;

    /// <summary>Собрать новый exe из прошлого и патча.</summary>
    public static byte[] Apply(byte[] oldFile, byte[] patch)
    {
        using var decompressor = new Decompressor();
        decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, WindowLogMax);
        decompressor.LoadDictionary(oldFile);
        return decompressor.Unwrap(patch).ToArray();
    }
}
