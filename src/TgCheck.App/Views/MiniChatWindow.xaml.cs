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

    public MiniChatWindow(Action<PeerRef> back)
    {
        _back = back;
        InitializeComponent();
        SourceInitialized += (_, _) => Helpers.WindowEffects.ApplyRoundedCorners(this, Color.FromRgb(0x2A, 0x38, 0x46));
        PrepareSpare();
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
        Card.BeginAnimation(OpacityProperty, null);
        Card.Opacity = 1; // без анимации появления — окно сразу готовое
        if (!IsVisible) Show();
        Activate();

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

    private ConversationView? _spare;

    private void PrepareSpare()
    {
        if (_spare is not null) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (_spare is not null) return;
            _spare = new ConversationView { IsCompactPanel = true, Visibility = Visibility.Collapsed };
            ChatHost.Children.Add(_spare);
        });
    }

    private void ShowView(ConversationViewModel conv)
    {
        if (!_views.TryGetValue(conv, out var view))
        {
            // Заготовка (построена заранее в простое) — открытие без задержки на построение окна переписки.
            view = _spare ?? new ConversationView { IsCompactPanel = true };
            var fresh = _spare is null;
            _spare = null;
            view.DataContext = conv;
            _views[conv] = view;
            if (fresh) ChatHost.Children.Add(view);
            PrepareSpare();
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
