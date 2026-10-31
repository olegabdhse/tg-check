using System.IO;
using System.Runtime.InteropServices;


namespace TgCheck.App.Helpers;

/// <summary>
/// Видео-стикеры Telegram (webm, VP9 с прозрачностью). VLC отдаёт их без прозрачности, поэтому разбираем сами:
/// webm → кадры цвета и отдельные кадры прозрачности (BlockAdditional), оба потока декодирует libvpx,
/// результат — BGRA с прозрачностью прямо в WriteableBitmap.
/// </summary>
public static class Vp9
{
    private const string Version = "1";
    private static readonly Lazy<Api?> Lib = new(Load);

    /// <summary>Декодер доступен (библиотека распаковалась и загрузилась).</summary>
    public static bool IsAvailable => Lib.Value is not null;

    // ---- libvpx: распаковка рядом со средой выполнения VC++ и загрузка
    private sealed class Api
    {
        public required IntPtr Iface;
        public required InitFn Init;
        public required DecodeFn Decode;
        public required GetFrameFn GetFrame;
        public required DestroyFn Destroy;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr IfaceFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int InitFn(IntPtr ctx, IntPtr iface, IntPtr cfg, int flags, int ver);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DecodeFn(IntPtr ctx, IntPtr data, uint size, IntPtr userPriv, int deadline);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr GetFrameFn(IntPtr ctx, ref IntPtr iter);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DestroyFn(IntPtr ctx);

    private static Api? Load()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck", "vpx", Version);
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "vcruntime140.dll", "vpxmd.dll" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) continue;
                using var res = typeof(Vp9).Assembly.GetManifestResourceStream("TgCheck." + name);
                if (res is null) return null;
                using var file = File.Create(path);
                res.CopyTo(file);
            }
            // Сначала среда выполнения из той же папки — иначе libvpx без установленного Visual C++ не загрузится.
            NativeLibrary.Load(Path.Combine(dir, "vcruntime140.dll"));
            var lib = NativeLibrary.Load(Path.Combine(dir, "vpxmd.dll"));
            T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            return new Api
            {
                Iface = Fn<IfaceFn>("vpx_codec_vp9_dx")(),
                Init = Fn<InitFn>("vpx_codec_dec_init_ver"),
                Decode = Fn<DecodeFn>("vpx_codec_decode"),
                GetFrame = Fn<GetFrameFn>("vpx_codec_get_frame"),
                Destroy = Fn<DestroyFn>("vpx_codec_destroy"),
            };
        }
        catch (Exception ex)
        {
            Core.Services.Logging.Write("vp9: " + ex.Message);
            return null;
        }
    }

    /// <summary>Один поток VP9 (цвет или прозрачность).</summary>
    private sealed class Decoder : IDisposable
    {
        private readonly Api _api;
        private IntPtr _ctx;

        public Decoder(Api api)
        {
            _api = api;
            _ctx = Marshal.AllocHGlobal(256);
            for (var i = 0; i < 256; i += 8) Marshal.WriteInt64(_ctx, i, 0);
            // VPX_DECODER_ABI_VERSION разный в версиях libvpx — пробуем известные значения.
            foreach (var abi in new[] { 12, 11, 10, 9, 13, 14 })
                if (_api.Init(_ctx, _api.Iface, IntPtr.Zero, 0, abi) == 0) return;
            throw new InvalidOperationException("libvpx: не удалось создать декодер VP9");
        }

        /// <summary>Декодировать кадр; результат (vpx_image_t*) живёт до следующего вызова. IntPtr.Zero — кадра нет.</summary>
        public unsafe IntPtr Decode(byte[] data)
        {
            fixed (byte* p = data)
                if (_api.Decode(_ctx, (IntPtr)p, (uint)data.Length, IntPtr.Zero, 0) != 0) return IntPtr.Zero;
            var iter = IntPtr.Zero;
            return _api.GetFrame(_ctx, ref iter);
        }

        public void Dispose()
        {
            if (_ctx == IntPtr.Zero) return;
            _api.Destroy(_ctx);
            Marshal.FreeHGlobal(_ctx);
            _ctx = IntPtr.Zero;
        }
    }

    // ---- разбор webm (EBML): кадры цвета и прозрачности с временем
    public sealed record Frame(double Time, byte[] Color, byte[]? Alpha);

    public static List<Frame> Demux(byte[] data)
    {
        var frames = new List<Frame>();
        var scale = 1_000_000.0; // TimecodeScale, нс на единицу (по умолчанию 1 мс)
        void Walk(int pos, int end, long clusterTime)
        {
            while (pos < end)
            {
                if (!ReadVint(data, ref pos, keepMarker: true, out var id) || !ReadVint(data, ref pos, keepMarker: false, out var size, allowUnknown: true)) return;
                var unknown = size < 0;
                var bodyEnd = unknown ? end : (int)Math.Min(end, pos + size);
                switch (id)
                {
                    case 0x18538067: // Segment
                    case 0x1549A966: // Info
                        Walk(pos, bodyEnd, clusterTime);
                        break;
                    case 0x2AD7B1: // TimecodeScale
                        scale = ReadUInt(data, pos, bodyEnd - pos);
                        break;
                    case 0x1F43B675: // Cluster
                        Walk(pos, bodyEnd, 0);
                        break;
                    case 0xE7: // Timecode кластера: следующие блоки этого кластера — от него
                        clusterTime = (long)ReadUInt(data, pos, bodyEnd - pos);
                        break;
                    case 0xA3: // SimpleBlock
                        AddBlock(pos, bodyEnd, null, clusterTime);
                        break;
                    case 0xA0: // BlockGroup: Block и, возможно, прозрачность (BlockAdditions → BlockMore → BlockAdditional)
                        int blockStart = -1, blockEnd = -1;
                        byte[]? alpha = null;
                        FindInGroup(pos, bodyEnd, ref blockStart, ref blockEnd, ref alpha);
                        if (blockStart >= 0) AddBlock(blockStart, blockEnd, alpha, clusterTime);
                        break;
                }
                pos = bodyEnd;
            }
        }

        void FindInGroup(int pos, int end, ref int blockStart, ref int blockEnd, ref byte[]? alpha)
        {
            while (pos < end)
            {
                if (!ReadVint(data, ref pos, true, out var id) || !ReadVint(data, ref pos, false, out var size)) return;
                var bodyEnd = (int)Math.Min(end, pos + size);
                if (id == 0xA1) { blockStart = pos; blockEnd = bodyEnd; }
                else if (id is 0x75A1 or 0xA6) FindInGroup(pos, bodyEnd, ref blockStart, ref blockEnd, ref alpha);
                else if (id == 0xA5) alpha = data[pos..bodyEnd];
                pos = bodyEnd;
            }
        }

        void AddBlock(int pos, int end, byte[]? alpha, long clusterTime)
        {
            if (!ReadVint(data, ref pos, false, out var track) || pos + 3 > end) return;
            if (track != 1) return;
            var rel = (short)((data[pos] << 8) | data[pos + 1]);
            var flags = data[pos + 2];
            pos += 3;
            if ((flags & 0x06) != 0) return; // кадры с «лейсингом» в стикерах не встречаются
            frames.Add(new Frame((clusterTime + rel) * scale / 1e9, data[pos..end], alpha));
        }

        try { Walk(0, data.Length, 0); }
        catch (Exception ex) { Core.Services.Logging.Write("webm: " + ex.Message); }
        frames.Sort((a, b) => a.Time.CompareTo(b.Time));
        return frames;
    }

    private static bool ReadVint(byte[] d, ref int pos, bool keepMarker, out long value, bool allowUnknown = false)
    {
        value = 0;
        if (pos >= d.Length) return false;
        var first = d[pos];
        var len = 1;
        while (len <= 8 && (first & (0x80 >> (len - 1))) == 0) len++;
        if (len > 8 || pos + len > d.Length) return false;
        long v = keepMarker ? first : first & (0xFF >> len);
        var allOnes = (first & (0xFF >> len)) == (0xFF >> len);
        for (var i = 1; i < len; i++)
        {
            v = (v << 8) | d[pos + i];
            allOnes &= d[pos + i] == 0xFF;
        }
        pos += len;
        value = !keepMarker && allowUnknown && allOnes ? -1 : v;
        return true;
    }

    private static double ReadUInt(byte[] d, int pos, int len)
    {
        ulong v = 0;
        for (var i = 0; i < len && i < 8; i++) v = (v << 8) | d[pos + i];
        return v;
    }

    /// <summary>
    /// Проигрыватель одного видео-стикера: кадр по времени, декодирование по порядку (с начала — при повторе),
    /// вывод в BGRA с прозрачностью нужного размера.
    /// </summary>
    public sealed class Player : IDisposable
    {
        private readonly List<Frame> _frames;
        private Decoder? _color, _alpha;
        private int _decoded = -1;

        private Player(List<Frame> frames) => _frames = frames;

        public double Duration { get; private set; }

        public static Player? Create(byte[] webm)
        {
            if (Lib.Value is null) return null;
            var frames = Demux(webm);
            if (frames.Count == 0) return null;
            var player = new Player(frames);
            var step = frames.Count > 1 ? frames[^1].Time - frames[^2].Time : 1 / 30.0;
            player.Duration = Math.Max(0.1, frames[^1].Time + step);
            return player;
        }

        private readonly object _sync = new();

        /// <summary>
        /// Декодировать кадр на момент t (секунды от начала) в BGRA (size×size, с прозрачностью). Можно из фонового потока.
        /// false — кадр тот же, что в прошлый раз, или его нет.
        /// </summary>
        public bool DecodeInto(double t, byte[] bgra, int size)
        {
            lock (_sync)
            {
                if (_disposed) return false;
                var api = Lib.Value!;
                var index = _frames.FindLastIndex(f => f.Time <= t);
                if (index < 0) index = 0;
                if (index == _decoded) return false;
                if (index < _decoded || _color is null) Reset(api);
                IntPtr color = IntPtr.Zero, alpha = IntPtr.Zero;
                // VP9 декодируется только по порядку: догоняем до нужного кадра.
                for (var i = _decoded + 1; i <= index; i++)
                {
                    color = _color!.Decode(_frames[i].Color);
                    alpha = _frames[i].Alpha is { } a && _alpha is not null ? _alpha.Decode(a) : IntPtr.Zero;
                }
                _decoded = index;
                if (color == IntPtr.Zero) return false;
                Draw(color, alpha, bgra, size);
                return true;
            }
        }

        private void Reset(Api api)
        {
            _color?.Dispose();
            _alpha?.Dispose();
            _color = new Decoder(api);
            _alpha = _frames.Any(f => f.Alpha is not null) ? new Decoder(api) : null;
            _decoded = -1;
        }

        // vpx_image_t (x64): fmt,cs,range (3×int), w,h,bit_depth,d_w,d_h,r_w,r_h,x_chroma_shift,y_chroma_shift (9×uint),
        // planes[4] с 48-го байта, stride[4] с 80-го.
        private static unsafe void Draw(IntPtr img, IntPtr alphaImg, byte[] bgra, int size)
        {
            var w = Marshal.ReadInt32(img, 24);
            var h = Marshal.ReadInt32(img, 28);
            var xs = Marshal.ReadInt32(img, 40);
            var ys = Marshal.ReadInt32(img, 44);
            var yP = (byte*)Marshal.ReadIntPtr(img, 48);
            var uP = (byte*)Marshal.ReadIntPtr(img, 56);
            var vP = (byte*)Marshal.ReadIntPtr(img, 64);
            var yS = Marshal.ReadInt32(img, 80);
            var uS = Marshal.ReadInt32(img, 84);
            var vS = Marshal.ReadInt32(img, 88);
            byte* aP = null;
            var aS = 0;
            if (alphaImg != IntPtr.Zero && Marshal.ReadInt32(alphaImg, 24) == w && Marshal.ReadInt32(alphaImg, 28) == h)
            {
                aP = (byte*)Marshal.ReadIntPtr(alphaImg, 48);
                aS = Marshal.ReadInt32(alphaImg, 80);
            }
            // Вписать кадр в квадрат с сохранением пропорций, по центру.
            var k = Math.Min((double)size / w, (double)size / h);
            var dw = Math.Max(1, (int)(w * k));
            var dh = Math.Max(1, (int)(h * k));
            var ox = (size - dw) / 2;
            var oy = (size - dh) / 2;
            var stride = size * 4;
            fixed (byte* dst = bgra)
            {
                new Span<byte>(dst, stride * size).Clear();
                for (var y = 0; y < dh; y++)
                {
                    var sy = y * h / dh;
                    var row = (uint*)(dst + (oy + y) * stride) + ox;
                    var yRow = yP + sy * yS;
                    var uRow = uP + (sy >> ys) * uS;
                    var vRow = vP + (sy >> ys) * vS;
                    var aRow = aP is null ? null : aP + sy * aS;
                    for (var x = 0; x < dw; x++)
                    {
                        var sx = x * w / dw;
                        int a = aRow is null ? 255 : aRow[sx];
                        if (a == 0) { row[x] = 0; continue; }
                        // BT.601, ограниченный диапазон
                        var c = 298 * (yRow[sx] - 16);
                        var d = uRow[sx >> xs] - 128;
                        var e = vRow[sx >> xs] - 128;
                        var r = Math.Clamp((c + 409 * e + 128) >> 8, 0, 255);
                        var g = Math.Clamp((c - 100 * d - 208 * e + 128) >> 8, 0, 255);
                        var b = Math.Clamp((c + 516 * d + 128) >> 8, 0, 255);
                        // Pbgra32: цвет, умноженный на прозрачность
                        row[x] = (uint)(a << 24 | (r * a / 255) << 16 | (g * a / 255) << 8 | (b * a / 255));
                    }
                }
            }
        }

        private bool _disposed;

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _color?.Dispose();
                _alpha?.Dispose();
                _color = _alpha = null;
            }
        }
    }
}
