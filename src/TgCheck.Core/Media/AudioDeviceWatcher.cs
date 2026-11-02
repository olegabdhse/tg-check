using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using TgCheck.Core.Services;

namespace TgCheck.Core.Media;

/// <summary>
/// Сообщает о смене устройства вывода по умолчанию (подключили наушники, включили динамики). WaveOut привязывается к устройству
/// при открытии и за сменой не следит: без этого звук оставался на прежнем устройстве, пока не включишь другую песню.
/// </summary>
public static class AudioDeviceWatcher
{
    private static readonly object Lock = new();
    private static MMDeviceEnumerator? _devices; // держим, пока работает программа: иначе подписка пропадёт
    private static Listener? _listener;
    private static int _changes;

    /// <summary>Устройство по умолчанию сменилось. Из фонового потока, серия изменений — один раз.</summary>
    public static event Action? DefaultOutputChanged;

    public static void Start()
    {
        lock (Lock)
        {
            if (_listener is not null) return;
            _listener = new Listener();
        }
        // В фоновом потоке: подписка COM работает только вне потока интерфейса (STA).
        _ = Task.Run(() =>
        {
            try
            {
                _devices = new MMDeviceEnumerator();
                _devices.RegisterEndpointNotificationCallback(_listener);
            }
            catch (Exception ex) { Logging.Write("audio devices: " + ex.Message); }
        });
    }

    // Windows присылает по одному уведомлению на каждую роль и устройство; ждём, пока всё уляжется.
    private static async Task NotifySoonAsync()
    {
        var change = Interlocked.Increment(ref _changes);
        await Task.Delay(400).ConfigureAwait(false);
        if (change != Volatile.Read(ref _changes)) return;
        try { DefaultOutputChanged?.Invoke(); }
        catch (Exception ex) { Logging.Write("audio devices: " + ex.Message); }
    }

    // Вызовы идут от Windows: долго здесь работать нельзя, поэтому только откладываем обработку.
    private sealed class Listener : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) _ = NotifySoonAsync();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
