using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

public partial class ChatsView : UserControl
{
    private const double CompactWidth = 860;
    private ChatsViewModel? _vm;
    private object? _lastConversation;
    private readonly Dictionary<ConversationViewModel, ConversationView> _views = [];
    private readonly List<ConversationViewModel> _recent = [];
    private const int KeepViews = 6;

    public ChatsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ChatsViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            ApplyLayout();
        };
        Loaded += (_, _) => PrepareSpare();
    }

    private ConversationView? _spare;

    // Окно переписки тяжёлое (шаблоны, панели, оверлеи) — следующее строим заранее, когда приложение простаивает.
    private void PrepareSpare()
    {
        if (_spare is not null) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (_spare is not null) return;
            _spare = new ConversationView { Visibility = Visibility.Collapsed };
            ConversationsHost.Children.Add(_spare);
        });
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatsViewModel.Conversation)) return;
        ShowConversation();
        ApplyLayout();
        _lastConversation = _vm?.Conversation;
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm is not null) _vm.IsCompact = ActualWidth < CompactWidth;
        ApplyLayout();
    }

    // Адаптивная раскладка: в узком окне список и чат показываются по очереди.
    private void ApplyLayout()
    {
        var compact = _vm?.IsCompact == true;
        var hasChat = _vm?.HasConversation == true;
        foreach (var view in _views.Values) { view.ShowBack = compact; view.IsCompactPanel = compact; }
        if (!compact)
        {
            ListColumn.Width = new GridLength(ActualWidth < 1050 ? 300 : 350);
            GapColumn.Width = new GridLength(14);
            ChatColumn.Width = new GridLength(1, GridUnitType.Star);
            ListPanel.Visibility = Visibility.Visible;
            ChatPanel.Visibility = Visibility.Visible;
            return;
        }
        GapColumn.Width = new GridLength(0);
        if (hasChat)
        {
            ListColumn.Width = new GridLength(0);
            ChatColumn.Width = new GridLength(1, GridUnitType.Star);
            ListPanel.Visibility = Visibility.Collapsed;
            ChatPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            ChatColumn.Width = new GridLength(0);
            ListPanel.Visibility = Visibility.Visible;
            ChatPanel.Visibility = Visibility.Collapsed;
            Anim.SlideIn(ListPanel, -40);
        }
    }

    // Последние открытые чаты держим готовыми (лента уже построена) — переключение мгновенное.
    private void ShowConversation()
    {
        var conv = _vm?.Conversation;
        foreach (var view in _views.Values) view.Visibility = Visibility.Collapsed;
        if (conv is null) return;

        if (!_views.TryGetValue(conv, out var target))
        {
            // Заготовка (создана заранее в простое) — первое открытие чата без задержки на построение окна.
            target = _spare ?? new ConversationView();
            var fresh = _spare is null;
            _spare = null;
            target.ShowBack = _vm?.IsCompact == true;
            target.IsCompactPanel = _vm?.IsCompact == true;
            target.DataContext = conv;
            _views[conv] = target;
            if (fresh) ConversationsHost.Children.Add(target);
            PrepareSpare();
        }
        _recent.Remove(conv);
        _recent.Insert(0, conv);
        foreach (var old in _recent.Skip(KeepViews).ToList())
        {
            ConversationsHost.Children.Remove(_views[old]);
            _views.Remove(old);
            _recent.Remove(old);
        }
        target.Visibility = Visibility.Visible;
        // Чат → чат — без анимации (как в Telegram Desktop); анимируем только появление из пустого состояния.
        if (_lastConversation is null || _vm?.IsCompact == true) Anim.SlideIn(target, _vm?.IsCompact == true ? 60 : 24, 200);
    }

    // Аватарки грузятся только для строк, которые реально появились на экране (чатов могут быть тысячи).
    private void ChatItem_Changed(object sender, RoutedEventArgs e) => EnsureAvatar(sender);
    private void ChatItem_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => EnsureAvatar(sender);

    private void EnsureAvatar(object sender)
    {
        if (sender is FrameworkElement { DataContext: PeerAvatarViewModel item } && _vm is not null)
            item.EnsureAvatar(_vm.Media);
    }

    // Клик по аватару с кольцом — открыть истории.
    private void Avatar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatItemViewModel { HasStories: true } chat } && _vm is not null)
        {
            _vm.OpenStoriesOf(chat.Peer);
            e.Handled = true;
        }
    }

    // Колесо мыши листает ленту историй горизонтально.
    private void Stories_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.ScrollableWidth > 0)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.6);
            e.Handled = true;
        }
    }

    /// <summary>Значок закрепления показывается, только если нет счётчика непрочитанных.</summary>
    public static IMultiValueConverter PinVisibility { get; } = new PinConverter();
    public static IValueConverter NotNull { get; } = new NotNullConverter();
    public static IValueConverter NonZero { get; } = new NonZeroConverter();

    private sealed class PinConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [true, false] ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class NotNullConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class NonZeroConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i != 0;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
