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
        IslandPlayer.HideRequested += () => SetMusicOpen(false);
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

    // Чаты островка — одна и та же коллекция: изменения переставляют строки на месте, а не пересоздают все
    // аватарки (иначе островок «моргал», будто обновляется целиком, и терял клик в этот момент).
    private readonly System.Collections.ObjectModel.ObservableCollection<ChatItemViewModel> _chats = [];

    private void Refresh()
    {
        var chats = _shell.IslandChats();
        if (_chats.SequenceEqual(chats)) return;
        foreach (var c in chats) c.EnsureAvatar(_shell.Chats.Media);
        for (var i = _chats.Count - 1; i >= 0; i--)
            if (!chats.Contains(_chats[i])) _chats.RemoveAt(i);
        for (var i = 0; i < chats.Count; i++)
        {
            var at = _chats.IndexOf(chats[i]);
            if (at < 0) _chats.Insert(i, chats[i]);
            else if (at != i) _chats.Move(at, i);
        }
        if (!ReferenceEquals(ChatsList.ItemsSource, _chats)) ChatsList.ItemsSource = _chats;
    }

    // Новое входящее — только обновить порядок (без «подпрыгивания»: оно выглядело как лишняя анимация обновления).
    private void OnIncoming(ChatItemViewModel chat) => Anim.Ui(() =>
    {
        if (!IsVisible || _hiding) return;
        Refresh();
    });

    // ===================================================================== Сторона экрана

    private bool IsSide => _edge is "Left" or "Right";

    // Мини-плеер музыки: клик по обложке — управление плавно разворачивается прямо в островке, крестик — сворачивает
    // (музыка при этом играет дальше).
    private bool _musicOpen;

    private void MusicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dragged) return;
        SetMusicOpen(!_musicOpen);
    }

    private void SetMusicOpen(bool open)
    {
        _musicOpen = open;
        var anim = new DoubleAnimation(open ? 278 : 0, TimeSpan.FromMilliseconds(open ? 240 : 180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        MusicPanel.BeginAnimation(WidthProperty, anim);
    }

    private void ApplyEdge()
    {
        IslandOrientation = IsSide ? Orientation.Vertical : Orientation.Horizontal;
        Divider.Width = IsSide ? 26 : 1;
        Divider.Height = IsSide ? 1 : 26;
        Divider.Margin = IsSide ? new Thickness(0, 10, 0, 6) : new Thickness(10, 0, 6, 0);
        MusicDivider.Width = IsSide ? 26 : 1;
        MusicDivider.Height = IsSide ? 1 : 26;
        MusicDivider.Margin = IsSide ? new Thickness(0, 6, 0, 4) : new Thickness(6, 0, 4, 0);
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
    // Островок живёт на своём мониторе (куда его перетащили): всё считаем в пикселях экрана по рабочей области
    // этого монитора — раньше бралась только основная, и на другой монитор островок не переносился.
    private string? _screen = UiSettings.IslandScreen;

    private IntPtr Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;
    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void PlaceAtEdge()
    {
        if (Hwnd == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0) return;
        var s = Scale;
        int w = (int)Math.Round(ActualWidth * s), h = (int)Math.Round(ActualHeight * s), gap = (int)Math.Round(Gap * s);
        var a = Monitors.WorkArea(_screen);
        int x, y;
        switch (_edge)
        {
            case "Left":
                x = a.Left + gap - (int)Math.Round(ShadowLeft * s);
                y = a.Top + (int)((a.Bottom - a.Top - h) * _along);
                break;
            case "Right":
                x = a.Right - w + (int)Math.Round(ShadowRight * s) - gap;
                y = a.Top + (int)((a.Bottom - a.Top - h) * _along);
                break;
            case "Bottom":
                x = a.Left + (int)((a.Right - a.Left - w) * _along);
                y = a.Bottom - h + (int)Math.Round(ShadowBottom * s) - gap;
                break;
            default:
                x = a.Left + (int)((a.Right - a.Left - w) * _along);
                y = a.Top + gap - (int)Math.Round(ShadowTop * s);
                break;
        }
        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        SetWindowPos(Hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // После перетаскивания — к ближайшей стороне того монитора, где отпустили; место вдоль неё — где отпустили.
    private void SnapToNearestEdge()
    {
        GetWindowRect(Hwnd, out var r);
        var center = new PointPx { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        var (a, device) = Monitors.At(center.X, center.Y);
        _screen = device;
        double cx = center.X, cy = center.Y;
        var distances = new Dictionary<string, double>
        {
            ["Top"] = cy - a.Top,
            ["Bottom"] = a.Bottom - cy,
            ["Left"] = cx - a.Left,
            ["Right"] = a.Right - cx,
        };
        _edge = distances.MinBy(d => d.Value).Key;
        ApplyEdge();
        UpdateLayout();
        var s = Scale;
        var w = ActualWidth * s;
        var h = ActualHeight * s;
        _along = IsSide
            ? Math.Clamp((cy - h / 2 - a.Top) / Math.Max(1, a.Bottom - a.Top - h), 0, 1)
            : Math.Clamp((cx - w / 2 - a.Left) / Math.Max(1, a.Right - a.Left - w), 0, 1);
        PlaceAtEdge();
        UiSettings.IslandPlace = (_edge, _along);
        UiSettings.IslandScreen = _screen;
    }


    private void Island_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // В развёрнутом плеере (перемотка и кнопки) удержание не тащит островок.
        if (e.OriginalSource is DependencyObject d && IslandPlayer.IsAncestorOf(d)) { _dragged = false; return; }
        _dragged = false;
        _press.Start();
    }

    private void Island_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _press.Stop();
        if (_dragging) EndDrag();
        if (_dragged) e.Handled = true; // отпустили после перетаскивания — это не клик
    }

    private (int X, int Y) _grabPx;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct PointPx { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct RectPx { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out PointPx p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RectPx r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // Перетаскиваем сами (захват мыши окном), а не системным переносом окна: кнопка чата под пальцем
    // уже захватила мышь, и системный перенос в этом случае сразу обрывается.
    private void StartDrag()
    {
        _press.Stop();
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        _dragged = true;
        _dragging = true;
        _quick?.Close();
        // Двигаем окно напрямую системой, в пикселях экрана: одно перемещение за шаг мыши, без пересчёта
        // Left/Top через WPF (два перемещения прозрачного окна с перерисовкой — отсюда «заторможенность»).
        GetCursorPos(out var c);
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var r);
        _grabPx = (c.X - r.Left, c.Y - r.Top);
        CaptureMouse();
        PreviewMouseMove += OnDragMove;
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(120)));
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(120)));
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        GetCursorPos(out var c);
        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle, IntPtr.Zero, c.X - _grabPx.X, c.Y - _grabPx.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
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
            // Мини-окно убираем, островок плавно гаснет; приложение показываем после анимации, сразу
            // (свёрнутое окно WPF не рисует: чат, открытый в нём заранее, появлялся через ~1 с чёрным окном),
            // а чат переключаем уже в развёрнутом окне — это быстро.
            _mini?.HideNow();
            HideIsland();
            var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                _restoreMain();
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _shell.OpenChat(peer));
            };
            later.Start();
        });
        mini.IsVisibleChanged += (_, e) => { if (e.NewValue is false) _shell.MiniChat = null; };
        mini.Closed += (_, _) => _mini = null;
        return mini;
    }
}
