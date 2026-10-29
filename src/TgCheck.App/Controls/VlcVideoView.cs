using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using TgCheck.App.Helpers;

namespace TgCheck.App.Controls;

/// <summary>
/// Видеоплеер на движке VLC: играет любые форматы (webm, HEVC, AV1, mkv…). Кадры VLC декодирует в память,
/// а показываются они обычной картинкой WPF — поверх можно класть кнопки, скругления и т.п.
/// API — как у MediaElement в той части, что нужна просмотрщику.
/// </summary>
public sealed class VlcVideoView : Image
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(VlcVideoView), new PropertyMetadata(null, (d, e) => ((VlcVideoView)d).OnSourceChanged((string?)e.NewValue)));

    /// <summary>Путь к локальному файлу видео.</summary>
    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<string>? MediaFailed;

    private MediaPlayer? _player;
    private Media? _media;
    private int _version;
    private bool _playRequested;
    private bool _opened;
    private Task _stopTask = Task.CompletedTask;

    // Кадр: VLC пишет в _buffer (под _frameLock), интерфейс копирует его в _bitmap.
    private readonly object _frameLock = new();
    private IntPtr _buffer;
    private int _width, _height, _stride;
    private WriteableBitmap? _bitmap;
    private int _framePending;

    // Делегаты держим в полях: VLC вызывает их из своих потоков, сборщик мусора не должен их собрать.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    public VlcVideoView()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;
        Unloaded += (_, _) => Stop();
    }

    // ===================================================================== Управление

    public bool IsMuted
    {
        get => _player?.Mute ?? false;
        set { if (_player is not null) _player.Mute = value; _muted = value; }
    }
    private bool _muted;

    /// <summary>Длительность (null — ещё неизвестна).</summary>
    public TimeSpan? Duration => _player is { Length: > 0 } p ? TimeSpan.FromMilliseconds(p.Length) : null;

    public TimeSpan Position
    {
        get => _player is { } p && p.Time > 0 ? TimeSpan.FromMilliseconds(p.Time) : TimeSpan.Zero;
        set
        {
            if (_player is not { } p) return;
            if (p.State == VLCState.Ended) { Replay(value); return; }
            p.Time = (long)value.TotalMilliseconds;
        }
    }

    public void Play()
    {
        _playRequested = true;
        if (_player is not { } p) return;
        if (p.State == VLCState.Ended) { Replay(TimeSpan.Zero); return; }
        p.Play();
    }

    public void Pause()
    {
        _playRequested = false;
        if (_player is { IsPlaying: true } p) p.SetPause(true);
    }

    /// <summary>Остановить и освободить файл.</summary>
    public void Stop()
    {
        _playRequested = false;
        _version++;
        var player = _player;
        var media = _media;
        _media = null;
        // Stop у VLC может ждать свои потоки — не в потоке интерфейса.
        if (player is not null) _stopTask = Task.Run(() => { player.Stop(); media?.Dispose(); });
        Source = null;
        _opened = false;
    }

    // После конца файла VLC нужно перезапустить (у него нет «перемотать в начало» из состояния Ended).
    private void Replay(TimeSpan from)
    {
        if (_player is not { } p) return;
        _ = Task.Run(() =>
        {
            p.Stop();
            p.Play();
            if (from > TimeSpan.Zero) p.Time = (long)from.TotalMilliseconds;
        });
    }

    // ===================================================================== Источник

    private async void OnSourceChanged(string? path)
    {
        Stop();
        if (string.IsNullOrEmpty(path)) return;
        var version = _version;
        var vlc = await VlcRuntime.GetAsync();
        await _stopTask; // прежний файл остановлен — не остановим заодно новый
        if (version != _version) return;
        if (vlc is null) { MediaFailed?.Invoke(this, "Не удалось запустить видеоплеер."); return; }

        if (_player is null)
        {
            _player = new MediaPlayer(vlc) { EnableHardwareDecoding = true };
            _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
            _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
            _player.Playing += (_, _) => Ui(() =>
            {
                if (_opened) return;
                _opened = true;
                MediaOpened?.Invoke(this, EventArgs.Empty);
            });
            _player.EndReached += (_, _) => Ui(() => MediaEnded?.Invoke(this, EventArgs.Empty));
            _player.EncounteredError += (_, _) => Ui(() => MediaFailed?.Invoke(this, "Ошибка воспроизведения."));
        }
        _player.Mute = _muted;
        _media = new Media(vlc, path, FromType.FromPath);
        _player.Media = _media;
        _opened = false;
        if (_playRequested) _player.Play();
    }

    private void Ui(Action action) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);

    // ===================================================================== Кадры (потоки VLC)

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // RV32 — 32 бита на пиксель, порядок B,G,R,X: в WPF это Bgr32.
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        lock (_frameLock)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _width = (int)width;
            _height = (int)height;
            _stride = _width * 4;
            _buffer = Marshal.AllocHGlobal(_stride * _height);
        }
        pitches = (uint)_stride;
        lines = (uint)_height;
        int w = _width, h = _height;
        Dispatcher.Invoke(() =>
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            Source = _bitmap;
        });
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_frameLock)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_frameLock);
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) => Monitor.Exit(_frameLock);

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        // Кадров может быть больше, чем успевает интерфейс, — копируем только последний.
        if (Interlocked.Exchange(ref _framePending, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _framePending, 0);
            lock (_frameLock)
            {
                if (_bitmap is null || _buffer == IntPtr.Zero || _bitmap.PixelWidth != _width || _bitmap.PixelHeight != _height) return;
                _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _buffer, _stride * _height, _stride);
            }
        });
    }
}
