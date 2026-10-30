using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

public enum LoginMode { Phone, Qr }

public enum PhoneStage { Phone, Code, Password, Email, EmailCode }

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly TelegramService _tg;
    private readonly Action _onLoggedIn;
    private CancellationTokenSource? _qrCts;
    private Task? _qrTask;
    private TaskCompletionSource<string>? _qrPassword;
    private Country? _chosenCountry;
    private bool _syncingPhone;

    public LoginViewModel(TelegramService tg, string? defaultPhone, Action onLoggedIn)
    {
        _tg = tg;
        _onLoggedIn = onLoggedIn;

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(PhoneFormat.Countries);
        view.Filter = o => o is Country c && (CountrySearch.Length == 0 ||
            c.Name.Contains(CountrySearch, StringComparison.CurrentCultureIgnoreCase) ||
            c.CodeText.Contains(CountrySearch.TrimStart('+').Insert(0, "+"), StringComparison.Ordinal));
        CountriesView = view;

        var digits = PhoneFormat.Digits(defaultPhone);
        if (digits.Length > 0)
        {
            var (code, rest) = PhoneFormat.Split(digits);
            _countryCode = code;
            _nationalNumber = rest;
        }
        else
        {
            var c = PhoneFormat.ByRegion() ?? PhoneFormat.Countries[0];
            _chosenCountry = c;
            _countryCode = c.Code;
        }
        UpdateCountry();
        _nationalNumber = PhoneFormat.Format(PhoneFormat.Digits(_nationalNumber), Country?.Mask);
    }

    /// <summary>Попросить view поставить фокус в поле номера.</summary>
    public event Action? FocusNumberRequested;

    public ImageSource? Logo => Images.AppLogo;

    /// <summary>Подключение для входа: встроенный прокси, свой или напрямую.</summary>
    public ProxyChoiceViewModel? Proxy { get; init; }

    [ObservableProperty] private LoginMode _mode = LoginMode.Phone;
    [ObservableProperty] private PhoneStage _stage = PhoneStage.Phone;
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] private string? _passwordHint;
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SubmitCommand), nameof(SubmitQrPasswordCommand))]
    private bool _isBusy;

    [ObservableProperty] private ImageSource? _qrImage;
    [ObservableProperty] private string _qrStatus = "";
    [ObservableProperty] private bool _qrNeedsPassword;
    [ObservableProperty] private bool _qrFailed;

    // ===================================================================== Номер телефона

    /// <summary>Код страны без «+».</summary>
    [ObservableProperty] private string _countryCode;

    /// <summary>Номер без кода страны, уже отформатированный по маске.</summary>
    [ObservableProperty] private string _nationalNumber;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CountryName))]
    private Country? _country;

    [ObservableProperty] private bool _isCountryPickerOpen;
    [ObservableProperty] private string _countrySearch = "";

    public ICollectionView CountriesView { get; }

    public string CountryName => Country?.Name ?? (CountryCode.Length == 0 ? "Выберите страну" : "Неизвестный код страны");

    /// <summary>Введённая часть номера (невидимая) + остаток маски серым — подсказка, сколько цифр ещё ввести.</summary>
    public string MaskTyped => NationalNumber;
    public string MaskRest => PhoneFormat.MaskRest(PhoneFormat.Digits(NationalNumber), Country?.Mask);

    public string FullPhone => "+" + CountryCode + PhoneFormat.Digits(NationalNumber);

    partial void OnCountryCodeChanged(string value)
    {
        if (_syncingPhone) return;
        var digits = PhoneFormat.Digits(value);

        // Вставили/набрали больше, чем код страны — лишние цифры уходят в номер.
        if (digits.Length > 0 && (digits.Length > 4 || !PhoneFormat.IsCodePrefix(digits)))
        {
            var (code, rest) = PhoneFormat.Split(digits);
            SetPhone(code, rest + PhoneFormat.Digits(NationalNumber));
            FocusNumberRequested?.Invoke();
            return;
        }

        if (digits != value)
        {
            _syncingPhone = true;
            CountryCode = digits;
            _syncingPhone = false;
        }
        UpdateCountry();
        Reformat();
        if (PhoneFormat.IsCodeComplete(digits)) FocusNumberRequested?.Invoke();
    }

    partial void OnNationalNumberChanged(string value)
    {
        if (_syncingPhone) return;
        UpdateCountry();
        OnPropertyChanged(nameof(MaskTyped));
        OnPropertyChanged(nameof(MaskRest));
    }

    partial void OnCountrySearchChanged(string value) => CountriesView.Refresh();

    partial void OnIsCountryPickerOpenChanged(bool value)
    {
        if (value) CountrySearch = "";
    }

    /// <summary>Форматирует то, что сейчас в поле номера (вызывается view при вводе).</summary>
    public string FormatNational(string text) => PhoneFormat.Format(PhoneFormat.Digits(text), Country?.Mask);

    /// <summary>В поле номера вставили полный номер с «+» — разбираем на код и номер.</summary>
    public void ApplyFullNumber(string text)
    {
        var (code, rest) = PhoneFormat.Split(PhoneFormat.Digits(text));
        SetPhone(code, rest);
    }

    [RelayCommand]
    private void SelectCountry(Country? country)
    {
        if (country is null) return;
        _chosenCountry = country;
        SetPhone(country.Code, PhoneFormat.Digits(NationalNumber));
        IsCountryPickerOpen = false;
        FocusNumberRequested?.Invoke();
    }

    [RelayCommand]
    private void ToggleCountryPicker() => IsCountryPickerOpen = !IsCountryPickerOpen;

    private void SetPhone(string code, string nationalDigits)
    {
        _syncingPhone = true;
        CountryCode = code;
        NationalNumber = PhoneFormat.Digits(nationalDigits);
        _syncingPhone = false;
        UpdateCountry();
        Reformat();
    }

    private void Reformat()
    {
        _syncingPhone = true;
        NationalNumber = FormatNational(NationalNumber);
        _syncingPhone = false;
        OnPropertyChanged(nameof(MaskTyped));
        OnPropertyChanged(nameof(MaskRest));
    }

    private void UpdateCountry()
    {
        Country = PhoneFormat.Detect(CountryCode, PhoneFormat.Digits(NationalNumber), _chosenCountry);
        OnPropertyChanged(nameof(CountryName));
    }

    // ===================================================================== Шаги входа

    public string StageTitle => Stage switch
    {
        PhoneStage.Phone => "Номер телефона",
        PhoneStage.Code => "Код подтверждения",
        PhoneStage.Password => "Двухэтапная аутентификация",
        PhoneStage.Email => "Электронная почта",
        PhoneStage.EmailCode => "Код из письма",
        _ => "",
    };

    public string StageHint => Stage switch
    {
        PhoneStage.Phone => "Выберите страну и введите номер — код придёт в приложение Telegram.",
        PhoneStage.Code => $"Код отправлен в Telegram на других ваших устройствах (или по SMS) для {CountryCodeText} {NationalNumber}.",
        PhoneStage.Password => "На аккаунте включён облачный пароль. Введите его, чтобы завершить вход.",
        PhoneStage.Email => "Telegram просит привязать почту для входа.",
        PhoneStage.EmailCode => "Введите код, который пришёл на почту.",
        _ => "",
    };

    private string CountryCodeText => "+" + CountryCode;

    public string SubmitText => Stage switch
    {
        PhoneStage.Phone => "Получить код",
        PhoneStage.Password => "Подтвердить пароль",
        _ => "Войти",
    };

    partial void OnStageChanged(PhoneStage value)
    {
        OnPropertyChanged(nameof(StageTitle));
        OnPropertyChanged(nameof(StageHint));
        OnPropertyChanged(nameof(SubmitText));
    }

    partial void OnModeChanged(LoginMode value)
    {
        Error = null;
        _ = value == LoginMode.Qr ? StartQrAsync() : StopQrAsync(resetPhone: true);
    }

    // ===================================================================== По номеру

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        Error = null;
        if (Stage == PhoneStage.Password && Password.Length == 0)
        {
            Error = "Введите облачный пароль.";
            return;
        }

        IsBusy = true;
        var before = Stage;
        try
        {
            var step = Stage switch
            {
                PhoneStage.Phone => await _tg.SubmitPhoneAsync(FullPhone),
                PhoneStage.Code or PhoneStage.EmailCode => await _tg.SubmitCodeAsync(Code),
                PhoneStage.Password => await _tg.SubmitPasswordAsync(Password),
                PhoneStage.Email => await _tg.SubmitEmailAsync(Email),
                _ => throw new InvalidOperationException(),
            };

            if (step == LoginStep.Completed)
            {
                Password = "";
                _onLoggedIn();
                return;
            }

            var next = step switch
            {
                LoginStep.Code => PhoneStage.Code,
                LoginStep.Password => PhoneStage.Password,
                LoginStep.Email => PhoneStage.Email,
                _ => PhoneStage.EmailCode,
            };

            // WTelegram повторно запрашивает тот же шаг, если введённое значение неверно.
            if (next == before && before != PhoneStage.Phone)
                Error = before == PhoneStage.Password ? "Неверный пароль. Попробуйте ещё раз." : "Неверный код. Попробуйте ещё раз.";

            if (next is PhoneStage.Code or PhoneStage.EmailCode && next != before) Code = "";
            if (next == PhoneStage.Password)
            {
                Password = "";
                if (before != PhoneStage.Password) _ = LoadPasswordHintAsync();
            }
            Stage = next;
        }
        catch (Exception ex)
        {
            Error = TgErrors.Describe(ex);
            if (before != PhoneStage.Phone)
            {
                // Сценарий входа прерван на сервере — начинаем заново с номера.
                await _tg.ResetLoginAsync();
                Password = "";
                Stage = PhoneStage.Phone;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Подсказка — необязательна: не блокируем ввод пароля, если запрос задерживается.
    private async Task LoadPasswordHintAsync()
    {
        PasswordHint = null;
        try
        {
            var hintTask = _tg.GetPasswordHintAsync();
            if (await Task.WhenAny(hintTask, Task.Delay(5000)) == hintTask) PasswordHint = await hintTask;
        }
        catch { /* без подсказки */ }
    }

    private bool CanSubmit() => !IsBusy;

    [RelayCommand]
    private void ToggleShowPassword() => ShowPassword = !ShowPassword;

    [RelayCommand]
    private async Task BackAsync()
    {
        Error = null;
        await _tg.ResetLoginAsync();
        Code = "";
        Password = "";
        Stage = PhoneStage.Phone;
    }

    // ===================================================================== По QR

    private async Task StartQrAsync()
    {
        await StopQrAsync(resetPhone: false);
        Stage = PhoneStage.Phone;
        QrFailed = false;
        QrNeedsPassword = false;
        QrImage = null;
        Password = "";
        QrStatus = "Генерация QR-кода…";

        var cts = _qrCts = new CancellationTokenSource();
        _qrTask = RunQrAsync(cts.Token);
        await _qrTask;
    }

    private async Task RunQrAsync(CancellationToken ct)
    {
        try
        {
            await _tg.ResetLoginAsync();
            await _tg.LoginWithQrAsync(
                url => Ui(() =>
                {
                    QrImage = Images.Qr(url);
                    QrStatus = "Отсканируйте код в Telegram на телефоне";
                }),
                PromptPasswordAsync,
                ct);

            Ui(() =>
            {
                Password = "";
                _onLoggedIn();
            });
        }
        catch (OperationCanceledException)
        {
            // переключились на вход по номеру
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Ui(() =>
            {
                Error = TgErrors.Describe(ex);
                QrFailed = true;
                QrNeedsPassword = false;
                QrImage = null;
                QrStatus = "Не удалось войти по QR-коду";
                IsBusy = false;
            });
        }
    }

    private Task<string> PromptPasswordAsync(string? hint, int attempt, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _qrPassword = tcs;
        Ui(() =>
        {
            IsBusy = false;
            Password = "";
            PasswordHint = hint;
            QrNeedsPassword = true;
            QrStatus = "QR-код подтверждён";
            Error = attempt > 1 ? "Неверный пароль. Попробуйте ещё раз." : null;
        });
        return tcs.Task;
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void SubmitQrPassword()
    {
        if (Password.Length == 0)
        {
            Error = "Введите облачный пароль.";
            return;
        }
        Error = null;
        IsBusy = true;
        _qrPassword?.TrySetResult(Password);
    }

    [RelayCommand]
    private Task RetryQr() => StartQrAsync();

    private async Task StopQrAsync(bool resetPhone)
    {
        var cts = _qrCts;
        var task = _qrTask;
        _qrCts = null;
        _qrTask = null;
        if (cts is not null)
        {
            cts.Cancel();
            _qrPassword?.TrySetCanceled();
            if (task is not null)
                try { await task; } catch { /* отменено */ }
            cts.Dispose();
        }
        QrNeedsPassword = false;
        IsBusy = false;
        if (resetPhone)
        {
            await _tg.ResetLoginAsync();
            Stage = PhoneStage.Phone;
        }
    }

    public void Cancel() => _qrCts?.Cancel();

    private static void Ui(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
