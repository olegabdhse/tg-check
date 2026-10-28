using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TgCheck.App.ViewModels;
using TgCheck.Core.Models;

namespace TgCheck.App.Views;

/// <summary>Компактное окно чата размером с телефон, открывается из «островка».</summary>
public partial class MiniChatWindow : Window
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Helpers.LightWindowAutomationPeer(this);

    private readonly Action<PeerRef> _back;
    private bool _closing;

    public MiniChatWindow(Action<PeerRef> back)
    {
        _back = back;
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (Chat.DataContext is ConversationViewModel conv && conv.HandleEscape()) { e.Handled = true; return; }
            e.Handled = true;
            CloseAnimated();
        };
    }

    /// <summary>Показать чат: окно «выпадает» из островка.</summary>
    public void ShowChat(ConversationViewModel conv, double top)
    {
        _closing = false;
        var switching = IsVisible && Chat.DataContext is not null && Chat.DataContext != conv;
        Chat.DataContext = conv;
        if (switching)
        {
            Helpers.Anim.SlideIn(Chat, 30);
            Activate();
            return;
        }

        var area = SystemParameters.WorkArea;
        // Открываем там, где окно оставили в прошлый раз (если это место ещё на экране).
        if (Helpers.UiSettings.MiniChatPosition is { } saved)
        {
            Left = saved.X;
            Top = saved.Y;
            Height = Math.Min(720, area.Bottom - saved.Y - 8);
        }
        else
        {
            Left = area.Left + (area.Width - Width) / 2;
            Top = top;
            Height = Math.Min(720, area.Bottom - top - 8);
        }
        Opacity = 0;
        if (!IsVisible) Show();
        Activate();

        // Лёгкое появление: короткий fade + едва заметный масштаб — без «пружины», не грузит отрисовку.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    /// <summary>Плавно закрыть (сворачивается обратно к островку).</summary>
    public void CloseAnimated()
    {
        if (_closing || !IsVisible) return;
        _closing = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(200);
        var fade = new DoubleAnimation(Opacity, 0, duration);
        fade.Completed += (_, _) =>
        {
            if (!_closing) return;
            Chat.DataContext = null;
            Hide(); // окно переиспользуется — следующий чат открывается мгновенно
        };
        BeginAnimation(OpacityProperty, fade);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.9, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.7, duration) { EasingFunction = ease });
    }

    /// <summary>Скрыть сразу, без анимации (переход в приложение).</summary>
    public void HideNow()
    {
        _closing = true;
        BeginAnimation(OpacityProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Hide();
        Chat.DataContext = null;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        Helpers.UiSettings.MiniChatPosition = new Point(Left, Top);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Chat.DataContext is not ConversationViewModel conv) { CloseAnimated(); return; }
        _back(conv.Peer);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseAnimated();
}
