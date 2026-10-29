using CommunityToolkit.Mvvm.ComponentModel;
using TgCheck.Core.Config;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Навигация между экранами: загрузка → вход → главное окно (чаты, профили).</summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private TelegramService? _tg;
    private AppEnv? _env;

    [ObservableProperty]
    private object? _currentPage;

    public async Task InitializeAsync()
    {
        CurrentPage = StatusViewModel.Busy("Подключение к Telegram…");

        if (_tg is not null)
        {
            await _tg.DisposeAsync();
            _tg = null;
        }

        try
        {
            _env = AppEnv.Load();
        }
        catch (EnvConfigException ex)
        {
            CurrentPage = StatusViewModel.Error("Не настроен файл .env", ex.Message, InitializeAsync, showAppFolder: true);
            return;
        }

        _tg = new TelegramService(_env);
        var proxyNote = _tg.UsesProxy ? " через прокси" : "";

        // Вход сохранён — главное окно с чатами из кэша показываем сразу, а с Telegram соединяемся в фоне
        // (как официальные клиенты: не ждём сеть, чтобы показать уже известное).
        ShellViewModel? early = null;
        if (_tg.UserId != 0)
        {
            early = new ShellViewModel(_tg, _env, ShowLogin, ChangeProxyAsync) { ConnectionStatus = "Соединение…" };
            Helpers.Anim.InstantUntil = DateTime.UtcNow.AddSeconds(1.5);
            CurrentPage = early;
            _ = early.Chats.ShowCachedAsync();
            _ = early.MyProfile.ShowCachedAsync();
        }
        else CurrentPage = StatusViewModel.Busy($"Подключение к Telegram{proxyNote}…");

        try
        {
            if (await _tg.TryRestoreSessionAsync(attempt =>
                {
                    if (early is not null) early.ConnectionStatus = $"Соединение… (попытка {attempt} из 4)";
                    else CurrentPage = StatusViewModel.Busy($"Подключение к Telegram{proxyNote}… попытка {attempt} из 4");
                }))
            {
                ShowProfile(early);
                return;
            }
        }
        catch (Exception ex)
        {
            early?.Dispose();
            var env = _env!;
            CurrentPage = StatusViewModel.ConnectionError("Не удалось подключиться", TgErrors.Describe(ex), InitializeAsync,
                env.Proxy, proxy => Core.Config.AppEnv.SaveUserProxy(env.DataDirectory, proxy));
            return;
        }

        early?.Dispose();
        ShowLogin();
    }

    /// <summary>Свой прокси (null — без прокси): сохраняем и переподключаемся; чаты сразу снова из кэша.</summary>
    private Task ChangeProxyAsync(string? proxy)
    {
        if (_env is null) return Task.CompletedTask;
        AppEnv.SaveUserProxy(_env.DataDirectory, proxy);
        if (CurrentPage is ShellViewModel shell) shell.Dispose();
        return InitializeAsync();
    }

    private void ShowLogin()
    {
        if (_tg is null) return;
        CurrentPage = new LoginViewModel(_tg, _env?.DefaultPhone, ShowProfile);
    }

    private void ShowProfile() => ShowProfile(null);

    /// <summary>Главное окно; existing — уже показанное из кэша до подключения.</summary>
    private void ShowProfile(ShellViewModel? existing)
    {
        if (_tg is null || _env is null) return;
        var shell = existing ?? new ShellViewModel(_tg, _env, ShowLogin, ChangeProxyAsync);
        shell.ConnectionStatus = null;
        _tg.MarkSessionGood();
        if (!ReferenceEquals(CurrentPage, shell)) CurrentPage = shell;
        _ = shell.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Logging.Write("exit: программа закрывается, сессия сохраняется");
        if (CurrentPage is LoginViewModel login) login.Cancel();
        if (CurrentPage is ShellViewModel shell) shell.Dispose();
        if (_tg is not null) await _tg.DisposeAsync();
    }
}
