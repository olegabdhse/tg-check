using TL;
using WTelegram;

namespace TgCheck.Core.Services;

// Связь после входа: обрыв (сеть пропала, сон, прокси упал) восстанавливается сам, без перезапуска программы.
public sealed partial class TelegramService
{
    /// <summary>Связь с Telegram: false — потеряна, идёт переподключение; true — восстановлена (пропущенное надо досинхронизировать).</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Связь восстановлена другим способом (выбранный не проходил): «встроенный прокси», «напрямую»…</summary>
    public event Action<string>? RouteChanged;

    private volatile bool _online;
    private volatile bool _disposed;
    private int _reconnecting;
    private Timer? _watchdog;
    private long _lastAlive = Environment.TickCount64;
    private int _failedChecks;

    /// <summary>Из сети что-то пришло — связь жива.</summary>
    private void MarkAlive()
    {
        Volatile.Write(ref _lastAlive, Environment.TickCount64);
        _failedChecks = 0;
    }

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
        if (e.IsAvailable) _ = Task.Run(async () => { await Task.Delay(2000); await CheckConnectionAsync(force: true); });
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

    // Раз в 30 с (и при появлении сети): живо ли соединение. Раньше хватало одного ответа дольше 15 с, чтобы пересоздать
    // всё подключение, — на медленном или загруженном прокси (качается песня, видео) это случалось при живой связи,
    // и переподключение обрывало загрузки и само мешало. Теперь: недавно что-то пришло — проверять не нужно;
    // ответа нет 25 с — ещё не обрыв; переподключаемся после двух неудачных проверок подряд (или сразу после смены сети).
    private async Task CheckConnectionAsync(bool force = false)
    {
        if (!_online || _disposed || Volatile.Read(ref _reconnecting) != 0) return;
        _tunnelGuard.Tick(Environment.TickCount64);
        _ = ReturnToTunnelAsync();
        if (!force && Environment.TickCount64 - Volatile.Read(ref _lastAlive) < 25_000) return;
        try
        {
            var ping = _client.Updates_GetState();
            if (await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(25))) != ping) throw new TimeoutException("нет ответа 25 с");
            await ping;
            MarkAlive();
        }
        catch (Exception ex) when (!TgErrors.IsSessionDead(ex))
        {
            var failed = Interlocked.Increment(ref _failedChecks);
            Logging.Write($"connection: проверка не прошла ({ex.Message}), подряд: {failed}");
            if (force || failed >= 2) await ReconnectAsync();
        }
        catch { /* сессия завершена на сервере — это обработает интерфейс при следующем запросе */ }
    }

    // Один неудачный запуск или сбой уводил на встроенный прокси, и программа оставалась на нём, пока связь не оборвётся совсем:
    // песни и медиа там грузятся в десятки раз медленнее. Раз в две минуты проверяем, не ожил ли туннель, и возвращаемся на него.
    private const long TunnelRecheckMs = 120_000;
    private long _lastTunnelCheck = Environment.TickCount64;
    private int _checkingTunnel;
    private readonly TunnelGuard _tunnelGuard = new();

    private bool OnTunnel => CurrentProxy is { } proxy && XrayUrls.Contains(proxy);

    // Туннель не справился: на первый способ после туннелей и пауза перед следующей попыткой вернуться.
    private void LeaveTunnel(string why)
    {
        var minutes = _tunnelGuard.Failed(Environment.TickCount64);
        _route = XrayUrls.Count % Routes.Count;
        Logging.Write($"connection: {why}, на {minutes} мин. переходим на «{RouteName}»");
    }

    // Файлы зависают при живом основном соединении: сторож связи этого не видит, а короткая проверка туннеля проходит.
    private void OnFilesStuck()
    {
        if (_disposed || !TunnelsFirst || !OnTunnel || !_tunnelGuard.FilesStuck(Environment.TickCount64)) return;
        LeaveTunnel("файлы через туннель зависают");
        _ = Task.Run(ReconnectAsync);
    }

    private async Task ReturnToTunnelAsync()
    {
        var tunnels = XrayUrls;
        if (tunnels.Count == 0 || !TunnelsFirst || tunnels.Contains(CurrentProxy!)) return;
        if (!_tunnelGuard.CanReturn(Environment.TickCount64)) return;
        if (Environment.TickCount64 - Volatile.Read(ref _lastTunnelCheck) < TunnelRecheckMs) return;
        if (Interlocked.Exchange(ref _checkingTunnel, 1) != 0) return;
        try
        {
            Volatile.Write(ref _lastTunnelCheck, Environment.TickCount64);
            using var stop = new CancellationTokenSource(TunnelProbeTimeout);
            var answers = await Task.WhenAll(tunnels.Select(url => ProbeTunnelAsync(url, stop.Token)));
            if (answers.All(a => a is null) || _disposed) return;
            Logging.Write($"connection: туннель снова отвечает, уходим с маршрута «{RouteName}»");
            _tunnelGuard.Returned(Environment.TickCount64);
            await ReconnectAsync();
        }
        catch (Exception ex) { Logging.Write("connection: проверка туннеля: " + ex.Message); }
        finally { Volatile.Write(ref _checkingTunnel, 0); }
    }

    private async Task ReconnectAsync()
    {
        if (Interlocked.Exchange(ref _reconnecting, 1) != 0) return;
        var routeBefore = RouteName;
        if (OnTunnel && _tunnelGuard.InTrial(Environment.TickCount64)) LeaveTunnel("связь через туннель оборвалась вскоре после возврата");
        try
        {
            ConnectionChanged?.Invoke(false);
            for (var attempt = 1; !_disposed; attempt++)
            {
                try
                {
                    if (attempt == 1) await PickWorkingRouteAsync(); // связь пропала — сразу на тот туннель, что отвечает
                    await ResetClientAsync(deleteSession: false);
                    var client = _client;
                    var connect = Task.Run(() => client.ConnectAsync());
                    if (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(20))) != connect)
                        throw new TimeoutException("Telegram не ответил за 20 секунд.");
                    await connect;
                    if (client.UserId == 0) return; // сессии нет — переподключение не поможет
                    await client.Users_GetUsers(InputUser.Self);
                    Logging.Write($"connection: восстановлено (попытка {attempt})");
                    MarkAlive();
                    break;
                }
                catch (Exception ex) when (!TgErrors.IsSessionDead(ex))
                {
                    Logging.Write($"connection: попытка {attempt} не удалась ({ex.Message})");
                    if (OnTunnel && _tunnelGuard.InTrial(Environment.TickCount64)) LeaveTunnel("вернуться на туннель не удалось");
                    else if (attempt % 2 == 0) NextRoute(); // две неудачи подряд — пробуем другой способ (встроенный прокси / напрямую)
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
        if (RouteName != routeBefore) RouteChanged?.Invoke(RouteName);
        ResyncNeeded?.Invoke();
    }

    private void StopWatchdog()
    {
        _disposed = true;
        _watchdog?.Dispose();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
    }
}
