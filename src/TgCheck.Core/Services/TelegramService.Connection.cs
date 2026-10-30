using TL;
using WTelegram;

namespace TgCheck.Core.Services;

// Связь после входа: обрыв (сеть пропала, сон, прокси упал) восстанавливается сам, без перезапуска программы.
public sealed partial class TelegramService
{
    /// <summary>Связь с Telegram: false — потеряна, идёт переподключение; true — восстановлена (пропущенное надо досинхронизировать).</summary>
    public event Action<bool>? ConnectionChanged;

    private volatile bool _online;
    private volatile bool _disposed;
    private int _reconnecting;
    private Timer? _watchdog;

    /// <summary>Вход выполнен — с этого момента следим за связью.</summary>
    private void StartWatchdog()
    {
        _online = true;
        if (_watchdog is not null) return;
        _watchdog = new Timer(_ => _ = CheckConnectionAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _ = Task.Run(async () => { await Task.Delay(2000); await CheckConnectionAsync(); });
    }

    // Библиотека сама переподключается несколько раз; если сдалась — сообщает ReactorError.
    private Task OnOtherAsync(IObject other)
    {
        if (other is ReactorError err && _online)
        {
            Logging.Write($"connection: обрыв ({err.Exception?.Message})");
            _ = Task.Run(ReconnectAsync);
        }
        return Task.CompletedTask;
    }

    // Раз в 30 с (и при появлении сети): живо ли соединение. Нет ответа за 15 с — переподключаемся.
    private async Task CheckConnectionAsync()
    {
        if (!_online || _disposed || Volatile.Read(ref _reconnecting) != 0) return;
        try
        {
            var ping = _client.Updates_GetState();
            if (await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(15))) != ping) throw new TimeoutException("нет ответа 15 с");
            await ping;
        }
        catch (Exception ex) when (!TgErrors.IsSessionDead(ex))
        {
            Logging.Write($"connection: проверка не прошла ({ex.Message})");
            await ReconnectAsync();
        }
        catch { /* сессия завершена на сервере — это обработает интерфейс при следующем запросе */ }
    }

    private async Task ReconnectAsync()
    {
        if (Interlocked.Exchange(ref _reconnecting, 1) != 0) return;
        try
        {
            ConnectionChanged?.Invoke(false);
            for (var attempt = 1; !_disposed; attempt++)
            {
                try
                {
                    await ResetClientAsync(deleteSession: false);
                    var client = _client;
                    var connect = Task.Run(() => client.ConnectAsync());
                    if (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(20))) != connect)
                        throw new TimeoutException("Telegram не ответил за 20 секунд.");
                    await connect;
                    if (client.UserId == 0) return; // сессии нет — переподключение не поможет
                    await client.Users_GetUsers(InputUser.Self);
                    Logging.Write($"connection: восстановлено (попытка {attempt})");
                    break;
                }
                catch (Exception ex) when (!TgErrors.IsSessionDead(ex))
                {
                    Logging.Write($"connection: попытка {attempt} не удалась ({ex.Message})");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 + attempt * 3)));
                }
            }
        }
        catch (Exception ex)
        {
            Logging.Write($"connection: переподключение остановлено ({ex.Message})");
            return;
        }
        finally { Volatile.Write(ref _reconnecting, 0); }
        if (_disposed) return;
        Media.ForgetPending();
        ConnectionChanged?.Invoke(true);
        ResyncNeeded?.Invoke();
    }

    private void StopWatchdog()
    {
        _disposed = true;
        _watchdog?.Dispose();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
    }
}
