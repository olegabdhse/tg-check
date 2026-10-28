using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TgCheck.App.ViewModels;
using TgCheck.Core.Models;

namespace TgCheck.App.Views;

/// <summary>Данные уведомления — снимок на момент прихода сообщения.</summary>
public sealed record NotificationData(PeerRef Peer, string Title, string Text, ImageSource? Avatar, Brush AvatarBrush, string Initials);

/// <summary>
/// Всплывающее уведомление в правом нижнем углу (как в Telegram Desktop): аватар, чат, превью и «Ответить».
/// Несколько уведомлений складываются стопкой, каждое скрывается само через несколько секунд.
/// </summary>
public partial class NotificationWindow : Window
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Helpers.LightWindowAutomationPeer(this);

    private static readonly List<NotificationWindow> Open = [];
    private const int MaxVisible = 3;

    private readonly Action<PeerRef> _openChat;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _closing;

    private NotificationWindow(NotificationData data, Action<PeerRef> openChat)
    {
        _openChat = openChat;
        DataContext = data;
        InitializeComponent();
        _timer.Tick += (_, _) => Dismiss();
        // Пока курсор на уведомлении — не скрываем.
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_closing) _timer.Start(); };
        Loaded += (_, _) =>
        {
            Layout();
            Slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            _timer.Start();
        };
    }

    public static void Show(NotificationData data, Action<PeerRef> openChat)
    {
        // Новое сообщение из того же чата заменяет старое уведомление.
        foreach (var same in Open.Where(w => ((NotificationData)w.DataContext).Peer.Key == data.Peer.Key).ToList()) same.CloseNow();
        while (Open.Count >= MaxVisible) Open[0].CloseNow();
        var window = new NotificationWindow(data, openChat) { Opacity = 0 };
        Open.Add(window);
        window.Show();
    }

    public static void CloseAll()
    {
        foreach (var w in Open.ToList()) w.CloseNow();
    }

    // Стопка снизу вверх, новое — внизу.
    private static void Layout()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom;
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var w = Open[i];
            var height = w.ActualHeight > 0 ? w.ActualHeight : 120;
            w.Left = area.Right - w.Width;
            w.Top = bottom - height;
            bottom -= height - 8;
        }
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => CloseNow();
        BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 40, TimeSpan.FromMilliseconds(180)));
    }

    private void CloseNow()
    {
        _closing = true;
        _timer.Stop();
        if (Open.Remove(this)) Layout();
        Close();
    }

    private void OpenChat()
    {
        var peer = ((NotificationData)DataContext).Peer;
        CloseNow();
        _openChat(peer);
    }

    private void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenChat();

    private void Reply_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenChat();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }
}
