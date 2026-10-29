using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;

namespace TgCheck.App.ViewModels;

/// <summary>Экран «загрузка» или «ошибка» с кнопкой повтора.</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    private readonly Func<Task>? _retry;

    private StatusViewModel(string title, string? message, bool isBusy, Func<Task>? retry, bool showAppFolder)
    {
        Title = title;
        Message = message;
        IsBusy = isBusy;
        _retry = retry;
        ShowAppFolder = showAppFolder;
    }

    public string Title { get; }
    public string? Message { get; }
    public bool IsBusy { get; }
    public bool CanRetry => _retry is not null;
    public bool ShowAppFolder { get; }

    public static StatusViewModel Busy(string title) => new(title, null, true, null, false);

    public static StatusViewModel Error(string title, string message, Func<Task> retry, bool showAppFolder = false) =>
        new(title, message, false, retry, showAppFolder);

    [RelayCommand]
    private Task Retry() => _retry?.Invoke() ?? Task.CompletedTask;

    // ---- прокси на экране обрыва соединения
    private Action<string?>? _saveProxy;

    /// <summary>Не удалось подключиться: кроме «Повторить» — смена прокси (ссылка MTProxy/SOCKS5) или подключение без него.</summary>
    /// Встроенный прокси (из сборки) в поле не показывается — только свой.
    public static StatusViewModel ConnectionError(string title, string message, Func<Task> retry, Core.Config.AppEnv env) =>
        new(title, message, false, retry, false)
        {
            _saveProxy = proxy => Core.Config.AppEnv.SaveUserProxy(env.DataDirectory, proxy),
            ProxyText = env.UserProxy ?? "",
            UsesProxy = env.Proxy is not null,
            CanUseBuiltIn = env.HasBuiltInProxy && !env.UsesBuiltInProxy,
            ProxyState = env.UsesBuiltInProxy ? "Сейчас: встроенный прокси" : env.UserProxy is not null ? "Сейчас: свой прокси" : "Сейчас: без прокси",
        };

    public bool CanChangeProxy => _saveProxy is not null;
    public bool UsesProxy { get; private init; }
    public bool CanUseBuiltIn { get; private init; }
    public string? ProxyState { get; private init; }

    [RelayCommand]
    private Task BuiltInProxy()
    {
        _saveProxy?.Invoke(Core.Config.AppEnv.BuiltInProxy);
        return Retry();
    }
    [ObservableProperty] private string _proxyText = "";
    [ObservableProperty] private string? _proxyError;

    [RelayCommand]
    private Task ApplyProxy()
    {
        var link = ProxyText.Trim();
        if (!Core.Config.AppEnv.IsValidProxy(link))
        {
            ProxyError = "Нужна ссылка вида https://t.me/proxy?server=…&port=…&secret=… или socks5://хост:порт";
            return Task.CompletedTask;
        }
        _saveProxy?.Invoke(link);
        return Retry();
    }

    [RelayCommand]
    private Task NoProxy()
    {
        _saveProxy?.Invoke(null);
        return Retry();
    }

    [RelayCommand]
    private static void OpenAppFolder() => Shell.Open(AppContext.BaseDirectory);
}
