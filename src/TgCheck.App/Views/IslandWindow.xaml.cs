using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

/// <summary>
/// «Островок» сверху экрана, пока главное окно свёрнуто: 4 чата, где вы писали последними.
/// Клик по чату — мини-окно чата, клик по значку — развернуть приложение.
/// </summary>
public partial class IslandWindow : Window
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Helpers.LightWindowAutomationPeer(this);

    private readonly ShellViewModel _shell;
    private readonly Action _restoreMain;
    private MiniChatWindow? _mini;
    private bool _hiding;

    public IslandWindow(ShellViewModel shell, Action restoreMain)
    {
        _shell = shell;
        _restoreMain = restoreMain;
        InitializeComponent();
        SizeChanged += (_, _) => Reposition();
        _shell.IslandChanged += OnIslandChanged;
        _shell.Incoming += OnIncoming;
        Closed += (_, _) =>
        {
            _mini?.Close();
            _shell.IslandChanged -= OnIslandChanged;
            _shell.Incoming -= OnIncoming;
        };
    }

    public ImageSource? Logo => Images.AppLogo;

    private void OnIslandChanged() => Anim.Ui(Refresh);

    private void Refresh()
    {
        var chats = _shell.IslandChats();
        foreach (var c in chats) c.EnsureAvatar(_shell.Chats.Media);
        ChatsList.ItemsSource = chats;
    }

    // Новое входящее — островок слегка «подпрыгивает».
    private void OnIncoming(ChatItemViewModel chat) => Anim.Ui(() =>
    {
        if (!IsVisible || _hiding) return;
        Refresh();
        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new CubicEase()));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new BackEase { Amplitude = 0.5 }));
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    });

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
    }

    /// <summary>Появление: выезжает сверху и «раскрывается» из капсулы.</summary>
    public void ShowIsland()
    {
        _hiding = false;
        _mini ??= CreateMini(); // заранее, чтобы первый клик по чату открывался без задержки
        Refresh();
        var area = SystemParameters.WorkArea;
        Top = area.Top - 90;
        Opacity = 0;
        if (!IsVisible) Show();
        UpdateLayout();
        Reposition();

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var duration = TimeSpan.FromMilliseconds(420);
        BeginAnimation(TopProperty, new DoubleAnimation(area.Top - 90, area.Top + 2, duration) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.55, 1, duration) { EasingFunction = ease });
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, duration) { EasingFunction = ease });
    }

    /// <summary>Скрытие: сжимается в капсулу и уезжает вверх.</summary>
    public void HideIsland()
    {
        _mini?.CloseAnimated();
        if (!IsVisible || _hiding) return;
        _hiding = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(220);
        var up = new DoubleAnimation(Top, SystemParameters.WorkArea.Top - 90, duration) { EasingFunction = ease };
        up.Completed += (_, _) => { if (_hiding) Hide(); };
        BeginAnimation(TopProperty, up);
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 0, duration));
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.6, duration) { EasingFunction = ease });
    }

    /// <summary>Скрыть сразу, без анимации.</summary>
    public void HideNow()
    {
        _hiding = true;
        BeginAnimation(TopProperty, null);
        BeginAnimation(OpacityProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Hide();
    }

    private void AppButton_Click(object sender, RoutedEventArgs e)
    {
        _mini?.HideNow();
        HideNow();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, _restoreMain);
    }

    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatItemViewModel chat }) return;
        var conv = _shell.OpenMiniChat(chat.Peer);
        _mini ??= CreateMini();
        _mini.ShowChat(conv, Top + ActualHeight - 14);
    }

    private MiniChatWindow CreateMini()
    {
        var mini = new MiniChatWindow(back: peer =>
        {
            // Сначала убираем мини-окно и островок, чат готовим, пока окно ещё свёрнуто,
            // и только потом показываем уже готовое приложение — без промежуточной перерисовки.
            _mini?.HideNow();
            HideNow();
            _shell.OpenChat(peer);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, _restoreMain);
        });
        mini.IsVisibleChanged += (_, e) => { if (e.NewValue is false) _shell.MiniChat = null; };
        mini.Closed += (_, _) => _mini = null;
        return mini;
    }
}
