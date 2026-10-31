using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;
using TgCheck.Core.Media;

namespace TgCheck.App.Views;

/// <summary>
/// Лента чата: старт снизу, «прилипание» к низу при новых сообщениях, подгрузка истории без скачков,
/// перемотка голосовых, перетаскивание файлов, видеоплеер.
/// </summary>
public partial class ConversationView : UserControl
{
    private const double BottomThreshold = 60;
    private ConversationViewModel? _vm;
    private bool _stickToBottom = true;
    private bool _loadingOlder;
    private readonly DispatcherTimer _videoTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _videoPlaying;

    /// <summary>Показывать кнопку «назад» в шапке (узкое окно, мини-окно).</summary>
    public static readonly DependencyProperty ShowBackProperty = DependencyProperty.Register(
        nameof(ShowBack), typeof(bool), typeof(ConversationView),
        new PropertyMetadata(false, (d, e) => ((ConversationView)d).UpdateBack()));

    // «Назад» видна в узком окне и всегда — в ветке комментариев.
    private void UpdateBack() =>
        BackButton.Visibility = ShowBack || _vm?.IsThread == true ? Visibility.Visible : Visibility.Collapsed;

    public bool ShowBack { get => (bool)GetValue(ShowBackProperty); set => SetValue(ShowBackProperty, value); }

    /// <summary>
    /// Узкий режим (мини-окно): мини-профиль открывается поверх чата на всю ширину, с кнопкой «назад» к чату.
    /// </summary>
    public bool IsCompactPanel
    {
        get => _compactPanel;
        set
        {
            _compactPanel = value;
            Grid.SetColumn(SidePanel, value ? 0 : 1);
            Grid.SetColumnSpan(SidePanel, value ? 2 : 1);
            SidePanel.Width = value ? double.NaN : 320;
            SidePanel.BorderThickness = value ? new Thickness(0) : new Thickness(1, 0, 0, 0);
            Panel.SetZIndex(SidePanel, value ? 10 : 0);
            SideCompactBack.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            SideCloseButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            SideStackBack.Opacity = value ? 0 : 1; // в узком режиме роль «назад» у SideCompactBack
            SideStackBack.IsHitTestVisible = !value;
            if (value) SideStackBack.Width = 0;
            // В мини-окне полного профиля нет — кнопка «Открыть профиль» там не нужна.
            if (value) OpenFullProfileButton.Visibility = Visibility.Collapsed;
        }
    }

    private bool _compactPanel;

    public ConversationView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => { Detach(); VideoPlayer.Stop(); _videoTimer.Stop(); MusicNow.Instance.Resume(MusicNow.Video); };
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Composer.Focus());
        _videoTimer.Tick += (_, _) => UpdateVideoTime();
        // Кружок начали слушать — отмечаем прослушанным (точка пропадает и у собеседника).
        AddHandler(Controls.VideoNoteView.SoundStartedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is FrameworkElement { DataContext: MessageViewModel m }) _vm?.MarkListened(m);
        }));
        Anim.SetShow(JumpButton, false);
        JumpButton.Visibility = Visibility.Collapsed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _vm = DataContext as ConversationViewModel;
        if (_vm is null) return;
        _vm.ScrollToBottomRequested += ScrollToBottom;
        _vm.ScrollToMessageRequested += ScrollToMessage;
        _vm.FeedResetting += HideFeed;
        _vm.InsertTextRequested += InsertText;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.IsAtBottom = () => IsNearBottom(Scroller.VerticalOffset);
        _vm.VisibleMaxId = VisibleMaxId;
        PreviewWave.SeekRequested -= OnPreviewSeek;
        PreviewWave.SeekRequested += OnPreviewSeek;
        _vm.Items.CollectionChanged += OnItemsChanged;
        UpdateBack();
        // В теме форума справа островок тем — лента не должна уходить под него.
        Scroller.Padding = _vm.ShowTopicRail ? new Thickness(18, 6, 80, 10) : new Thickness(18, 6, 12, 10);
        JumpButton.Margin = _vm.ShowTopicRail ? new Thickness(0, 0, 84, 14) : new Thickness(0, 0, 22, 14);
        FeedTop.Margin = _vm.ShowTopicRail ? new Thickness(0, 0, 78, 0) : new Thickness(0); // плашка закрепа и плеер — не под островком тем
        // Первая загрузка: лента появляется уже прокрученной вниз, без мигания и прыжка сверху вниз.
        if (_vm.Items.Count == 0) HideFeed();
        _stickToBottom = true;
        ScrollToBottom(false);
    }

    private void OnPreviewSeek(double fraction) => _vm?.SeekPreview(fraction);

    private void Detach()
    {
        if (_vm is null) return;
        _vm.ScrollToBottomRequested -= ScrollToBottom;
        _vm.ScrollToMessageRequested -= ScrollToMessage;
        _vm.FeedResetting -= HideFeed;
        _vm.InsertTextRequested -= InsertText;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.IsAtBottom = null;
        _vm.VisibleMaxId = null;
        _vm.Items.CollectionChanged -= OnItemsChanged;
        _vm = null;
    }

    private bool IsNearBottom(double offset) => offset >= Scroller.ScrollableHeight - BottomThreshold;

    private void ScrollToBottom(bool animated)
    {
        _stickToBottom = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Scroller.UpdateLayout();
            if (animated && _feedHidden && _vm is { Items.Count: > 0 })
            {
                // Лента только что пересобрана (возврат вниз от закрепа): встаём чуть выше конца и доезжаем плавно.
                SmoothScroll.JumpTo(Scroller, Math.Max(0, Scroller.ScrollableHeight - Scroller.ViewportHeight * 1.2));
                RevealFeed();
                SmoothScroll.ScrollTo(Scroller, Scroller.ScrollableHeight);
                return;
            }
            if (animated && !_feedHidden)
            {
                // Далеко от низа — сначала прыжок поближе, затем короткий плавный доезд (а не через всю ленту).
                if (Scroller.ScrollableHeight - Scroller.VerticalOffset > Scroller.ViewportHeight * 1.5)
                    SmoothScroll.JumpTo(Scroller, Math.Max(0, Scroller.ScrollableHeight - Scroller.ViewportHeight * 1.2));
                _bottomIntentUntil = DateTime.UtcNow.AddMilliseconds(700);
                SmoothScroll.ScrollTo(Scroller, Scroller.ScrollableHeight);
            }
            else SmoothScroll.JumpTo(Scroller, Scroller.ScrollableHeight);
            if (_vm is { Items.Count: > 0 }) RevealFeed();
        });
    }

    private DateTime _bottomIntentUntil;

    // Лента пересобирается (переход к далёкому сообщению): прячем её, пока не встанет на место, — без мигания.
    private bool _feedHidden;

    private void HideFeed()
    {
        _feedHidden = true;
        Scroller.BeginAnimation(OpacityProperty, null);
        Scroller.Opacity = 0;
    }

    private void RevealFeed()
    {
        if (!_feedHidden) return;
        _feedHidden = false;
        // Без анимации: при переключении с другого чата лента просто появляется уже на месте.
        Scroller.BeginAnimation(OpacityProperty, null);
        Scroller.Opacity = 1;
        MarkReadSoon();
    }

    // ---- якорь прокрутки: что бы ни менялось выше экрана (подгрузка истории, удаление, вторая часть ленты),
    // видимое сообщение остаётся на месте — сдвиг применяется до отрисовки кадра, без рывка.
    private readonly List<(FrameworkElement El, double Top)> _anchors = [];
    private bool _anchorPending;

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_anchorPending || _feedHidden || _stickToBottom || !Scroller.IsLoaded) return;
        _anchors.Clear();
        foreach (var (el, top) in VisibleContainers(fromEnd: false))
        {
            _anchors.Add((el, top));
            if (_anchors.Count == 3) break;
        }
        if (_anchors.Count == 0) return;
        _anchorPending = true;
        Feed.LayoutUpdated += RestoreAnchor;
    }

    private void RestoreAnchor(object? sender, EventArgs e)
    {
        Feed.LayoutUpdated -= RestoreAnchor;
        _anchorPending = false;
        if (_feedHidden || _stickToBottom) return;
        foreach (var (el, top) in _anchors)
        {
            // Якорь удалён из ленты — берём следующий.
            if (Feed.ItemContainerGenerator.ItemFromContainer(el) == DependencyProperty.UnsetValue || !el.IsVisible) continue;
            var delta = el.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y - top;
            if (Math.Abs(delta) > 0.5) SmoothScroll.ShiftBy(Scroller, delta);
            break;
        }
        _anchors.Clear();
    }

    /// <summary>Сообщения на экране (верх относительно окна прокрутки): сверху вниз или снизу вверх.</summary>
    private IEnumerable<(FrameworkElement El, double Top)> VisibleContainers(bool fromEnd)
    {
        var count = Feed.Items.Count;
        var view = Scroller.ViewportHeight;
        for (var n = 0; n < count; n++)
        {
            var i = fromEnd ? count - 1 - n : n;
            if (Feed.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el || !el.IsVisible) continue;
            var top = el.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y;
            var bottom = top + el.ActualHeight;
            if (fromEnd ? top >= view : bottom <= 0) continue; // ещё не дошли до экрана
            if (fromEnd ? bottom <= 0 : top >= view) yield break; // экран пройден
            yield return (el, top);
        }
    }

    // id последнего сообщения, которое видно на экране: читаются только они (пришедшие ниже — нет, пока не прокрутите).
    private int VisibleMaxId()
    {
        if (_vm is null || _feedHidden || !IsVisible) return 0;
        if (IsNearBottom(Scroller.VerticalOffset)) return int.MaxValue;
        foreach (var (el, top) in VisibleContainers(fromEnd: true))
            if (el.DataContext is MessageViewModel m && top < Scroller.ViewportHeight - 24) return m.AllIds.Max();
        return 0;
    }

    // Голосовые и кружки на экране качаются сами (с ободком и крестиком) — к нажатию «играть» они уже готовы.
    private void AutoDownloadVisible()
    {
        if (_vm is null || _feedHidden || Controls.AppServices.Media is not { } media) return;
        foreach (var (el, _) in VisibleContainers(fromEnd: true))
            if (el.DataContext is MessageViewModel { IsVoice: true } or MessageViewModel { IsRound: true }) ((MessageViewModel)el.DataContext).AutoDownload(media);
    }

    private DispatcherTimer? _readTimer;

    // Прочтение по мере прокрутки — не на каждый пиксель, а когда прокрутка остановилась.
    private void MarkReadSoon()
    {
        if (_readTimer is null)
        {
            _readTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _readTimer.Tick += (_, _) => { _readTimer.Stop(); _vm?.MarkRead(); AutoDownloadVisible(); };
        }
        _readTimer.Stop();
        _readTimer.Start();
    }

    // Переход к сообщению (закреп, ответ): сообщение — по центру экрана.
    private void ScrollToMessage(MessageViewModel message)
    {
        _stickToBottom = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Scroller.UpdateLayout();
            TgCheck.Core.Services.Logging.Write($"[perf] jump: раскладка ленты {sw.ElapsedMilliseconds} мс");
            if (Feed.ItemContainerGenerator.ContainerFromItem(message) is FrameworkElement el)
            {
                var top = el.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y + Scroller.VerticalOffset;
                var target = Math.Clamp(top - (Scroller.ViewportHeight - el.ActualHeight) / 2, 0, Scroller.ScrollableHeight);
                // После пересборки или если сообщение далеко — сразу на место (плавная прокрутка через сотни сообщений
                // отрисовывает их все и тормозит); если рядом — плавно.
                if (_feedHidden || Math.Abs(target - Scroller.VerticalOffset) > Scroller.ViewportHeight * 1.5) SmoothScroll.JumpTo(Scroller, target);
                else SmoothScroll.ScrollTo(Scroller, target);
            }
            RevealFeed();
        });
    }

    /// <summary>
    /// Печать «в никуда» (фокус не в поле ввода) уходит в поле сообщения — как в Telegram.
    /// false — писать сюда нельзя (канал, форум) или поле занято окном отправки.
    /// </summary>
    public bool TypeIntoComposer(string text)
    {
        if (_vm is not { CanWrite: true, HasAttachments: false, IsRecording: false } || !Composer.IsVisible) return false;
        Composer.Focus();
        Composer.SelectedText = "";
        Composer.CaretIndex = Composer.Text.Length;
        Composer.AppendText(text);
        Composer.CaretIndex = Composer.Text.Length;
        return true;
    }

    private void InsertText(string text)
    {
        var caret = Composer.CaretIndex;
        Composer.Text = Composer.Text.Insert(caret, text);
        Composer.CaretIndex = caret + text.Length;
        Composer.Focus();
    }

    private async void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null) return;

        if (e.ExtentHeightChange != 0 && e.VerticalChange == 0)
        {
            // Контент вырос (догрузились картинки/пришли сообщения): если были внизу — остаёмся внизу.
            // Во время доезда вниз — новая цель для плавной прокрутки (иначе она остановилась бы на старом конце).
            if (_stickToBottom && DateTime.UtcNow < _bottomIntentUntil) SmoothScroll.ScrollTo(Scroller, Scroller.ScrollableHeight);
            else if (_stickToBottom) Scroller.ScrollToEnd();
        }
        else
        {
            // Пока лента сама доезжает вниз (после отправки) — «держаться внизу» не сбрасываем: иначе дорастающий
            // новый пузырь оставлял ленту чуть выше конца.
            _stickToBottom = IsNearBottom(Scroller.VerticalOffset) || DateTime.UtcNow < _bottomIntentUntil;
        }

        var atBottom = IsNearBottom(Scroller.VerticalOffset);
        var showJump = (!atBottom && Scroller.ScrollableHeight > 1) || _vm.IsDetached;
        if (Anim.GetShow(JumpButton) != showJump) Anim.SetShow(JumpButton, showJump);
        if (e.VerticalChange != 0 || e.ExtentHeightChange != 0) MarkReadSoon();
        if (e.VerticalChange != 0 && _vm.HasPinned) { _pinnedTimer.Stop(); _pinnedTimer.Start(); }

        // Фоновая подгрузка истории заранее (за ~1500 px до верха): позицию держит якорь прокрутки — незаметно для глаза.
        if (!_loadingOlder && !_feedHidden && Scroller.VerticalOffset < 1500 && e.VerticalChange < 0 && _vm.HasMoreHistory)
        {
            _loadingOlder = true;
            _stickToBottom = false;
            try { await _vm.LoadOlderAsync(); }
            finally { _loadingOlder = false; }
        }
    }

    // Плашка закрепа следует за прокруткой: после остановки (150 мс) — ближайший закреп выше видимой части.
    private DispatcherTimer? _pinnedTimerField;
    private DispatcherTimer _pinnedTimer
    {
        get
        {
            if (_pinnedTimerField is not null) return _pinnedTimerField;
            _pinnedTimerField = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _pinnedTimerField.Tick += (_, _) =>
            {
                _pinnedTimerField.Stop();
                if (_vm is not null && TopVisibleMessageId() is { } id) _vm.SyncPinnedToViewport(id);
            };
            return _pinnedTimerField;
        }
    }

    // Верхнее видимое сообщение: двоичный поиск по ленте (сообщения идут сверху вниз).
    private int? TopVisibleMessageId()
    {
        if (_vm is null || _vm.Items.Count == 0) return null;
        int lo = 0, hi = _vm.Items.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Feed.ItemContainerGenerator.ContainerFromIndex(mid) is not FrameworkElement el || !el.IsVisible) return null;
            var bottom = el.TransformToAncestor(Scroller).Transform(new Point(0, el.ActualHeight)).Y;
            if (bottom > 0) { found = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        for (var i = Math.Max(found, 0); i < _vm.Items.Count; i++)
            if (_vm.Items[i] is MessageViewModel m) return m.Id;
        return null;
    }

    private void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
                break;
            case Key.V when Keyboard.Modifiers == ModifierKeys.Control:
                if (_vm.PasteFromClipboard()) { e.Handled = true; FocusCaption(); }
                break;
            case Key.Up when Composer.Text.Length == 0:
                // Как в Telegram: ↑ в пустом поле — редактировать последнее своё сообщение.
                var lastOwn = _vm.Items.OfType<MessageViewModel>().LastOrDefault(m => m.IsOut && m.HasText);
                if (lastOwn is not null)
                {
                    e.Handled = true;
                    _vm.EditCommand.Execute(lastOwn);
                    Composer.CaretIndex = Composer.Text.Length;
                }
                break;
        }
    }

    // Выделение: Ctrl+клик начинает, а пока что-то выбрано — обычный клик выбирает / снимает (медиа не открываются).
    private void MessageRow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { DataContext: MessageViewModel m }) return;
        if (!_vm.IsSelecting && Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        _vm.ToggleSelect(m);
    }

    // Кружок у пузыря — открыть меню сообщения с панелью реакций (как в Telegram Desktop).
    private void ReactButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Parent: Grid wrap } || wrap.Children.OfType<Border>().FirstOrDefault() is not { ContextMenu: { } menu } bubble) return;
        menu.PlacementTarget = bubble;
        menu.DataContext = bubble.DataContext;
        menu.IsOpen = true;
    }

    // Реакция выбрана в меню — закрываем меню (кнопка внутри меню сама его не закрывает).
    private void Reaction_Click(object sender, RoutedEventArgs e)
    {
        DependencyObject? p = sender as DependencyObject;
        while (p is not null && p is not System.Windows.Controls.ContextMenu) p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p);
        if (p is System.Windows.Controls.ContextMenu menu) menu.IsOpen = false;
    }

    // ---- запись голосового: логика кнопки микрофона Telegram Desktop
    // Держите — запись; отпустили — прослушивание и обрезка, затем «Отправить»; вверх — запись закрепляется (руки свободны);
    // увели влево и отпустили — отмена. Раньше отменой считался любой выход курсора за кнопку,
    // а она во время записи визуально больше, чем её область нажатия, — запись то и дело «путалась» и отменялась.
    private const double LockDistance = 60, CancelDistance = 110;
    private Point _micStart;
    private bool _micReleasing;
    private bool _micCancelArmed;

    private void Mic_Down(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        e.Handled = true;
        if (_vm.IsRecordLocked || _vm.IsVoicePreview)
        {
            _vm.SendRecordingCommand.Execute(null); // закреплённая или прослушанная запись: кнопка — «отправить»
            return;
        }
        if (_vm.IsRecording) return;
        _micStart = e.GetPosition(this);
        _micReleasing = false;
        SetCancelArmed(false);
        MicButton.CaptureMouse();
        _vm.StartRecording();
    }

    private void Mic_Move(object sender, MouseEventArgs e)
    {
        if (_vm is not { IsRecording: true, IsRecordLocked: false } || !MicButton.IsMouseCaptured) return;
        var pos = e.GetPosition(this);
        var up = _micStart.Y - pos.Y;
        var left = _micStart.X - pos.X;
        if (up > LockDistance && up > Math.Abs(left))
        {
            // Потянули вверх — запись продолжается без удержания.
            _vm.LockRecordingCommand.Execute(null);
            _micReleasing = true;
            SetCancelArmed(false);
            MicButton.ReleaseMouseCapture();
            return;
        }
        // Увели влево достаточно далеко — отпускание отменит запись (кнопка краснеет, подсказка меняется).
        SetCancelArmed(left > CancelDistance);
    }

    private void SetCancelArmed(bool armed)
    {
        if (_micCancelArmed == armed) return;
        _micCancelArmed = armed;
        CancelHintText.Text = armed ? "Отпустите — отмена" : "Влево — отмена";
        UpdateMicVisual();
    }

    private void Mic_Up(object sender, MouseButtonEventArgs e)
    {
        if (_vm is not { IsRecording: true, IsRecordLocked: false } || !MicButton.IsMouseCaptured) return;
        e.Handled = true;
        var cancel = _micCancelArmed;
        _micReleasing = true;
        SetCancelArmed(false);
        MicButton.ReleaseMouseCapture();
        // Отпустили — не отправляем сразу, а переходим к прослушиванию и обрезке; отправит кнопка «Отправить».
        if (cancel) _ = _vm.FinishRecordingAsync(cancel: true);
        else _vm.StopRecordingCommand.Execute(null);
    }

    // Захват потерян не по нашей воле (Alt+Tab и т.п.) — запись отменяется.
    private void Mic_LostCapture(object sender, MouseEventArgs e)
    {
        if (_micReleasing || _vm is not { IsRecording: true, IsRecordLocked: false }) return;
        SetCancelArmed(false);
        _ = _vm.FinishRecordingAsync(cancel: true);
    }

    private void UpdateMicVisual()
    {
        if (_vm is null) return;
        var recording = _vm.IsRecording;
        var sendMode = _vm.IsRecordLocked || _vm.IsVoicePreview;
        MicCircle.Fill = _micCancelArmed && recording ? (Brush)FindResource("DangerBrush")
            : recording || sendMode ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
        MicIcon.Foreground = recording || sendMode ? Brushes.White : (Brush)FindResource("TextSecondaryBrush");
        MicIcon.Text = sendMode ? "" : _micCancelArmed ? "" : "";
        // Начало/конец записи — кнопка плавно вырастает в большой круг (и обратно).
        if (recording != _micBig)
        {
            _micBig = recording;
            var grow = new DoubleAnimation(recording ? 1.3 : 1, TimeSpan.FromMilliseconds(recording ? 220 : 140))
            {
                EasingFunction = recording ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } : new CubicEase(),
            };
            MicCircleScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            MicCircleScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            MicPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(recording ? 0.22 : 0, TimeSpan.FromMilliseconds(200)));
            if (recording)
            {
                var bob = new DoubleAnimation(0, -5, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
                MicLockArrowMove.BeginAnimation(TranslateTransform.YProperty, bob);
            }
            else MicLockArrowMove.BeginAnimation(TranslateTransform.YProperty, null);
        }
        // Замок — пока запись удерживается; закрепили — прячем.
        var showLock = recording && !_vm.IsRecordLocked;
        if (showLock != _micLockShown)
        {
            _micLockShown = showLock;
            MicLock.BeginAnimation(OpacityProperty, new DoubleAnimation(showLock ? 1 : 0, TimeSpan.FromMilliseconds(180)));
        }
        // Ореол «дышит» по громкости голоса, плавно (сглаживание — чтобы не дёргался на каждом замере).
        var target = recording ? 1.42 + Math.Min(_vm.RecordLevel * 1.8, 0.28) : 1; // в пределах окна: кнопка у самого края
        _micLevel += (target - _micLevel) * 0.35;
        var pulse = new DoubleAnimation(recording ? _micLevel : 1, TimeSpan.FromMilliseconds(60));
        MicPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        MicPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    private bool _micBig, _micLockShown;
    private double _micLevel = 1;

    // ---- окно отправки вложений: Enter в подписи — отправить, Ctrl+V — добавить ещё
    private void Caption_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && _vm.PasteFromClipboard()) e.Handled = true;
    }

    private void FocusCaption() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        CaptionBox.Focus();
        CaptionBox.CaretIndex = CaptionBox.Text.Length;
    });

    // ---- перемотка голосового кликом по волне
    private void Wave_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MessageViewModel m } wave || _vm is null) return;
        var fraction = e.GetPosition(wave).X / wave.ActualWidth;
        // Песни — в своём проигрывателе (VLC), голосовые — в общем.
        if (m.IsAudio && MusicPlayer.Instance.Key == m.AudioKey) MusicPlayer.Instance.Seek(fraction);
        else if (!m.IsAudio && AudioPlayer.Instance.Key == m.AudioKey) AudioPlayer.Instance.Seek(fraction);
        else _vm.PlayAudioCommand.Execute(m);
        e.Handled = true;
    }

    // ---- перетаскивание файлов
    private void Root_DragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _vm?.AddAttachments(files);
        FocusCaption();
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        base.OnDragLeave(e);
    }

    // ←/→ листают медиа в просмотрщике.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Полей ввода в просмотрщике нет, а фокус часто остаётся в поле сообщения — стрелки всё равно листают медиа.
        if (_vm?.IsViewerOpen == true)
        {
            if (e.Key == Key.Left) { _vm.ViewerPrevCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Right) { _vm.ViewerNextCommand.Execute(null); e.Handled = true; }
        }
        base.OnPreviewKeyDown(e);
    }

    // ---- видеоплеер

    // В ручном режиме MediaElement не открывает файл, пока не вызван Play() — запускаем сами, как только путь готов.
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(ConversationViewModel.IsLoading) && !_vm.IsLoading)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RevealFeed); // после прокрутки; пустой чат или ошибка — показать как есть
            return;
        }
        if (e.PropertyName is nameof(ConversationViewModel.IsRecording) or nameof(ConversationViewModel.IsRecordLocked) or nameof(ConversationViewModel.RecordLevel)
            or nameof(ConversationViewModel.IsVoicePreview))
        {
            UpdateMicVisual();
            return;
        }
        if (e.PropertyName != nameof(ConversationViewModel.ViewerVideoPath)) return;
        if (_vm.ViewerVideoPath is null) { VideoPlayer.Stop(); _videoTimer.Stop(); MusicNow.Instance.Resume(MusicNow.Video); return; }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            VideoPlayer.Play();
            _videoPlaying = true;
            PlayPauseButton.Content = "";
            _videoTimer.Start();
        });
    }
    private void VideoPlayer_MediaOpened(object? sender, EventArgs e)
    {
        AudioPlayer.Instance.Stop();
        MusicNow.Instance.Interrupt(MusicNow.Video); // видео со звуком — музыка на паузу (продолжится, когда закроют)
        VideoPlayer.Play();
        _videoPlaying = true;
        PlayPauseButton.Content = "";
        _videoTimer.Start();
    }

    // VLC не смог открыть файл (повреждён или не докачан) — превью и кнопка «Открыть в плеере».
    private void VideoPlayer_MediaFailed(object? sender, string error)
    {
        _videoTimer.Stop();
        _videoPlaying = false;
        TgCheck.Core.Services.Logging.Write($"video failed {_vm?.ViewerMessage?.Item.MimeType} {_vm?.ViewerVideoPath}: {error}");
        if (_vm is not null) _vm.ViewerVideoFailed = true;
    }

    private void VideoPlayer_MediaEnded(object? sender, EventArgs e)
    {
        if (_vm?.ViewerMessage?.IsGif == true) { VideoPlayer.Play(); return; } // GIF — по кругу (Play после конца начинает заново)
        VideoPlayer.Pause();
        _videoPlaying = false;
        PlayPauseButton.Content = "";
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPlaying) VideoPlayer.Pause();
        else VideoPlayer.Play();
        _videoPlaying = !_videoPlaying;
        PlayPauseButton.Content = _videoPlaying ? "" : "";
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        VideoPlayer.IsMuted = !VideoPlayer.IsMuted;
        MuteButton.Content = VideoPlayer.IsMuted ? "" : "";
    }

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (VideoPlayer.Duration is { } duration)
            VideoPlayer.Position = TimeSpan.FromSeconds(SeekSlider.Value * duration.TotalSeconds);
    }

    // Кнопка «копировать» у блока кода: на секунду-полторы превращается в галочку.
    private async void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        // Кнопка в шаблоне — событие ловит контейнер блоков кода.
        if (e.OriginalSource is not Button { DataContext: TextSegment segment } button) return;
        try { Clipboard.SetText(segment.Text); }
        catch { return; } // буфер обмена занят другой программой
        button.Content = "";
        button.ToolTip = "Скопировано";
        await Task.Delay(1500);
        button.Content = "";
        button.ToolTip = "Копировать";
    }

    private void UpdateVideoTime()
    {
        if (_vm?.IsViewerOpen != true) { VideoPlayer.Stop(); _videoTimer.Stop(); return; }
        if (VideoPlayer.Duration is not { } duration) return;
        var total = duration.TotalSeconds;
        var pos = VideoPlayer.Position.TotalSeconds;
        if (!SeekSlider.IsMouseCaptureWithin) SeekSlider.Value = total > 0 ? pos / total : 0;
        VideoTime.Text = $"{Fmt.Duration(pos)} / {Fmt.Duration(total)}";
    }
}
