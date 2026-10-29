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
        try
        {
            var proxyNote = _tg.UsesProxy ? " через прокси" : "";
            CurrentPage = StatusViewModel.Busy($"Подключение к Telegram{proxyNote}…");
            if (await _tg.TryRestoreSessionAsync(attempt =>
                    CurrentPage = StatusViewModel.Busy($"Подключение к Telegram{proxyNote}… попытка {attempt} из 4")))
            {
                ShowProfile();
                return;
            }
        }
        catch (Exception ex)
        {
            CurrentPage = StatusViewModel.Error("Не удалось подключиться", TgErrors.Describe(ex), InitializeAsync);
            return;
        }

        ShowLogin();
    }

    private void ShowLogin()
    {
        if (_tg is null) return;
        CurrentPage = new LoginViewModel(_tg, _env?.DefaultPhone, ShowProfile);
    }

    private void ShowProfile()
    {
        if (_tg is null || _env is null) return;
        var shell = new ShellViewModel(_tg, _env, ShowLogin);
        _tg.MarkSessionGood();
        CurrentPage = shell;
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
