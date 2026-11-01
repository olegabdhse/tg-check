namespace TgCheck.Core.Models;

/// <summary>Сеанс аккаунта (устройство, где выполнен вход) — как «Активные сеансы» в Telegram.</summary>
public sealed class SessionInfo
{
    public long Hash { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsOfficialApp { get; init; }
    /// <summary>Вход ещё не подтверждён (ждёт пароль двухэтапной проверки).</summary>
    public bool IsPending { get; init; }
    public string Device { get; init; } = "";
    public string Platform { get; init; } = "";
    public string SystemVersion { get; init; } = "";
    public string AppName { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Country { get; init; } = "";
    public string Region { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public DateTime ActiveUtc { get; init; }
}
