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

    // Готовые ленты последних чатов (как в главном окне): переключение — просто показать другую, без перестройки.
    private const int KeepViews = 4;
    private readonly Dictionary<ConversationViewModel, ConversationView> _views = [];
    private readonly List<ConversationViewModel> _recent = [];
    private ConversationView? _current;
    private ConversationViewModel? Current => _current?.DataContext as ConversationViewModel;

    private readonly Func<IReadOnlyList<ChatItemViewModel>>? _recentChats;
    private readonly Action<ChatItemViewModel>? _switchTo;

    public MiniChatWindow(Action<PeerRef> back, Func<IReadOnlyList<ChatItemViewModel>>? recentChats = null, Action<ChatItemViewModel>? switchTo = null)
    {
        _back = back;
        _recentChats = recentChats;
        _switchTo = switchTo;
        InitializeComponent();
        SourceInitialized += (_, _) => Helpers.WindowEffects.ApplyRoundedCorners(this, Color.FromRgb(0x2A, 0x38, 0x46));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (Current is { } conv && conv.HandleEscape()) { e.Handled = true; return; }
            e.Handled = true;
            CloseAnimated();
        };
    }

    /// <summary>Показать чат: окно «выпадает» из островка.</summary>
    public void ShowChat(ConversationViewModel conv, double top)
    {
        _closing = false;
        var switching = IsVisible && Current is not null && Current != conv;
        ShowView(conv);
        RefreshSwitcher(conv);
        if (switching)
        {
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
        Card.Opacity = 0;
        if (!IsVisible) Show();
        Activate();

        // Лёгкое появление содержимого (окно — обычное, его рисует Windows: никакого мерцания).
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    /// <summary>Закрыть (окно прячется и переиспользуется — следующий чат открывается мгновенно).</summary>
    public void CloseAnimated()
    {
        if (_closing || !IsVisible) return;
        HideNow();
    }

    /// <summary>Скрыть сразу, без анимации (переход в приложение).</summary>
    public void HideNow()
    {
        _closing = true;
        Card.BeginAnimation(OpacityProperty, null);
        Hide();
        if (_current is not null) _current.Visibility = Visibility.Collapsed;
        _current = null;
    }

    private void ShowView(ConversationViewModel conv)
    {
        if (!_views.TryGetValue(conv, out var view))
        {
            view = new ConversationView { DataContext = conv, IsCompactPanel = true };
            _views[conv] = view;
            ChatHost.Children.Add(view);
        }
        _recent.Remove(conv);
        _recent.Insert(0, conv);
        foreach (var old in _recent.Skip(KeepViews).ToList())
        {
            ChatHost.Children.Remove(_views[old]);
            _views.Remove(old);
            _recent.Remove(old);
        }
        foreach (var other in _views.Values) if (other != view) other.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
        _current = view;
    }

    // Печать вне поля ввода — в поле сообщения открытого чата.
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (_current?.TypeIntoComposer(e.Text) == true) e.Handled = true;
    }

    // Панель последних чатов в шапке: текущий отмечен рамкой.
    private void RefreshSwitcher(ConversationViewModel current)
    {
        if (_recentChats is null) return;
        var chats = _recentChats().ToList();
        if (chats.All(c => c.Key != current.Peer.Key) && current.Owner.FindChat(current.Peer.Key) is { } open) chats.Insert(0, open);
        ChatSwitcher.ItemsSource = chats.Take(5).Select(c => new MiniChatItem(c, c.Key == current.Peer.Key)).ToList();
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MiniChatItem { IsCurrent: false } item }) _switchTo?.Invoke(item.Chat);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        Helpers.UiSettings.MiniChatPosition = new Point(Left, Top);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } conv) { CloseAnimated(); return; }
        _back(conv.Peer);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseAnimated();
}

/// <summary>Чат в панели переключения мини-окна; текущий — с рамкой.</summary>
public sealed record MiniChatItem(ChatItemViewModel Chat, bool IsCurrent)
{
    public System.Windows.Media.Brush? Ring => IsCurrent ? (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush") : null;
}
