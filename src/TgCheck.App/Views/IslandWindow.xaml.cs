using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

/// <summary>
/// «Островок», пока главное окно свёрнуто или закрыто в трей: 4 чата, где вы писали последними.
/// Клик по чату — быстрый ответ (последние сообщения и поле ввода), двойной клик — мини-окно чата,
/// клик по значку — развернуть приложение. Долгое нажатие — перетащить: островок притягивается
/// к ближайшей стороне экрана (у боковых — вертикальный) и запоминает место вдоль неё.
/// </summary>
public partial class IslandWindow : Window
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Helpers.LightWindowAutomationPeer(this);

    public static readonly DependencyProperty IslandOrientationProperty = DependencyProperty.Register(
        nameof(IslandOrientation), typeof(Orientation), typeof(IslandWindow), new PropertyMetadata(Orientation.Horizontal));

    /// <summary>Горизонтальный у верхней/нижней стороны, вертикальный у боковых.</summary>
    public Orientation IslandOrientation
    {
        get => (Orientation)GetValue(IslandOrientationProperty);
        set => SetValue(IslandOrientationProperty, value);
    }

    // Отступы окна под тень (Margin корневого Grid): островок прижимается к краю экрана самой капсулой.
    private const double ShadowLeft = 22, ShadowTop = 8, ShadowRight = 22, ShadowBottom = 22, Gap = 4;

    private readonly ShellViewModel _shell;
    private readonly Action _restoreMain;
    private MiniChatWindow? _mini;
    private QuickReplyWindow? _quick;
    private bool _hiding;
    private string _edge;
    private double _along;

    // Долгое нажатие — перетаскивание.
    private readonly DispatcherTimer _press = new() { Interval = TimeSpan.FromMilliseconds(380) };
    private bool _dragging;
    private bool _dragged;

    public IslandWindow(ShellViewModel shell, Action restoreMain)
    {
        _shell = shell;
        _restoreMain = restoreMain;
        InitializeComponent();
        (_edge, _along) = UiSettings.IslandPlace;
        ApplyEdge();
        SizeChanged += (_, _) => { if (!_dragging && !_prewarming) PlaceAtEdge(); };
        _press.Tick += (_, _) => StartDrag();
        _shell.IslandChanged += OnIslandChanged;
        _shell.Incoming += OnIncoming;
        Closed += (_, _) =>
        {
            _mini?.Close();
            _quick?.Close();
            _shell.IslandChanged -= OnIslandChanged;
            _shell.Incoming -= OnIncoming;
        };
    }

    public ImageSource? Logo => Images.AppLogo;

    private void OnIslandChanged() => Anim.Ui(Refresh);

    private void Refresh()
    {
        var chats = _shell.IslandChats();
        // Тот же набор — ничего не перестраиваем: иначе островок «моргает» и теряет клик, пришедший в этот момент.
        if (ChatsList.ItemsSource is IEnumerable<ChatItemViewModel> shown && shown.SequenceEqual(chats)) return;
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

    // ===================================================================== Сторона экрана

    private bool IsSide => _edge is "Left" or "Right";

    private void ApplyEdge()
    {
        IslandOrientation = IsSide ? Orientation.Vertical : Orientation.Horizontal;
        Divider.Width = IsSide ? 26 : 1;
        Divider.Height = IsSide ? 1 : 26;
        Divider.Margin = IsSide ? new Thickness(0, 10, 0, 6) : new Thickness(10, 0, 6, 0);
        // Капсула «растёт» от своей стороны экрана.
        Pill.RenderTransformOrigin = _edge switch
        {
            "Bottom" => new Point(0.5, 1),
            "Left" => new Point(0, 0.5),
            "Right" => new Point(1, 0.5),
            _ => new Point(0.5, 0),
        };
    }

    /// <summary>Поставить островок к своей стороне экрана, в запомненное место вдоль неё.</summary>
    private void PlaceAtEdge()
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        switch (_edge)
        {
            case "Left":
                Left = area.Left + Gap - ShadowLeft;
                Top = area.Top + (area.Height - h) * _along;
                break;
            case "Right":
                Left = area.Right - w + ShadowRight - Gap;
                Top = area.Top + (area.Height - h) * _along;
                break;
            case "Bottom":
                Left = area.Left + (area.Width - w) * _along;
                Top = area.Bottom - h + ShadowBottom - Gap;
                break;
            default:
                Left = area.Left + (area.Width - w) * _along;
                Top = area.Top + Gap - ShadowTop;
                break;
        }
    }

    // После перетаскивания — к ближайшей стороне; место вдоль неё — где отпустили.
    private void SnapToNearestEdge()
    {
        var area = SystemParameters.WorkArea;
        var cx = Left + ActualWidth / 2;
        var cy = Top + ActualHeight / 2;
        var distances = new Dictionary<string, double>
        {
            ["Top"] = cy - area.Top,
            ["Bottom"] = area.Bottom - cy,
            ["Left"] = cx - area.Left,
            ["Right"] = area.Right - cx,
        };
        _edge = distances.MinBy(d => d.Value).Key;
        ApplyEdge();
        UpdateLayout();
        var w = ActualWidth;
        var h = ActualHeight;
        _along = IsSide
            ? Math.Clamp((cy - h / 2 - area.Top) / Math.Max(1, area.Height - h), 0, 1)
            : Math.Clamp((cx - w / 2 - area.Left) / Math.Max(1, area.Width - w), 0, 1);
        PlaceAtEdge();
        UiSettings.IslandPlace = (_edge, _along);
    }

    private void Island_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = false;
        _press.Start();
    }

    private void Island_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _press.Stop();
        if (_dragging) EndDrag();
        if (_dragged) e.Handled = true; // отпустили после перетаскивания — это не клик
    }

    // Курсор в единицах WPF на экране (GetCursorPos — в пикселях).
    private Point CursorOnScreen()
    {
        var p = PointToScreen(Mouse.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    private Vector _grab;

    // Перетаскиваем сами (захват мыши окном), а не системным переносом окна: кнопка чата под пальцем
    // уже захватила мышь, и системный перенос в этом случае сразу обрывается.
    private void StartDrag()
    {
        _press.Stop();
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        _dragged = true;
        _dragging = true;
        _quick?.Close();
        _grab = CursorOnScreen() - new Point(Left, Top);
        CaptureMouse();
        PreviewMouseMove += OnDragMove;
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(120)));
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(120)));
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        var p = CursorOnScreen() - _grab;
        Left = p.X;
        Top = p.Y;
    }

    private void EndDrag()
    {
        _dragging = false;
        PreviewMouseMove -= OnDragMove;
        ReleaseMouseCapture();
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        SnapToNearestEdge();
    }

    // ===================================================================== Показ / скрытие

    /// <summary>Появление у своей стороны: капсула «раскрывается».</summary>
    public void ShowIsland()
    {
        var shown = IsVisible && !_hiding;
        var reversing = IsVisible && _hiding; // ещё гаснет после прошлого скрытия
        _hiding = false;
        Refresh();
        if (shown) return; // уже на экране — повторная анимация появления выглядит как мерцание

        var duration = TimeSpan.FromMilliseconds(380);
        if (reversing)
        {
            // Не начинаем заново с маленькой капсулы — плавно разворачиваем скрытие с того места, где оно сейчас.
            // (Иначе островок «прыгает» и будто появляется второй раз.)
            var back = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = back });
            PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = back });
            return;
        }

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        UpdateLayout();
        PlaceAtEdge();

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        var grow = new DoubleAnimation(IsSide ? 0.8 : 0.55, 1, duration) { EasingFunction = ease };
        // Мини-окно (с заготовкой ленты) строим после появления: тяжёлая работа посреди анимации даёт рывки.
        grow.Completed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { if (!_hiding) _mini ??= CreateMini(); });
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(IsSide ? 0.55 : 0.8, 1, duration) { EasingFunction = ease });
    }

    /// <summary>
    /// Подготовить окно заранее (невидимо, за экраном): первое создание окна долгое, и без этого
    /// первое появление островка запаздывало и мелькало не того размера и не на своём месте.
    /// </summary>
    public void Prewarm()
    {
        if (IsVisible) return;
        Refresh();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        _prewarming = true;
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        Hide();
        _prewarming = false;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _mini ??= CreateMini());
    }

    private bool _prewarming;

    /// <summary>Скрытие: сжимается в капсулу и гаснет.</summary>
    public void HideIsland()
    {
        _mini?.CloseAnimated();
        _quick?.Close();
        if (!IsVisible || _hiding) return;
        _hiding = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(200);
        var fade = new DoubleAnimation(Opacity, 0, duration);
        fade.Completed += (_, _) => { if (_hiding) Hide(); };
        BeginAnimation(OpacityProperty, fade);
        PillScale.BeginAnimation(IsSide ? ScaleTransform.ScaleYProperty : ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.6, duration) { EasingFunction = ease });
    }

    /// <summary>Скрыть сразу, без анимации.</summary>
    public void HideNow()
    {
        _hiding = true;
        _quick?.Close();
        BeginAnimation(OpacityProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Hide();
    }

    // ===================================================================== Клики

    private void AppButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dragged) return;
        _mini?.HideNow();
        HideIsland(); // гаснет плавно, как при открытии из трея
        Dispatcher.BeginInvoke(DispatcherPriority.Background, _restoreMain);
    }

    // Клик / двойной клик: первый клик ждёт системное время двойного клика. Пришёл второй клик — мини-окно,
    // не пришёл — быстрый ответ. (Если открывать быстрый ответ сразу, его окно забирает фокус и второй клик теряется.)
    private DispatcherTimer? _clickTimer;
    private ChatItemViewModel? _quickChat;
    private bool _toggleOff;
    private DateTime _quickClosedAt;
    private ChatItemViewModel? _pendingChat;
    private Rect _pendingAnchor;

    private void OpenFullMini(ChatItemViewModel chat)
    {
        _quick?.Close();
        var conv = _shell.OpenMiniChat(chat.Peer);
        _mini ??= CreateMini();
        _mini.ShowChat(conv, Top + ActualHeight - 14);
    }

    // Если мини-окно уже открыто — клик просто переключает в нём чат.
    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (_dragged || sender is not FrameworkElement { DataContext: ChatItemViewModel chat } button) return;
        if (_mini is { IsVisible: true })
        {
            OpenFullMini(chat);
            return;
        }
        if (_clickTimer is { IsEnabled: true } && ReferenceEquals(_pendingChat, chat))
        {
            _clickTimer.Stop(); // второй клик подряд — полное мини-окно
            _pendingChat = null;
            OpenFullMini(chat);
            return;
        }
        _pendingChat = chat;
        var anchor = new Rect(button.PointToScreen(new Point(0, 0)), button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight)));
        // Координаты экрана в пикселях → в единицы WPF.
        var dpi = VisualTreeHelper.GetDpi(this);
        _pendingAnchor = new Rect(anchor.X / dpi.DpiScaleX, anchor.Y / dpi.DpiScaleY, anchor.Width / dpi.DpiScaleX, anchor.Height / dpi.DpiScaleY);
        // Быстрый ответ уже открыт (или только что закрылся от этого же клика) — сразу переключаем его на этот чат.
        var quickOpen = _quick is { IsVisible: true } || DateTime.UtcNow - _quickClosedAt < TimeSpan.FromMilliseconds(400);
        if (quickOpen && !ReferenceEquals(chat, _quickChat))
        {
            _clickTimer?.Stop();
            _pendingChat = null;
            OpenQuickReply(chat, _pendingAnchor);
            return;
        }
        // Клик по чату открытого быстрого ответа — закрыть его (а второй клик подряд всё так же откроет мини-окно).
        _toggleOff = quickOpen;
        _quick?.Close();
        if (_clickTimer is null)
        {
            _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime) };
            _clickTimer.Tick += (_, _) =>
            {
                _clickTimer.Stop();
                if (_pendingChat is { } single && !_toggleOff) OpenQuickReply(single, _pendingAnchor);
                _pendingChat = null;
            };
        }
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void OpenQuickReply(ChatItemViewModel chat, Rect anchor)
    {
        _quick?.Close();
        _quickChat = chat;
        var conv = _shell.OpenMiniChat(chat.Peer);
        _quick = new QuickReplyWindow(conv, anchor, _edge, openFull: () =>
        {
            _mini ??= CreateMini();
            _mini.ShowChat(conv, Top + ActualHeight - 14);
        });
        _quick.Closed += (_, _) =>
        {
            _quick = null;
            _quickClosedAt = DateTime.UtcNow;
            if (_mini is not { IsVisible: true }) _shell.MiniChat = null; // чат больше не «открыт» — снова считаем непрочитанные
        };
        _quick.Show();
    }

    private MiniChatWindow CreateMini()
    {
        var mini = new MiniChatWindow(back: peer =>
        {
            // Сначала убираем мини-окно и островок, чат готовим, пока окно ещё свёрнуто,
            // и только потом показываем уже готовое приложение — без промежуточной перерисовки.
            _mini?.HideNow();
            HideIsland();
            _shell.OpenChat(peer);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, _restoreMain);
        });
        mini.IsVisibleChanged += (_, e) => { if (e.NewValue is false) _shell.MiniChat = null; };
        mini.Closed += (_, _) => _mini = null;
        return mini;
    }
}
