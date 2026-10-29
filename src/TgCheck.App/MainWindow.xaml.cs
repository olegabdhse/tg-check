using System.ComponentModel;
using System.Windows;
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
        if (Images.AppLogo is { } logo) Icon = logo;
        StateChanged += OnStateChanged;
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
    }

    // Крестик не завершает приложение: окно прячется в трей, сообщения и уведомления продолжают приходить.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && DataContext is MainViewModel { CurrentPage: ShellViewModel })
        {
            e.Cancel = true;
            Hide();
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
    }

    private void OnIncoming(ChatItemViewModel chat) => Dispatcher.BeginInvoke(() =>
    {
        if (!UiSettings.Notifications || (chat.IsArchived && !UiSettings.NotifyArchived)) return;
        // Окно на виду и активно — уведомление не нужно (чат и так виден в списке).
        if (IsVisible && IsActive && WindowState != WindowState.Minimized) return;
        if (_notifyShell?.MiniChat?.Peer.Key == chat.Key && Application.Current.Windows.OfType<MiniChatWindow>().Any(w => w.IsActive)) return;
        chat.EnsureAvatar(_notifyShell!.Chats.Media);
        var data = new NotificationData(chat.Peer, chat.Title, chat.LastText, chat.Avatar, chat.AvatarBrush, chat.Initials);
        NotificationWindow.Show(data, OpenChatFromNotification);
        if (UiSettings.NotificationSound) Notifier.PlaySound();
    });

    private void OpenChatFromNotification(PeerRef peer)
    {
        _island?.HideNow();
        _notifyShell?.OpenChat(peer);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RestoreFromBackground);
    }

    // Свернули — сверху появляется «островок» с последними чатами; развернули — он прячется.
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (!UiSettings.ShowIsland) return;
            if (DataContext is not MainViewModel { CurrentPage: ShellViewModel shell }) return;
            if (_island is null || _islandShell != shell)
            {
                _island?.Close();
                _island = new IslandWindow(shell, RestoreFromBackground);
                _islandShell = shell;
            }
            _island.ShowIsland();
        }
        else
        {
            _island?.HideIsland();
        }
    }

    public void RestoreFromBackground()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
