using System.Windows;
using System.Windows.Threading;
using TgCheck.App.ViewModels;
using TgCheck.Core.Services;

namespace TgCheck.App;

public partial class App : Application
{
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
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
