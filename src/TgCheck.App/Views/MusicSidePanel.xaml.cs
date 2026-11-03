using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

/// <summary>
/// Боковой плеер: песня, поиск и подборки рядом с чатами. Скрытый — узкая полоса: наведение на её пустое место (волны) раскрывает
/// панель, чат при этом сжимается; через секунду после ухода мыши всё возвращается, что бы в панели ни делали.
/// Открытой панель остаётся, только если закрепить её стрелкой.
/// </summary>
public partial class MusicSidePanel : UserControl
{
    // Уже этого чату тесно: панель сама сворачивается в полосу, а когда окно снова шире — возвращается как было.
    private const double NarrowWidth = 1100;
    private const double OpenWidth = 360, StripWidth = 64, RightGap = 18, TopGap = 12, BottomGap = 18;
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(1);

    private static readonly DependencyProperty OpenAmountProperty = DependencyProperty.Register("OpenAmount", typeof(double), typeof(MusicSidePanel),
        new PropertyMetadata(1.0, (d, _) => ((MusicSidePanel)d).ApplyCardSize()));

    private readonly DispatcherTimer _closeTimer = new() { Interval = CloseDelay };
    private MusicViewModel? _music;
    private Window? _window;
    private bool _hoverOpen;
    private bool _strip;

    public MusicSidePanel()
    {
        InitializeComponent();
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (_hoverOpen && !Card.IsMouseOver) SetHoverOpen(false);
        };
        MusicNow.Instance.PropertyChanged += OnNowChanged;
        DataContextChanged += (_, _) => Attach(DataContext as MusicViewModel);
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.SizeChanged += OnWindowSizeChanged;
            ApplyWidth();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.SizeChanged -= OnWindowSizeChanged;
        };
        ApplyMode();
    }

    private double OpenAmount
    {
        get => (double)GetValue(OpenAmountProperty);
        set => SetValue(OpenAmountProperty, value);
    }

    private void OnNowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicNow.IsSidePanelStrip)) ApplyMode();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => ApplyWidth();

    private void ApplyWidth()
    {
        if (_window is { ActualWidth: > 0 } window) MusicNow.Instance.IsWindowNarrow = window.ActualWidth < NarrowWidth;
    }

    private void ApplyMode()
    {
        _strip = MusicNow.Instance.IsSidePanelStrip;
        _closeTimer.Stop();
        _hoverOpen = false;
        BeginAnimation(OpenAmountProperty, null);
        OpenAmount = _strip ? 0 : 1;
        FullPanel.Visibility = _strip ? Visibility.Collapsed : Visibility.Visible;
        StripPanel.Visibility = _strip ? Visibility.Visible : Visibility.Collapsed;
        ApplyCardSize();
    }

    // Ширина колонки меняется вместе с панелью: чат сжимается и расширяется следом за ней.
    private void ApplyCardSize()
    {
        var width = StripWidth + (OpenWidth - StripWidth) * OpenAmount;
        Card.Width = width;
        Card.Margin = new Thickness(0, TopGap, RightGap, BottomGap);
        Width = width + RightGap;
    }

    private void SetHoverOpen(bool open)
    {
        if (_hoverOpen == open) return;
        _hoverOpen = open;
        if (open) FullPanel.Visibility = Visibility.Visible;
        if (open) StripPanel.Visibility = Visibility.Collapsed;
        var animation = new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 220 : 180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (!open)
            animation.Completed += (_, _) =>
            {
                if (_hoverOpen || !_strip) return;
                FullPanel.Visibility = Visibility.Collapsed;
                StripPanel.Visibility = Visibility.Visible;
            };
        BeginAnimation(OpenAmountProperty, animation);
    }

    // Раскрывает только высота волн. Проверка по положению, а не по элементу под мышью: заходя на полосу сбоку, мышь сначала
    // попадает на рамку и отступы, а это не кнопки, — и панель раскрывалась даже по пути к кнопке.
    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_strip || _hoverOpen) return;
        var y = e.GetPosition(Waves).Y;
        if (y >= 0 && y <= Waves.ActualHeight) SetHoverOpen(true);
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e) => _closeTimer.Stop();

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverOpen) _closeTimer.Start();
    }

    // Обложки песен страница подгружает по мере прокрутки, а здесь песен немного: просим сразу те, что видны в сетке.
    private const int MaxCovers = 40;

    private void Attach(MusicViewModel? music)
    {
        if (ReferenceEquals(_music, music)) return;
        foreach (var shelf in Shelves(_music)) ((INotifyCollectionChanged)shelf.Items).CollectionChanged -= OnShelfChanged;
        _music = music;
        foreach (var shelf in Shelves(music))
        {
            ((INotifyCollectionChanged)shelf.Items).CollectionChanged += OnShelfChanged;
            foreach (var track in shelf.Items.Take(MaxCovers)) track.RequestCover();
        }
    }

    private static IEnumerable<MusicShelf> Shelves(MusicViewModel? music) =>
        music is null ? [] : music.SearchShelves.Concat(music.Shelves).Append(music.NextForYou).Append(music.PlaylistPage);

    private static void OnShelfChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewStartingIndex is < 0 or >= MaxCovers) return;
        foreach (var track in e.NewItems?.OfType<MusicTrackViewModel>() ?? []) track.RequestCover();
    }

    // Размытая обложка шире карточки: углы скругляет обрезка, а не рамка.
    private void PlayerCard_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PlayerCard.Clip = new RectangleGeometry(new Rect(e.NewSize), 16, 16);

    // Края полосы и панели скругляет обрезка: обложка-фон иначе вылезала бы за углы.
    private void Card_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Card.Clip = new RectangleGeometry(new Rect(e.NewSize), 18, 18);

    // Размытый фон нужен только плитке шире обложки; в квадратной он не виден, и эффект зря не считается.
    private void TileBackdrop_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border backdrop) return;
        backdrop.Visibility = e.NewSize.Width > e.NewSize.Height + 1 ? Visibility.Visible : Visibility.Hidden;
        backdrop.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);
    }

    // Полка чатов листается колесом в сторону.
    private void ChatShelf_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer shelf) return;
        shelf.ScrollToHorizontalOffset(shelf.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => MusicNow.Instance.CloseMusic();

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
}
