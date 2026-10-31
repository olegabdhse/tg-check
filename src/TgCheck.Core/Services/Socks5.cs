using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TgCheck.Core.Services;

/// <summary>Минимальный SOCKS5-клиент (RFC 1928/1929) для подключения к Telegram через прокси.</summary>
internal static class Socks5
{
    // Подключение и рукопожатие с прокси — не дольше 12 с: мёртвый прокси иначе «висел» до системного тайм-аута (~21 с и больше),
    // и переподключение стояло.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);

    public static async Task<TcpClient> ConnectAsync(Uri proxy, string host, int port)
    {
        var tcp = Tune(new TcpClient());
        using var cts = new CancellationTokenSource(ConnectTimeout);
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port > 0 ? proxy.Port : 1080, cts.Token);
            var s = tcp.GetStream();
            var ct = cts.Token;

            string? user = null, pass = null;
            if (!string.IsNullOrEmpty(proxy.UserInfo))
            {
                var parts = Uri.UnescapeDataString(proxy.UserInfo).Split(':', 2);
                user = parts[0];
                pass = parts.Length > 1 ? parts[1] : "";
            }

            await s.WriteAsync(user is null ? new byte[] { 5, 1, 0 } : new byte[] { 5, 2, 0, 2 }, ct);
            var hello = await ReadAsync(s, 2, ct);
            if (hello[0] != 5) throw new IOException("SOCKS5: прокси ответил не по протоколу SOCKS5.");

            if (hello[1] == 2)
            {
                if (user is null) throw new IOException("SOCKS5: прокси требует логин и пароль.");
                var u = Encoding.UTF8.GetBytes(user);
                var p = Encoding.UTF8.GetBytes(pass!);
                var auth = new byte[3 + u.Length + p.Length];
                auth[0] = 1;
                auth[1] = (byte)u.Length;
                u.CopyTo(auth, 2);
                auth[2 + u.Length] = (byte)p.Length;
                p.CopyTo(auth, 3 + u.Length);
                await s.WriteAsync(auth, ct);
                if ((await ReadAsync(s, 2, ct))[1] != 0) throw new IOException("SOCKS5: неверный логин или пароль прокси.");
            }
            else if (hello[1] != 0)
            {
                throw new IOException("SOCKS5: прокси не поддерживает нужный способ авторизации.");
            }

            byte[] address;
            byte type;
            if (IPAddress.TryParse(host, out var ip))
            {
                address = ip.GetAddressBytes();
                type = ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1;
            }
            else
            {
                var name = Encoding.ASCII.GetBytes(host);
                address = [(byte)name.Length, .. name];
                type = 3;
            }

            var request = new byte[6 + address.Length];
            request[0] = 5;
            request[1] = 1; // CONNECT
            request[3] = type;
            address.CopyTo(request, 4);
            request[^2] = (byte)(port >> 8);
            request[^1] = (byte)port;
            await s.WriteAsync(request, ct);

            var reply = await ReadAsync(s, 4, ct);
            if (reply[1] != 0) throw new IOException($"SOCKS5: прокси не смог подключиться к Telegram (код {reply[1]}).");
            var skip = reply[3] switch
            {
                1 => 4,
                4 => 16,
                3 => (await ReadAsync(s, 1, ct))[0],
                _ => throw new IOException("SOCKS5: некорректный ответ прокси."),
            };
            await ReadAsync(s, skip + 2, ct);
            return tcp;
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw new TimeoutException("Прокси не ответил за 12 секунд.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Прямое подключение (без прокси или MTProxy) — с тем же тайм-аутом и проверкой «жив ли канал».</summary>
    public static async Task<TcpClient> ConnectDirectAsync(string host, int port)
    {
        var tcp = Tune(new TcpClient());
        using var cts = new CancellationTokenSource(ConnectTimeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            return tcp;
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw new TimeoutException($"{host}:{port} не ответил за 12 секунд.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // TCP keep-alive: соединение, которое молча умерло (сон, смена сети, прокси оборвал простаивающий канал),
    // замечается за ~35 с системой, а не висит до следующего запроса.
    private static TcpClient Tune(TcpClient tcp)
    {
        tcp.NoDelay = true;
        try
        {
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 20);
            tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { /* старая система — без тонкой настройки */ }
        return tcp;
    }

    private static async Task<byte[]> ReadAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }
}
