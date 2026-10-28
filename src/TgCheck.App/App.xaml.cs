using System.Windows;
using System.Windows.Threading;
using TgCheck.App.ViewModels;
using TgCheck.Core.Services;

namespace TgCheck.App;

public partial class App : Application
{
    private MainViewModel? _main;

    // Одна копия приложения: вторая не открывает ту же сессию параллельно, а показывает уже запущенное окно.
    private static Mutex? _single;
    private static EventWaitHandle? _activate;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\TgCheck.SingleInstance", out var first);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TgCheck.Activate");
        if (!first)
        {
            _activate.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.RestoreFromBackground());
        }) { IsBackground = true, Name = "TgCheck.Activate" };
        listener.Start();

        base.OnStartup(e);
        // Закрытие окна прячет его в трей; выход — из меню значка.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;

        _main = new MainViewModel();
        var window = new MainWindow { DataContext = _main };
        MainWindow = window;
        window.Show();
        _ = _main.InitializeAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_main is not null) await _main.DisposeAsync();
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(TgErrors.Describe(e.Exception), "TG Check — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
