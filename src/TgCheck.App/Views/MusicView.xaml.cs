using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

public partial class MusicView : UserControl
{
    public MusicView()
    {
        InitializeComponent();
        // Открыли поиск или страницу чатов — полки ушли с экрана (перемешать), а у новой страницы — обложки видимых строк.
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MusicViewModel music)
                music.PropertyChanged += (_, a) =>
                {
                    if (a.PropertyName is nameof(MusicViewModel.IsSearch) or nameof(MusicViewModel.IsChatsPage) or nameof(MusicViewModel.IsPlaylistPage)) QueueCovers();
                };
        };
        ChatsPageScroll.IsVisibleChanged += (_, _) => QueueCovers();
        PlaylistPageScroll.IsVisibleChanged += (_, _) => QueueCovers();
    }

    /// <summary>Видно, когда полка не пуста и не идёт поиск (на время поиска — только результаты).</summary>
    public static readonly System.Windows.Data.IMultiValueConverter BothVisible = new ShelfOutsideSearch();

    private sealed class ShelfOutsideSearch : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            values is [true, false] ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // ---- «Для вас» и плейлисты: рядом, пока плиткам хватает места; в узком окне плейлисты уходят вниз

    private const double SideBySideWidth = 1120;

    private void ForYouGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var side = e.NewSize.Width >= SideBySideWidth;
        Grid.SetColumn(PlaylistsPanel, side ? 1 : 0);
        Grid.SetRow(PlaylistsPanel, side ? 0 : 1);
        PlaylistsPanel.Margin = side ? new Thickness(30, 0, 0, 28) : new Thickness(0, 0, 0, 28);
        PlaylistsColumn.MinWidth = side ? 490 : 0;
        PlaylistsColumn.Width = side ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
    }

    // ---- перемотка, как в плеере под списком чатов

    private void SeekTo(MouseEventArgs e) =>
        MusicNow.Instance.Seek(Math.Clamp(e.GetPosition(Seek).X / Math.Max(1, Seek.ActualWidth), 0, 1));

    private void Seek_Down(object sender, MouseButtonEventArgs e)
    {
        Seek.CaptureMouse();
        SeekTo(e);
        e.Handled = true;
    }

    private void Seek_Move(object sender, MouseEventArgs e)
    {
        if (Seek.IsMouseCaptured) SeekTo(e);
    }

    private void Seek_Up(object sender, MouseButtonEventArgs e)
    {
        if (!Seek.IsMouseCaptured) return;
        Seek.ReleaseMouseCapture();
        e.Handled = true;
    }

    // Ничего не играет — «играть» запускает подбор.
    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (MusicNow.Instance.IsActive) MusicNow.Instance.ToggleCommand.Execute(null);
        else if (DataContext is MusicViewModel vm) vm.NextCommand.Execute(null);
    }

    private void CloseMusic_Click(object sender, RoutedEventArgs e) => MusicNow.Instance.CloseMusic();

    // ---- полки: листание стрелками (плавно, на ~3 плитки) и колесо

    private void ShelfLeft_Click(object sender, RoutedEventArgs e) => ScrollShelf(sender, -1);
    private void ShelfRight_Click(object sender, RoutedEventArgs e) => ScrollShelf(sender, 1);

    private static void ScrollShelf(object sender, int direction)
    {
        if ((sender as FrameworkElement)?.Tag is not ScrollViewer sv) return;
        var step = Math.Max(170, Math.Floor(sv.ViewportWidth / 170) * 170);
        var from = sv.HorizontalOffset;
        var to = Math.Clamp(from + direction * step, 0, sv.ScrollableWidth);
        var start = DateTime.UtcNow;
        void Frame(object? s, EventArgs a)
        {
            var t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / 260);
            var ease = 1 - Math.Pow(1 - t, 3);
            sv.ScrollToHorizontalOffset(from + (to - from) * ease);
            if (t >= 1) CompositionTarget.Rendering -= Frame;
        }
        CompositionTarget.Rendering += Frame;
    }

    // Колесо над полкой листает страницу (это делает плавная прокрутка страницы); с Shift — саму полку.
    private void Shelf_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var sv = (ScrollViewer)sender;
        sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset - e.Delta, 0, sv.ScrollableWidth));
        e.Handled = true;
    }

    // ---- обложки: страница просит их сама — сначала то, что видно (сверху вниз, слева направо), потом то, что рядом
    // (на экран вперёд); остальные — когда до них долистают. Раньше разом просились обложки всех плиток всех полок,
    // и видимые ждали в очереди за невидимыми.

    private bool _coversQueued;

    /// <summary>Песен на странице плейлиста в ряд: две, пока карточкам хватает ширины.</summary>
    public static readonly System.Windows.Data.IValueConverter SongColumns = new SongColumnsConverter();

    private sealed class SongColumnsConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is double width && width >= 820 ? 2 : 1;

        public object ConvertBack(object value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    private void PlaylistPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PlaylistPageScroll.IsVisible) PlaylistPageScroll.ScrollToHome();
        QueueCovers();
    }

    // Чаты и миксы в ряд, пока хватает места; иначе миксы — под чатами.
    private void MixRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var side = e.NewSize.Width >= 1000;
        Grid.SetColumn(MixesBlock, side ? 1 : 0);
        Grid.SetRow(MixesBlock, side ? 0 : 1);
        Grid.SetColumnSpan(MixesBlock, side ? 1 : 2);
    }

    // Посмотрели и ушли — при следующем показе чаты и миксы другие.
    private readonly HashSet<MusicPlaylistShelf> _seenPlaylists = [];

    private void RefreshHiddenPlaylists(MusicViewModel music, bool pageShown, Rect view)
    {
        foreach (var (el, shelf) in new (FrameworkElement, MusicPlaylistShelf)[] { (ChatsBlock, music.ChatPlaylists), (MixesBlock, music.MyPlaylists) })
        {
            var onScreen = false;
            if (pageShown && el.IsVisible)
            {
                try { onScreen = el.TransformToAncestor(PageScroll).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight)).IntersectsWith(view); }
                catch (InvalidOperationException) { }
            }
            if (onScreen) _seenPlaylists.Add(shelf);
            else if (_seenPlaylists.Remove(shelf)) music.RefreshHiddenPlaylists(shelf);
        }
    }

    private void Page_ScrollChanged(object sender, ScrollChangedEventArgs e) => QueueCovers();

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueCovers();

    private void ChatsPage_ScrollChanged(object sender, ScrollChangedEventArgs e) => QueueCovers();

    private void QueueCovers()
    {
        if (_coversQueued) return;
        _coversQueued = true;
        // После раскладки (плитки уже на местах) и не чаще кадра — прокрутка шлёт десятки событий.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _coversQueued = false;
            RequestVisibleCovers(PageScroll);
            RequestVisibleCovers(ChatsPageScroll);
            RequestVisibleCovers(PlaylistPageScroll);
            ReshuffleHiddenShelves();
        });
    }

    // Полки «вперемешку» («Любимые», «Музыка из ваших чатов») получают новый порядок, только когда их не видно: полку
    // видели, потом она ушла за край (прокрутили, открыли поиск или другой раздел) — перемешиваем.
    private readonly HashSet<MusicShelf> _seenShelves = [];

    private void ReshuffleHiddenShelves()
    {
        if (DataContext is not MusicViewModel music) return;
        var pageShown = PageScroll.IsVisible && !music.IsSearch && !music.IsChatsPage && !music.IsPlaylistPage;
        var view = new Rect(0, 0, PageScroll.ActualWidth, PageScroll.ActualHeight);
        RefreshHiddenPlaylists(music, pageShown, view);
        foreach (var shelf in music.Shelves.Where(s => s.Shuffled))
        {
            var onScreen = false;
            if (pageShown && ShelfList.ItemContainerGenerator.ContainerFromItem(shelf) is FrameworkElement { IsVisible: true } el)
            {
                try { onScreen = el.TransformToAncestor(PageScroll).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight)).IntersectsWith(view); }
                catch (InvalidOperationException) { }
            }
            if (onScreen) _seenShelves.Add(shelf);
            else if (_seenShelves.Remove(shelf)) shelf.Reshuffle();
        }
    }

    private void RequestVisibleCovers(ScrollViewer scroll)
    {
        if (!scroll.IsVisible) return;
        var view = new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight);
        var near = Rect.Inflate(view, view.Width * 0.5, view.Height);
        var later = new List<MusicTrackViewModel>();
        foreach (var (el, vm) in Tracks(scroll))
        {
            Rect box;
            try { box = el.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
            catch (InvalidOperationException) { continue; } // элемент уже убран из дерева
            if (box.IntersectsWith(view)) vm.RequestCover();
            else if (box.IntersectsWith(near)) later.Add(vm);
        }
        foreach (var vm in later) vm.RequestCover();
    }

    // Плитки и строки треков на странице (в порядке дерева: полки сверху вниз, плитки слева направо).
    private static IEnumerable<(FrameworkElement, MusicTrackViewModel)> Tracks(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: "TileButton" or "RowButton", DataContext: MusicTrackViewModel vm } el && el.IsVisible)
            {
                yield return (el, vm);
                continue;
            }
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            foreach (var found in Tracks(child)) yield return found;
        }
    }

    // ---- перетаскивание полки мышью: зажал и повёл — полка едет за курсором; клик без сдвига — обычный клик по треку

    private ScrollViewer? _drag;
    private Point _dragStart;
    private double _dragOffset;
    private bool _dragging;

    private void Shelf_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _drag = (ScrollViewer)sender;
        _dragStart = e.GetPosition(_drag);
        _dragOffset = _drag.HorizontalOffset;
        _dragging = false;
    }

    private void Shelf_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is null || !ReferenceEquals(sender, _drag)) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _drag = null; return; }
        var dx = e.GetPosition(_drag).X - _dragStart.X;
        if (!_dragging)
        {
            if (Math.Abs(dx) < 6) return;
            _dragging = true;
            _drag.CaptureMouse(); // кнопка под курсором теряет нажатие — клика не будет
            _drag.Cursor = Cursors.SizeWE;
        }
        _drag.ScrollToHorizontalOffset(Math.Clamp(_dragOffset - dx, 0, _drag.ScrollableWidth));
        e.Handled = true;
    }

    private void Shelf_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is null) return;
        var drag = _drag;
        _drag = null;
        if (!_dragging) return;
        _dragging = false;
        drag.ReleaseMouseCapture();
        drag.ClearValue(CursorProperty);
        e.Handled = true;
    }

    // ---- тачпад: горизонтальный свайп (WM_MOUSEHWHEEL) листает полку под курсором

    private const int WM_MOUSEHWHEEL = 0x020E;
    private System.Windows.Interop.HwndSource? _hwnd;

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hwnd is not null) return;
        _hwnd = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
        _hwnd?.AddHook(WndProc);
    }

    private void Root_Unloaded(object sender, RoutedEventArgs e)
    {
        _hwnd?.RemoveHook(WndProc);
        _hwnd = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEHWHEEL || !IsVisible) return IntPtr.Zero;
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        if (ShelfUnderMouse() is not { } sv) return IntPtr.Zero;
        sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset + delta, 0, sv.ScrollableWidth));
        handled = true;
        return (IntPtr)1;
    }

    private ScrollViewer? ShelfUnderMouse()
    {
        var hit = InputHitTest(Mouse.GetPosition(this)) as DependencyObject;
        while (hit is not null && !ReferenceEquals(hit, this))
        {
            if (hit is ScrollViewer { Name: "ShelfScroll" } sv) return sv;
            hit = hit is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return null;
    }
}
