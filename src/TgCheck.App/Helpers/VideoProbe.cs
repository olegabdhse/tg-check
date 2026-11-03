using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using SkiaSharp;
using TgCheck.Core.Models;
using TgCheck.Core.Services;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace TgCheck.App.Helpers;

/// <summary>
/// Что нужно Telegram о видео перед отправкой: размер, длительность и кадр для превью. Без этого сервер не знает длину (в пузыре 0:00),
/// а превью у получателей серое. Читает VLC: он понимает все форматы, которые можно отправить. Не вышло — видео уходит и так.
/// </summary>
public static class VideoProbe
{
    private const int ThumbSide = 320;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(6);

    public static async Task<IReadOnlyDictionary<string, VideoInfo>> ProbeAllAsync(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, VideoInfo>();
        foreach (var path in paths) // по одному: кадр декодируется целиком, альбом из десяти видео разом нагрузил бы процессор
            if (await ProbeAsync(path) is { } info) result[path] = info;
        return result;
    }

    public static async Task<VideoInfo?> ProbeAsync(string path)
    {
        try
        {
            if (await VlcRuntime.GetAsync() is not { } vlc) return null;
            return await Task.Run(() => ProbeCoreAsync(vlc, path));
        }
        catch (Exception ex)
        {
            Logging.Write($"video probe: {ex.Message}");
            return null;
        }
    }

    private static async Task<VideoInfo?> ProbeCoreAsync(LibVLC vlc, string path)
    {
        using var media = new Media(vlc, path, FromType.FromPath);
        await media.Parse(MediaParseOptions.ParseLocal, 5000);
        if (media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video) is not { TrackType: TrackType.Video } track) return null;
        var video = track.Data.Video;
        if (video.Width == 0 || video.Height == 0) return null;
        var turn = video.Orientation switch
        {
            VideoOrientation.RightTop => 90,
            VideoOrientation.BottomRight => 180,
            VideoOrientation.LeftBottom => 270,
            _ => 0,
        };
        var sideways = turn is 90 or 270;
        var seconds = Math.Max(0, media.Duration) / 1000.0;
        var width = (int)(sideways ? video.Height : video.Width);
        var height = (int)(sideways ? video.Width : video.Height);
        var (thumb, played) = await GrabFrameAsync(vlc, path, seconds, (int)video.Width, turn);
        // В некоторых файлах длины нет в заголовке — её знает уже запущенный плеер.
        return new VideoInfo(width, height, seconds > 0 ? seconds : played, thumb);
    }

    // Один кадр: играем без звука и окна, кадр приходит в наш буфер. Первая секунда часто чёрная — берём чуть дальше.
    private static async Task<(byte[]? Thumb, double Seconds)> GrabFrameAsync(LibVLC vlc, string path, double seconds, int sourceWidth, int turn)
    {
        var gate = new object();
        var buffer = IntPtr.Zero;
        int width = 0, height = 0;
        byte[]? frame = null;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Вызовы идут из VLC: исключение отсюда завершило бы всю программу.
        VlcPlayer.LibVLCVideoFormatCb formatCb = (ref IntPtr _, IntPtr chroma, ref uint w, ref uint h, ref uint pitches, ref uint lines) =>
        {
            Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
            lock (gate)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                width = (int)w;
                height = (int)h;
                buffer = Marshal.AllocHGlobal(width * height * 4);
            }
            pitches = w * 4;
            lines = h;
            return 1;
        };
        VlcPlayer.LibVLCVideoCleanupCb cleanupCb = (ref IntPtr _) => { };
        VlcPlayer.LibVLCVideoLockCb lockCb = (_, planes) =>
        {
            Monitor.Enter(gate);
            Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };
        VlcPlayer.LibVLCVideoUnlockCb unlockCb = (_, _, _) => Monitor.Exit(gate);
        VlcPlayer.LibVLCVideoDisplayCb displayCb = (_, _) =>
        {
            lock (gate)
            {
                if (frame is not null || buffer == IntPtr.Zero) return;
                frame = new byte[width * height * 4];
                Marshal.Copy(buffer, frame, 0, frame.Length);
            }
            first.TrySetResult();
        };

        using var media = new Media(vlc, path, FromType.FromPath);
        media.AddOption(":no-audio");
        media.AddOption(":no-spu");
        if (seconds > 3) media.AddOption(":start-time=1");
        var player = new VlcPlayer(vlc) { EnableHardwareDecoding = false };
        double played = 0;
        try
        {
            player.SetVideoFormatCallbacks(formatCb, cleanupCb);
            player.SetVideoCallbacks(lockCb, unlockCb, displayCb);
            player.EncounteredError += (_, _) => first.TrySetResult();
            player.EndReached += (_, _) => first.TrySetResult();
            player.Play(media);
            await Task.WhenAny(first.Task, Task.Delay(FrameTimeout));
            if (seconds <= 0) // длина появляется не сразу после первого кадра
                for (var i = 0; i < 15 && player.Length <= 0; i++) await Task.Delay(100);
            played = Math.Max(0, player.Length) / 1000.0;
        }
        finally
        {
            // Остановка ждёт потоки VLC: только после неё можно освободить буфер и отпустить обратные вызовы.
            await Task.Run(() => { try { player.Stop(); } catch { } });
            player.Dispose();
            lock (gate)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }
            GC.KeepAlive(formatCb);
            GC.KeepAlive(cleanupCb);
            GC.KeepAlive(lockCb);
            GC.KeepAlive(unlockCb);
            GC.KeepAlive(displayCb);
        }
        byte[]? captured;
        lock (gate) captured = frame;
        // VLC мог уже повернуть кадр сам: тогда ширина кадра не равна исходной ширине.
        var needTurn = turn is 90 or 270 ? width == sourceWidth ? turn : 0 : turn;
        return (captured is null ? null : Encode(captured, width, height, needTurn), played);
    }

    private static byte[]? Encode(byte[] pixels, int width, int height, int turn)
    {
        var scale = Math.Min(1.0, (double)ThumbSide / Math.Max(width, height));
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
        var sideways = turn is 90 or 270;
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var source = SKImage.FromPixels(info, handle.AddrOfPinnedObject(), width * 4);
            using var surface = SKSurface.Create(new SKImageInfo(sideways ? h : w, sideways ? w : h));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);
            canvas.Translate(surface.Canvas.DeviceClipBounds.Width / 2f, surface.Canvas.DeviceClipBounds.Height / 2f);
            canvas.RotateDegrees(turn);
            canvas.DrawImage(source, new SKRect(-w / 2f, -h / 2f, w / 2f, h / 2f), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
            return data.ToArray();
        }
        finally { handle.Free(); }
    }
}
