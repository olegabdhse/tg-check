using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.Core.Config;

namespace TgCheck.App.ViewModels;

/// <summary>
/// Выбор подключения: встроенный прокси, свой (ссылка MTProxy / SOCKS5) или напрямую.
/// Один и тот же на экране входа, в главном окне и на экране обрыва связи. Ссылка встроенного прокси нигде не показывается.
/// </summary>
public sealed partial class ProxyChoiceViewModel : ObservableObject
{
    private readonly AppEnv _env;
    private readonly Func<string?, Task> _apply;

    /// <param name="apply">Сохранить выбор и переподключиться (null — без прокси, AppEnv.BuiltInProxy — встроенный).</param>
    public ProxyChoiceViewModel(AppEnv env, Func<string?, Task> apply)
    {
        _env = env;
        _apply = apply;
        _proxyText = env.UserProxy ?? "";
    }

    public bool UsesProxy => _env.Proxy is not null;
    public bool CanUseBuiltIn => _env.HasBuiltInProxy && !_env.UsesBuiltInProxy;

    public string Tip => _env.UsesBuiltInProxy ? "Подключено через встроенный прокси"
        : UsesProxy ? "Подключено через свой прокси" : "Подключено напрямую — без прокси";

    /// <summary>Коротко для экрана входа.</summary>
    public string Summary => _env.UsesBuiltInProxy ? "Встроенный прокси" : UsesProxy ? "Свой прокси" : "Без прокси";

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _proxyText;
    [ObservableProperty] private string? _proxyError;

    [RelayCommand]
    private void Toggle()
    {
        ProxyError = null;
        IsOpen = !IsOpen;
    }

    [RelayCommand]
    private Task Apply()
    {
        var link = ProxyText.Trim();
        if (!AppEnv.IsValidProxy(link))
        {
            ProxyError = "Нужна ссылка вида https://t.me/proxy?server=…&port=…&secret=… или socks5://хост:порт";
            return Task.CompletedTask;
        }
        IsOpen = false;
        return _apply(link);
    }

    [RelayCommand]
    private Task BuiltIn()
    {
        IsOpen = false;
        return _apply(AppEnv.BuiltInProxy);
    }

    [RelayCommand]
    private Task NoProxy()
    {
        IsOpen = false;
        return _apply(null);
    }
}
