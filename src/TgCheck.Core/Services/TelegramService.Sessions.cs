using TgCheck.Core.Models;
using TL;

namespace TgCheck.Core.Services;

// Активные сеансы: где выполнен вход в аккаунт, завершить один или все, кроме этого.
public sealed partial class TelegramService
{
    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync()
    {
        var res = await _client.Account_GetAuthorizations().ConfigureAwait(false);
        return res.authorizations.Select(a => new SessionInfo
        {
            Hash = a.hash,
            IsCurrent = a.flags.HasFlag(Authorization.Flags.current),
            IsOfficialApp = a.flags.HasFlag(Authorization.Flags.official_app),
            IsPending = a.flags.HasFlag(Authorization.Flags.password_pending),
            Device = a.device_model ?? "",
            Platform = a.platform ?? "",
            SystemVersion = a.system_version ?? "",
            AppName = a.app_name ?? "",
            AppVersion = a.app_version ?? "",
            Ip = a.ip ?? "",
            Country = a.country ?? "",
            Region = a.region ?? "",
            CreatedUtc = a.date_created,
            ActiveUtc = a.date_active,
        }).OrderByDescending(s => s.IsCurrent).ThenByDescending(s => s.ActiveUtc).ToList();
    }

    /// <summary>Завершить сеанс на другом устройстве.</summary>
    public Task TerminateSessionAsync(long hash) => _client.Account_ResetAuthorization(hash);

    /// <summary>Завершить все сеансы, кроме этого.</summary>
    public Task TerminateOtherSessionsAsync() => _client.Auth_ResetAuthorizations();
}
