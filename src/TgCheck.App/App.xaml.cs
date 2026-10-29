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
        // Сетевой цикл Telegram работает в пуле потоков: при запуске пул забит фоновыми задачами (кэш, страницы чатов,
        // видеодвижок), и без запаса потоков входящие сообщения и статусы ждали свободного по несколько секунд.
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), Math.Max(io, 32));
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

    private static bool _errorShown;

    // Ошибка пишется в лог целиком; окно — одно (повторяющаяся ошибка не заваливает экран окнами).
    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Logging.Write("unhandled: " + e.Exception);
        if (_errorShown) return;
        _errorShown = true;
        MessageBox.Show(TgErrors.Describe(e.Exception), "TG Check — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        _errorShown = false;
    }
}
