using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;
using TgCheck.App.Views;
using TgCheck.Core.Models;

namespace TgCheck.App;

public partial class MainWindow : Window
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Helpers.LightWindowAutomationPeer(this);

    private IslandWindow? _island;
    private ShellViewModel? _islandShell;
    private ShellViewModel? _notifyShell;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        // Иконку окна берём из exe (см. ApplyExeIcon): PNG-иконку WPF панель задач не показывает.
        StateChanged += OnStateChanged;
        IsVisibleChanged += (_, e) => { UpdateOnScreen(); if (e.NewValue is false) TrimMemorySoon(); };
        Activated += (_, _) => { UpdateOnScreen(); if (WindowState != WindowState.Minimized) _island?.HideIsland(); };
        Deactivated += (_, _) => { UpdateOnScreen(); ShowIslandIfAway(); TrimMemorySoon(); };
        // Кнопка на панели задач появляется при показе окна и берёт иконку exe через оболочку (у нас — пустой значок);
        // повторная установка иконки после показа заставляет панель её обновить.
        ContentRendered += (_, _) => WindowEffects.RefreshExeIconWhenIdle(this);
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) WindowEffects.RefreshExeIconWhenIdle(this); };
        StateChanged += (_, _) => WindowEffects.RefreshExeIconWhenIdle(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnMainChanged;
            if (e.NewValue is MainViewModel main) main.PropertyChanged += OnMainChanged;
        };
        _tray = new TrayIcon(RestoreFromBackground, ExitApp);
        Closed += (_, _) =>
        {
            _island?.Close();
            NotificationWindow.CloseAll();
            _tray?.Dispose();
            _tray = null;
            // Окно закрывается по-настоящему только при выходе (или до входа в аккаунт).
            Application.Current.Shutdown();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ApplyDarkTitleBar(this, (Color)FindResource("BgColor"));
        WindowEffects.ApplyExeIcon(this);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    // Разворачивание с панели задач: островок начинает гаснуть ещё до того, как окно развернётся и займёт
    // поток отрисовкой (как при открытии из трея) — иначе он пропадает рывком, без анимации.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SYSCOMMAND = 0x0112, SC_RESTORE = 0xF120, SC_MAXIMIZE = 0xF030;
        if (msg == WM_SYSCOMMAND && WindowState == WindowState.Minimized)
        {
            var cmd = wParam.ToInt32() & 0xFFF0;
            if (cmd is SC_RESTORE or SC_MAXIMIZE) _island?.HideIsland();
        }
        return IntPtr.Zero;
    }

    // Крестик не завершает приложение: окно прячется в трей, сообщения и уведомления продолжают приходить.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && DataContext is MainViewModel { CurrentPage: ShellViewModel })
        {
            e.Cancel = true;
            Hide();
            ShowIsland();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowHint("TG Check работает в фоне", "Новые сообщения будут приходить в уведомлениях. Выход — через значок в трее.");
            }
            return;
        }
        base.OnClosing(e);
    }

    // Печать, когда фокус не в поле ввода: в открытый чат или в поиск (как в Telegram).
    protected override void OnPreviewTextInput(System.Windows.Input.TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox) return;
        if (ShellView.Current?.RouteTyping(e.Text) == true) e.Handled = true;
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    // После входа подписываемся на входящие для уведомлений.
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentPage)) return;
        var shell = (sender as MainViewModel)?.CurrentPage as ShellViewModel;
        if (ReferenceEquals(shell, _notifyShell)) return;
        if (_notifyShell is not null) _notifyShell.Incoming -= OnIncoming;
        _notifyShell = shell;
        if (shell is not null) shell.Incoming += OnIncoming;
        // Островок готовим заранее, в простое: первое создание окна занимает ~2 с, и при первом сворачивании
        // он появлялся с задержкой, сначала не того размера и не на своём месте — «дважды».
        if (shell is not null && UiSettings.ShowIsland)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => EnsureIsland()?.Prewarm());
    }

    private void OnIncoming(ChatItemViewModel chat) => Dispatcher.BeginInvoke(() =>
    {
        // Личные переписки с людьми (не боты) уведомляют всегда, даже из архива, — молчат, только если звук у чата выключен.
        // Архив без уведомлений — для групп, каналов и ботов (настройка «Уведомления из архива»).
        if (!UiSettings.Notifications || (chat.IsArchived && chat.Peer.Kind != PeerKind.User && !UiSettings.NotifyArchived)) return;
        // Как в Telegram: уведомление приходит и при развёрнутом окне — не нужно только для чата, который сейчас открыт на экране
        // (раньше при открытом окне уведомлений не было вовсе, и сообщения легко пропустить).
        if (_notifyShell?.IsOnScreen(chat.Key) == true && (IsActive || Application.Current.Windows.OfType<MiniChatWindow>().Any(w => w.IsActive))) return;
        chat.EnsureAvatar(_notifyShell!.Chats.Media);
        var data = new NotificationData(chat.Peer, chat.Title, chat.LastText, chat.Avatar, chat.AvatarBrush, chat.Initials);
        NotificationWindow.Show(data, OpenChatFromNotification);
        if (UiSettings.NotificationSound) Notifier.PlaySound();
    });

    private void OpenChatFromNotification(PeerRef peer)
    {
        _island?.HideIsland();
        _notifyShell?.OpenChat(peer);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RestoreFromBackground);
    }

    // Свернули — сверху появляется «островок» с последними чатами; развернули — он прячется.
    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateOnScreen();
        if (WindowState == WindowState.Minimized) { ShowIsland(); TrimMemorySoon(); }
        else _island?.HideIsland();
    }

    // Свернули или убрали в трей — освобождаем память, как Telegram Desktop: сборка мусора и сброс рабочего набора
    // (страницы, которые сейчас не нужны, уходят из оперативной памяти; понадобятся — вернутся сами).
    private CancellationTokenSource? _trimCts;

    private async void TrimMemorySoon()
    {
        _trimCts?.Cancel();
        var cts = _trimCts = new CancellationTokenSource();
        try { await Task.Delay(3000, cts.Token); } catch (TaskCanceledException) { return; }
        if (IsActive && IsVisible && WindowState != WindowState.Minimized) return; // вернулись в окно — не трогаем
        await Task.Run(() =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1); } catch { }
        });
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    // Чат главного окна «читается» только пока окно на экране и в фокусе (как в Telegram Desktop):
    // окно за другими программами — сообщения остаются непрочитанными.
    private void UpdateOnScreen()
    {
        if (DataContext is MainViewModel { CurrentPage: ShellViewModel shell })
            shell.MainOnScreen = IsVisible && WindowState != WindowState.Minimized && IsActive;
    }

    // Ушли в другое приложение, не сворачивая окно, — островок тоже появляется (как при сворачивании).
    // Если фокус ушёл в наше же окно (мини-приложение, быстрый ответ, уведомление) — не показываем.
    private void ShowIslandIfAway() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (IsActive || !IsVisible || WindowState == WindowState.Minimized) return;
        if (Application.Current.Windows.OfType<Window>().Any(w => w.IsActive)) return;
        ShowIsland();
    });

    // Островок с последними чатами — когда окно свёрнуто или закрыто в трей.
    private void ShowIsland()
    {
        if (!UiSettings.ShowIsland) return;
        EnsureIsland()?.ShowIsland();
    }

    private IslandWindow? EnsureIsland()
    {
        if (DataContext is not MainViewModel { CurrentPage: ShellViewModel shell }) return null;
        if (_island is null || _islandShell != shell)
        {
            _island?.Close();
            _island = new IslandWindow(shell, RestoreFromBackground);
            _islandShell = shell;
        }
        return _island;
    }

    public void RestoreFromBackground()
    {
        Show();
        _island?.HideIsland(); // окно было закрыто в трей — его состояние не менялось, островок прячем сами
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
