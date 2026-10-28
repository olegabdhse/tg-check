using System.Net.Sockets;
using TgCheck.Core.Config;
using TL;
using WTelegram;

namespace TgCheck.Core.Services;

/// <summary>Переводит ошибки Telegram/сети в понятные сообщения на русском.</summary>
public static class TgErrors
{
    /// <summary>Текст ошибки для пользователя; полная ошибка пишется в лог.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is not (EnvConfigException or LoginException)) Logging.Write("error: " + ex);
        return Text(ex);
    }

    private static string Text(Exception ex) => ex switch
    {
        AggregateException { InnerException: { } inner } => Text(inner),
        RpcException r => DescribeRpc(r),
        EnvConfigException e => e.Message,
        LoginException l => l.Message,
        IOException { Message: var m } when m.StartsWith("SOCKS5", StringComparison.Ordinal) => m,
        SocketException or IOException or HttpRequestException or TimeoutException => NoConnection,
        WTException w when w.InnerException is { } inner => Text(inner),
        WTException w => w.Message,
        _ => ex.Message,
    };

    private const string NoConnection =
        "Не удаётся подключиться к серверам Telegram — соединение обрывается.\n\n" +
        "Что проверить:\n" +
        "• интернет работает и официальный Telegram Desktop на этом ПК подключается;\n" +
        "• если Telegram у провайдера работает плохо — включите VPN или пропишите прокси в .env: " +
        "TG_PROXY=socks5://хост:порт или MTProxy-ссылку https://t.me/proxy?server=…;\n" +
        "• антивирус/фаервол не блокирует TgCheck.exe.";

    public static bool IsSessionDead(Exception ex) =>
        ex is RpcException { Code: 401 } ||
        ex is RpcException { Message: "AUTH_KEY_UNREGISTERED" or "SESSION_REVOKED" or "SESSION_EXPIRED" or "USER_DEACTIVATED" };

    private static string DescribeRpc(RpcException r)
    {
        if (r.Code == 420) return $"Слишком много запросов. Подождите {Wait(r.X)} и попробуйте снова.";

        return r.Message switch
        {
            "PHONE_NUMBER_INVALID" => "Неверный номер. Укажите его в международном формате, например +79991234567.",
            "PHONE_NUMBER_BANNED" => "Этот номер заблокирован в Telegram.",
            "PHONE_NUMBER_UNOCCUPIED" => "На этот номер не зарегистрирован аккаунт Telegram.",
            "PHONE_CODE_INVALID" => "Неверный код.",
            "PHONE_CODE_EMPTY" => "Введите код.",
            "PHONE_CODE_EXPIRED" => "Срок действия кода истёк. Запросите новый.",
            "PASSWORD_HASH_INVALID" => "Неверный пароль двухэтапной аутентификации.",
            "API_ID_INVALID" or "API_ID_PUBLISHED_FLOOD" => "Неверные TG_API_ID / TG_API_HASH в файле .env.",
            "AUTH_KEY_UNREGISTERED" or "SESSION_REVOKED" or "SESSION_EXPIRED" => "Сессия завершена. Войдите заново.",
            "AUTH_TOKEN_EXPIRED" or "AUTH_TOKEN_INVALID" => "QR-код устарел. Обновите его.",
            "STARGIFT_CONVERT_TOO_OLD" => "Время, в течение которого подарок можно было продать, истекло.",
            "STARGIFT_NOT_FOUND" or "STARGIFT_INVALID" => "Подарок не найден — возможно, он уже продан.",
            "STARGIFT_ALREADY_CONVERTED" => "Этот подарок уже продан.",
            "STARGIFT_ALREADY_UPGRADED" => "Подарок улучшен до уникального — продать за звёзды его нельзя.",
            "STARGIFT_ALREADY_REFUNDED" => "За этот подарок уже вернули звёзды.",
            "MESSAGE_ID_INVALID" => "Не удалось найти сообщение с подарком.",
            _ => $"Ошибка Telegram: {r.Message} (код {r.Code})",
        };
    }

    private static string Wait(int seconds) => seconds switch
    {
        < 60 => $"{seconds} сек.",
        < 3600 => $"{(seconds + 59) / 60} мин.",
        _ => $"{seconds / 3600} ч {seconds % 3600 / 60} мин.",
    };
}

/// <summary>Ошибка процесса входа, понятная пользователю.</summary>
public sealed class LoginException(string message) : Exception(message);
