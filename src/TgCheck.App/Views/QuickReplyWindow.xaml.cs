using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

/// <summary>
/// Быстрый ответ из островка (один клик по чату): два последних сообщения и поле ввода.
/// Появляется рядом с островком со стороны экрана, куда есть место; закрывается по Esc или когда уходит фокус.
/// </summary>
public partial class QuickReplyWindow : Window
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Helpers.LightWindowAutomationPeer(this);

    private readonly ConversationViewModel _conv;
    private readonly Rect _anchor;
    private readonly string _edge;
    private readonly Action _openFull;
    private readonly ObservableCollection<MessageViewModel> _recent = [];
    private bool _closing;

    public QuickReplyWindow(ConversationViewModel conv, Rect anchor, string edge, Action openFull)
    {
        _conv = conv;
        _anchor = anchor;
        _edge = edge;
        _openFull = openFull;
        DataContext = conv;
        InitializeComponent();
        Recent.ItemsSource = _recent;
        SourceInitialized += (_, _) => Helpers.WindowEffects.ApplyRoundedCorners(this, Color.FromRgb(0x2A, 0x38, 0x46));
        conv.Items.CollectionChanged += OnItemsChanged;
        Closed += (_, _) => conv.Items.CollectionChanged -= OnItemsChanged;
        Loaded += (_, _) =>
        {
            UpdateRecent();
            Place();
            Activate();
            Input.Focus();
        };
        SizeChanged += (_, _) => Place();
        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; SafeClose(); } };
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateRecent);

    // Последние два сообщения чата (без разделителей дат и служебных).
    private void UpdateRecent()
    {
        var last = _conv.Items.OfType<MessageViewModel>().Where(m => m.Item.ServiceText is null).TakeLast(2).ToList();
        if (_recent.SequenceEqual(last)) return;
        _recent.Clear();
        foreach (var m in last) _recent.Add(m);
        Empty.Visibility = last.Count == 0 && _conv.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        if (last.Count == 0 && !_conv.IsLoading) Empty.Text = "Сообщений пока нет";
    }

    // Рядом с чатом в островке: снизу от верхнего островка, сверху от нижнего, сбоку от боковых.
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight;
        const double gap = 12;
        double left, top;
        switch (_edge)
        {
            case "Bottom":
                left = _anchor.Left + _anchor.Width / 2 - w / 2;
                top = _anchor.Top - h - gap;
                break;
            case "Left":
                left = _anchor.Right + gap;
                top = _anchor.Top + _anchor.Height / 2 - h / 2;
                break;
            case "Right":
                left = _anchor.Left - w - gap;
                top = _anchor.Top + _anchor.Height / 2 - h / 2;
                break;
            default:
                left = _anchor.Left + _anchor.Width / 2 - w / 2;
                top = _anchor.Bottom + gap;
                break;
        }
        Left = Math.Clamp(left, area.Left + 8, Math.Max(area.Left + 8, area.Right - w - 8));
        Top = Math.Clamp(top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - h - 8));
    }

    private void Send()
    {
        if (_conv.SendCommand.CanExecute(null)) _conv.SendCommand.Execute(null);
        Input.Focus();
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        Send();
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void OpenFull_Click(object sender, RoutedEventArgs e)
    {
        SafeClose();
        _openFull();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SafeClose();

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
