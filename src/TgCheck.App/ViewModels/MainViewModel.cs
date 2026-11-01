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

    // Каждое подключение — со своим номером. Пока одно шло (обрыв, повторные попытки), а его заменили новым (выбрали прокси,
    // нажали «повторить»), старое падало на уже закрытом клиенте и поверх нового показывало ошибку или экран входа —
    // выглядело так, будто сессия потерялась. Теперь устаревшее подключение молча завершается.
    private int _generation;

    public async Task InitializeAsync()
    {
        var generation = ++_generation;
        bool Stale() => generation != _generation;
        CurrentPage = StatusViewModel.Busy("Подключение к Telegram…");

        if (_tg is not null)
        {
            await _tg.DisposeAsync();
            _tg = null;
        }
        if (Stale()) return;

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
                    if (Stale()) return;
                    if (early is not null) early.ConnectionStatus = $"Соединение… (попытка {attempt} из 6)";
                    else CurrentPage = StatusViewModel.Busy($"Подключение к Telegram{proxyNote}… попытка {attempt} из 6");
                }))
            {
                if (Stale()) { early?.Dispose(); return; }
                ShowProfile(early);
                return;
            }
        }
        catch (Exception ex)
        {
            if (Stale()) { early?.Dispose(); Logging.Write($"connection: прежнее подключение остановлено ({ex.GetType().Name})"); return; }
            early?.Dispose();
            CurrentPage = StatusViewModel.ConnectionError("Не удалось подключиться", TgErrors.Describe(ex), InitializeAsync,
                new ProxyChoiceViewModel(_env!, ChangeProxyAsync));
            return;
        }

        if (Stale()) { early?.Dispose(); return; }
        early?.Dispose();
        ShowLogin();
    }

    /// <summary>Свой прокси (null — без прокси): сохраняем и переподключаемся; чаты сразу снова из кэша.</summary>
    private Task ChangeProxyAsync(string? proxy)
    {
        if (_env is null) return Task.CompletedTask;
        AppEnv.SaveUserProxy(_env.DataDirectory, proxy);
        try { _tg?.FlushDialogsCache(); } catch { }
        if (CurrentPage is ShellViewModel shell) shell.Dispose();
        if (CurrentPage is LoginViewModel login) login.Cancel();
        return InitializeAsync();
    }

    private void ShowLogin()
    {
        if (_tg is null) return;
        CurrentPage = new LoginViewModel(_tg, _env?.DefaultPhone, ShowProfile)
        {
            Proxy = _env is null ? null : new ProxyChoiceViewModel(_env, ChangeProxyAsync),
        };
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
        try { _tg?.FlushDialogsCache(); } catch { } // список чатов — каким он был при выходе
        if (CurrentPage is LoginViewModel login) login.Cancel();
        if (CurrentPage is ShellViewModel shell) shell.Dispose();
        if (_tg is not null) await _tg.DisposeAsync();
    }
}
