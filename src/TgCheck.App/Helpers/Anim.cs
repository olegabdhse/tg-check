using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TgCheck.App.Helpers;

/// <summary>Небольшие анимации интерфейса через attached-свойства.</summary>
public static class Anim
{
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Плавное появление (прозрачность + сдвиг вверх), когда элемент становится видимым.</summary>
    public static readonly DependencyProperty FadeOnVisibleProperty = DependencyProperty.RegisterAttached(
        "FadeOnVisible", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnFadeOnVisibleChanged));

    public static bool GetFadeOnVisible(DependencyObject d) => (bool)d.GetValue(FadeOnVisibleProperty);
    public static void SetFadeOnVisible(DependencyObject d, bool v) => d.SetValue(FadeOnVisibleProperty, v);

    /// <summary>Плавное появление при смене DataContext (переключение страниц/чатов).</summary>
    public static readonly DependencyProperty FadeOnDataContextProperty = DependencyProperty.RegisterAttached(
        "FadeOnDataContext", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnFadeOnDataContextChanged));

    public static bool GetFadeOnDataContext(DependencyObject d) => (bool)d.GetValue(FadeOnDataContextProperty);
    public static void SetFadeOnDataContext(DependencyObject d, bool v) => d.SetValue(FadeOnDataContextProperty, v);

    private static void OnFadeOnVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.IsVisibleChanged -= OnVisibleChanged;
        if (e.NewValue is true) fe.IsVisibleChanged += OnVisibleChanged;
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) FadeIn((FrameworkElement)sender, 160, 6);
    }

    private static void OnFadeOnDataContextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.DataContextChanged -= OnDataContextChanged;
        if (e.NewValue is true) fe.DataContextChanged += OnDataContextChanged;
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not null) FadeIn((FrameworkElement)sender, 200, 10);
    }

    // ===================================================================== Show: анимированное появление и скрытие

    public enum ShowMode { Fade, Scale, SlideRight, SlideUp, SlideDown }

    /// <summary>
    /// Управляет видимостью с анимацией: true — плавно показать, false — плавно скрыть и Collapsed.
    /// Используется вместо Visibility для диалогов, панелей и оверлеев.
    /// </summary>
    /// <summary>До этого момента Show переключается мгновенно (окно только что открылось).</summary>
    public static DateTime InstantUntil { get; set; }

    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnShowChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(ShowMode), typeof(Anim), new PropertyMetadata(ShowMode.Fade));

    public static bool GetShow(DependencyObject d) => (bool)d.GetValue(ShowProperty);
    public static void SetShow(DependencyObject d, bool v) => d.SetValue(ShowProperty, v);
    public static ShowMode GetMode(DependencyObject d) => (ShowMode)d.GetValue(ModeProperty);
    public static void SetMode(DependencyObject d, ShowMode v) => d.SetValue(ModeProperty, v);

    private static void OnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var show = e.NewValue is true;
        // Первые мгновения после открытия окна (данные из кэша) и ещё не показанные элементы — без анимации:
        // интерфейс появляется сразу готовым, а не «выезжает» частями.
        if (!el.IsLoaded || DateTime.UtcNow < InstantUntil)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            if (el.RenderTransform is TransformGroup { IsFrozen: false, Children.Count: 2 } g)
            {
                g.Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, null);
                g.Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ((ScaleTransform)g.Children[0]).ScaleX = ((ScaleTransform)g.Children[0]).ScaleY = 1;
                g.Children[1].BeginAnimation(TranslateTransform.XProperty, null);
                g.Children[1].BeginAnimation(TranslateTransform.YProperty, null);
                ((TranslateTransform)g.Children[1]).X = ((TranslateTransform)g.Children[1]).Y = 0;
            }
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var mode = GetMode(el);
        var ms = show ? 220 : 160;
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

        var group = el.RenderTransform as TransformGroup;
        if (group is null || group.IsFrozen || group.Children.Count != 2)
        {
            group = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
            el.RenderTransform = group;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        var scale = (ScaleTransform)group.Children[0];
        var move = (TranslateTransform)group.Children[1];

        if (show) el.Visibility = Visibility.Visible;
        else if (el.Visibility != Visibility.Visible) return;

        var fade = new DoubleAnimation(show ? 0 : el.Opacity, show ? 1 : 0, duration) { EasingFunction = ease };
        CacheWhile(el, fade);
        if (!show)
            fade.Completed += (_, _) => { if (!GetShow(el)) el.Visibility = Visibility.Collapsed; };
        el.BeginAnimation(UIElement.OpacityProperty, fade);

        switch (mode)
        {
            case ShowMode.Scale:
                var s = new DoubleAnimation(show ? 0.94 : 1, show ? 1 : 0.96, duration) { EasingFunction = ease };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
                break;
            case ShowMode.SlideRight:
                var w = el.ActualWidth > 0 ? el.ActualWidth : 340;
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(show ? w * 0.6 : 0, show ? 0 : w * 0.6, duration) { EasingFunction = ease });
                break;
            case ShowMode.SlideUp:
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? 14 : 0, show ? 0 : 14, duration) { EasingFunction = ease });
                break;
            case ShowMode.SlideDown:
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? -14 : 0, show ? 0 : -14, duration) { EasingFunction = ease });
                break;
        }
    }

    /// <summary>Плавный переход контента: старое уезжает/гаснет, новое появляется со сдвигом.</summary>
    /// <summary>Мягкое появление уже построенной страницы: проявление и подъём на несколько пикселей, сразу.</summary>
    public static void Rise(FrameworkElement el, double fromY = 10, int ms = 170)
    {
        if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
            el.RenderTransform = tt = new TranslateTransform();
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0.35, 1, duration) { EasingFunction = ease };
        CacheWhile(el, fade);
        el.BeginAnimation(UIElement.OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, duration) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.XProperty, null);
        tt.X = 0;
    }

    public static void SlideIn(FrameworkElement el, double fromX, int ms = 240)
    {
        if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
            el.RenderTransform = tt = new TranslateTransform();
        el.Opacity = 0;
        // Стартуем после раскладки нового содержимого — иначе первые кадры «съедает» построение интерфейса.
        el.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(ms));
            var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = Ease };
            CacheWhile(el, fade);
            el.BeginAnimation(UIElement.OpacityProperty, fade);
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, duration) { EasingFunction = Ease });
        });
    }

    /// <summary>
    /// На время анимации элемент рисуется один раз в текстуру видеокарты и дальше только двигается/проявляется —
    /// без перерисовки содержимого каждый кадр. После анимации кэш снимается (чтобы текст оставался чётким).
    /// </summary>
    private static void CacheWhile(UIElement el, AnimationTimeline animation)
    {
        var dpi = VisualTreeHelper.GetDpi(el);
        el.CacheMode = new BitmapCache(dpi.DpiScaleX) { SnapsToDevicePixels = true };
        animation.Completed += (_, _) => el.CacheMode = null;
    }

    public static void FadeIn(FrameworkElement el, int ms = 180, double offsetY = 8)
    {
        if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
            el.RenderTransform = tt = new TranslateTransform();
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));
        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = Ease };
        CacheWhile(el, fadeIn);
        el.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, duration) { EasingFunction = Ease });
    }

    /// <summary>Выполнить действие на UI-потоке (события Telegram приходят из фоновых потоков).</summary>
    public static void Ui(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action, DispatcherPriority.Normal);
    }
}

/// <summary>Цвета аватарок-заглушек как в Telegram (по ID).</summary>
public static class AvatarColors
{
    private static readonly (string From, string To)[] Pairs =
    [
        ("#FF885E", "#FF516A"), ("#FFCD6A", "#FFA85C"), ("#82B1FF", "#665FFF"), ("#A0DE7E", "#54CB68"),
        ("#53EDD6", "#28C9B7"), ("#72D5FD", "#2A9EF1"), ("#E0A2F3", "#D669ED"),
    ];

    private static readonly Brush[] Brushes = Pairs.Select(p =>
    {
        var b = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(p.From), (Color)ColorConverter.ConvertFromString(p.To), 90);
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    private static readonly Brush[] Names = Pairs.Select(p =>
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.To));
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    public static Brush StoryUnread { get; } = Frozen(new LinearGradientBrush(Color.FromRgb(0x2A, 0xAB, 0xEE), Color.FromRgb(0x4F, 0xCE, 0x5D), 45));
    public static Brush StorySeen { get; } = Frozen(new SolidColorBrush(Color.FromRgb(0x4D, 0x5B, 0x6A)));

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    public static Brush For(long id) => Brushes[(int)(Math.Abs(id) % Brushes.Length)];
    public static Brush NameFor(long id) => Names[(int)(Math.Abs(id) % Names.Length)];

    public static string Initials(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.FirstOrDefault(char.IsLetterOrDigit)).Where(c => c != default).Take(2).ToArray();
        return words.Length == 0 ? "?" : new string(words).ToUpperInvariant();
    }
}
