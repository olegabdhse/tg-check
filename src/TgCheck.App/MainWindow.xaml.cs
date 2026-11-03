using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;
using TgCheck.App.Views;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

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
        IsVisibleChanged += (_, e) => { UpdateOnScreen(); if (e.NewValue is false) TrimMemorySoon(); else OnBackOnScreen(); };
        Activated += (_, _) => { UpdateOnScreen(); if (WindowState != WindowState.Minimized) _island?.HideIsland(); };
        Deactivated += (_, _) => { UpdateOnScreen(); ShowIslandIfAway(); };
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
        WatchGdi();
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

    /// <summary>Запуск после фонового обновления: окно не показывается и не забирает фокус, размер и положение — прежние.</summary>
    internal void StartQuietly(QuietStart start)
    {
        if (start.Bounds is { } b)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top, Width, Height) = (b.Left, b.Top, b.Width, b.Height);
        }
        if (start.Visible)
        {
            if (start.Maximized) WindowState = WindowState.Maximized;
            Show();
        }
        else if (start.Minimized)
        {
            _maximizeOnRestore = start.Maximized;
            ShowActivated = false;
            WindowState = WindowState.Minimized;
            Show();
            ShowActivated = true;
        }
        else if (start.Maximized) WindowState = WindowState.Maximized;
    }

    // ===================================================================== Фоновое обновление

    private bool _maximizeOnRestore;
    private WindowState _shownState = WindowState.Normal;
    private DispatcherTimer? _restartTimer;
    private int _hiddenTicks;
    private bool _whatsNewTaken;
    private static readonly TimeSpan UserAwayBeforeRestart = TimeSpan.FromMinutes(2);

    // Новая версия уже на диске. Перезапускаемся, только когда этого нельзя заметить; такого момента не было — она заработает при следующем запуске.
    private void OnUpdateInstalled()
    {
        _restartTimer ??= CreateRestartTimer();
        _restartTimer.Start();
    }

    private DispatcherTimer CreateRestartTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) =>
        {
            if (IsOnScreen) { _hiddenTicks = 0; return; }
            if (++_hiddenTicks >= 2 && CanRestartUnnoticed()) RestartQuietly();
        };
        return timer;
    }

    private bool CanRestartUnnoticed()
    {
        if (_notifyShell is not { HasUnfinishedWork: false } || _island?.IsMouseOver == true) return false;
        if (MusicPlayer.Instance.HasSong || NowPlaying.Instance.IsActive) return false; // играет или на паузе: перезапуск потерял бы место в песне
        if (Application.Current.Windows.OfType<Window>().Any(w => w != this && w != _island && w.IsVisible)) return false;
        var input = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Time) >= UserAwayBeforeRestart.TotalMilliseconds;
    }

    // Нажали «Обновить»: перезапуск сразу, окно остаётся на экране.
    private void OnRestartRequested() => RestartOnNewVersion(visible: true);

    private void RestartQuietly() => RestartOnNewVersion(visible: false);

    private void RestartOnNewVersion(bool visible)
    {
        _restartTimer?.Stop();
        var bounds = WindowState == WindowState.Normal && !double.IsNaN(Left) && !double.IsNaN(Top) ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var start = new QuietStart(Environment.ProcessId, WindowState == WindowState.Minimized, _shownState == WindowState.Maximized,
            bounds.IsEmpty || double.IsInfinity(bounds.Left) ? null : bounds, visible);
        try
        {
            Logging.Write(visible ? "update: перезапуск с окном на экране" : "update: тихий перезапуск, окно скрыто");
            TgCheck.Core.Services.UpdateService.StartInstalled(start.ToArgs());
        }
        catch (Exception ex)
        {
            Logging.Write("update: новая версия не запустилась: " + ex.Message);
            return;
        }
        // Если что-то (поток плеера, загрузка) не даёт процессу выйти — завершаем принудительно: новая копия ждёт этого.
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(5)); Environment.Exit(0); });
        ExitApp();
    }

    // ===================================================================== Защита от утечки графических объектов

    // У процесса предел 10 000 объектов GDI: на нём WPF перестаёт рисовать (пустые окна), и программа падает.
    // Перезапуск заранее сохраняет сессию; записи в логе помогут понять, что копит объекты.
    private const int GdiRestartAt = 7000, GdiLogStep = 500;
    private int _gdiLogged;
    private DispatcherTimer? _gdiTimer;

    private void WatchGdi()
    {
        _gdiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _gdiTimer.Tick += (_, _) =>
        {
            var gdi = (int)GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
            if (gdi - _gdiLogged >= GdiLogStep)
            {
                _gdiLogged = gdi;
                Logging.Write($"gdi: {gdi} объектов; окно на экране: {IsOnScreen}, песня: {MusicPlayer.Instance.HasSong}, " +
                              $"скрытая панель: {MusicNow.Instance.IsSidePanelStrip}, островок: {_island?.IsVisible == true}, мини-окно: {_notifyShell?.MiniChat is not null}, окон: {Application.Current.Windows.Count}");
            }
            if (gdi < GdiRestartAt) return;
            _gdiTimer.Stop();
            Logging.Write($"gdi: {gdi} объектов — перезапуск, пока программа не упала");
            RestartOnNewVersion(visible: IsOnScreen);
        };
        _gdiTimer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    // Окно открыли — рассказать, что программа обновилась, пока её не видели.
    private void ShowWhatsNew()
    {
        if (_whatsNewTaken || !IsOnScreen || _notifyShell is null) return;
        _whatsNewTaken = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (TgCheck.Core.Services.UpdateService.TakeInstalled() is { } update)
                new UpdateWindow(update) { Owner = this }.ShowDialog();
        });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    // Заголовок окна рисует Windows — ему тема сообщается отдельно.
    private void ApplyThemeToWindow() => WindowEffects.ApplyDarkTitleBar(this, AppTheme.WindowBackground, AppTheme.IsLight);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyThemeToWindow();
        AppTheme.Changed += ApplyThemeToWindow;
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

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != System.Windows.Input.Key.G || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control || _notifyShell is null) return;
        e.Handled = true;
        var ghost = GhostViewModel.Instance;
        ghost.IsEnabled = !ghost.IsEnabled;
        _notifyShell.ShowToast(ghost.IsEnabled ? "Режим призрака включён" : "Режим призрака выключен");
    }

    // Печать, когда фокус не в поле ввода: в открытый чат или в поиск (как в Telegram).
    protected override void OnPreviewTextInput(System.Windows.Input.TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox) return;
        if (ShellView.Current?.RouteTyping(e.Text) == true) e.Handled = true;
    }

    internal void ExitApp()
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
        if (_notifyShell is not null)
        {
            _notifyShell.Incoming -= OnIncoming;
            _notifyShell.ReactionIncoming -= OnReaction;
            _notifyShell.UpdateInstalled -= OnUpdateInstalled;
            _notifyShell.RestartRequested -= OnRestartRequested;
        }
        _notifyShell = shell;
        if (shell is not null)
        {
            shell.Incoming += OnIncoming;
            shell.ReactionIncoming += OnReaction;
            shell.UpdateInstalled += OnUpdateInstalled;
            shell.RestartRequested += OnRestartRequested;
        }
        UpdateOnScreen();
        ShowWhatsNew();
        // Островок готовим заранее, в простое: первое создание окна занимает ~2 с, и при первом сворачивании
        // он появлялся с задержкой, сначала не того размера и не на своём месте — «дважды».
        // Запустились без окна (после фонового обновления) — он и должен быть на экране.
        if (shell is not null && UiSettings.ShowIsland)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (IsOnScreen) EnsureIsland()?.Prewarm();
                else ShowIsland();
            });
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

    // Реакция на ваше сообщение — уведомление, как о сообщении: те же настройки, и не для чата, открытого на экране.
    private void OnReaction(ChatItemViewModel chat, string text) => Dispatcher.BeginInvoke(() =>
    {
        if (!UiSettings.Notifications || (chat.IsArchived && chat.Peer.Kind != PeerKind.User && !UiSettings.NotifyArchived)) return;
        if (_notifyShell?.IsOnScreen(chat.Key) == true && IsActive) return;
        chat.EnsureAvatar(_notifyShell!.Chats.Media);
        NotificationWindow.Show(new NotificationData(chat.Peer, chat.Title, text, chat.Avatar, chat.AvatarBrush, chat.Initials), OpenChatFromNotification);
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
        if (_maximizeOnRestore && WindowState == WindowState.Normal)
        {
            _maximizeOnRestore = false;
            WindowState = WindowState.Maximized; // вызовет этот обработчик снова
            return;
        }
        if (WindowState != WindowState.Minimized) _shownState = WindowState;
        UpdateOnScreen();
        Controls.StickerView.WindowStateChanged();
        if (WindowState == WindowState.Minimized) { ShowIsland(); TrimMemorySoon(); }
        else { _island?.HideIsland(); OnBackOnScreen(); }
    }

    // Свернули или убрали в трей — освобождаем память, как Telegram Desktop: сборка мусора и сброс рабочего набора
    // (страницы, которые сейчас не нужны, уходят из оперативной памяти; понадобятся — вернутся сами).
    private CancellationTokenSource? _trimCts;

    private async void TrimMemorySoon()
    {
        _trimCts?.Cancel();
        var cts = _trimCts = new CancellationTokenSource();
        try { await Task.Delay(3000, cts.Token); } catch (TaskCanceledException) { return; }
        if (IsOnScreen) return; // окно снова на экране — не трогаем
        await TrimProcessAsync();
        _awayTicks = 0;
        _awayTimer ??= CreateAwayTimer();
        _awayTimer.Start();
    }

    private bool IsOnScreen => IsVisible && WindowState != WindowState.Minimized;

    // Сборка с уплотнением останавливает программу, в том числе звук: пока играет песня, она только фоновая.
    private static Task TrimProcessAsync() => Task.Run(() =>
    {
        if (!MusicPlayer.Instance.IsPlaying)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        else GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false); // фоновая сборка без остановки звука
        try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1); } catch { }
    });

    // Окно не на экране: через 2 минуты убираем и экран открытого чата, дальше раз в 10 минут снова отдаём память — фоновая работа её набирает.
    private DispatcherTimer? _awayTimer;
    private int _awayTicks;

    private DispatcherTimer CreateAwayTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timer.Tick += async (_, _) =>
        {
            if (IsOnScreen) { timer.Stop(); return; }
            if (++_awayTicks % 5 != 1) return;
            ShellView.Current?.TrimHiddenChats();
            _island?.TrimHiddenMini();
            await TrimProcessAsync();
        };
        return timer;
    }

    private void OnBackOnScreen()
    {
        if (!IsOnScreen) return;
        _trimCts?.Cancel();
        _awayTimer?.Stop();
        ShellView.Current?.RestoreChat();
        ShowWhatsNew();
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
