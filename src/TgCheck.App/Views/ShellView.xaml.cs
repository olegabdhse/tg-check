using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TgCheck.App.Controls;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;
using TgCheck.Core.Media;

namespace TgCheck.App.Views;

public partial class ShellView : UserControl
{
    private ShellViewModel? _vm;
    private readonly DispatcherTimer _storyTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public ShellView()
    {
        InitializeComponent();
        Loaded += (_, _) => Current = this;
        Unloaded += (_, _) => { if (Current == this) Current = null; };
        Focusable = true;
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ShellViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            ShowPage(animate: false);
        };
        _storyTimer.Tick += (_, _) =>
        {
            if (_storyVm is { } s && _storyVideo is { Duration: { TotalSeconds: > 0 } d } v)
                s.ReportVideoProgress(v.Position.TotalSeconds / d.TotalSeconds);
        };
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) ShowPage(animate: true);
        if (e.PropertyName == nameof(ShellViewModel.StoryViewer)) AttachStories(_vm?.StoryViewer);
        if (e.PropertyName == nameof(ShellViewModel.IsSettingsOpen)) ShowSettings(_vm?.IsSettingsOpen == true);
    }

    // ===================================================================== Настройки
    // Панель выезжает слева, контент сдвигается на её ширину, список чатов сжимается до аватарок.

    private const double SettingsWidth = 340;

    // Раскладка меняется один раз (сразу), а движение — только сдвигом картинки (без пересчёта раскладки каждый кадр):
    // раньше каждый кадр менялась ширина, список и чат перестраивались и запускали свои анимации — выходило дёргано.
    private readonly System.Windows.Media.TranslateTransform _contentShift = new();

    private void ShowSettings(bool open)
    {
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(open ? 260 : 220);
        ContentArea.RenderTransform = _contentShift;
        SettingsPanel.Visibility = Visibility.Visible;
        SettingsShade.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ContentArea.Margin = new Thickness(open ? SettingsWidth : 0, 0, 0, 0);
        ChatsPage.SetListCollapsed(open);
        // Контент уже на новом месте — визуально он стартует со старого и доезжает вместе с панелью.
        var shift = new System.Windows.Media.Animation.DoubleAnimation(open ? -SettingsWidth : SettingsWidth, 0, time) { EasingFunction = ease };
        _contentShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, shift);
        var slide = new System.Windows.Media.Animation.DoubleAnimation(open ? 0 : -SettingsWidth, time) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            if (_vm?.IsSettingsOpen != true) SettingsPanel.Visibility = Visibility.Collapsed;
        };
        SettingsShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
    }

    private void SettingsShade_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_vm is { IsSettingsOpen: true }) _vm.IsSettingsOpen = false;
    }

    // ===================================================================== Видео историй
    // Каждая видео-история играет в своём, новом плеере: прежний останавливается и выбрасывается целиком.
    // Раньше один плеер переключался между файлами, и остановка старого файла иногда гасила уже запущенный новый —
    // история вставала на превью. Превью уходит, только когда показан первый настоящий кадр видео.

    private static readonly System.Windows.Media.Effects.BlurEffect PreviewBlur = new() { Radius = 40, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
    private StoriesViewModel? _storyVm;
    private VlcVideoView? _storyVideo;

    private void AttachStories(StoriesViewModel? stories)
    {
        if (ReferenceEquals(_storyVm, stories)) return;
        if (_storyVm is not null) _storyVm.PropertyChanged -= OnStoryChanged;
        _storyVm = stories;
        if (stories is not null) stories.PropertyChanged += OnStoryChanged;
        ShowStoryVideo(stories?.VideoPath);
    }

    private void OnStoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _storyVm) || _storyVm is not { } s) return;
        if (e.PropertyName == nameof(StoriesViewModel.IsPaused))
        {
            if (_storyVideo is { } v) { if (s.IsPaused) v.Pause(); else v.Play(); }
            return;
        }
        if (e.PropertyName == nameof(StoriesViewModel.VideoPath)) ShowStoryVideo(s.VideoPath);
        if (e.PropertyName == nameof(StoriesViewModel.VideoStream) && s.VideoStream is { } stream) ShowStoryVideo(null, stream);
    }

    private void ShowStoryVideo(string? path, System.IO.Stream? stream = null)
    {
        _storyTimer.Stop();
        if (_storyVideo is { } old)
        {
            _storyVideo = null;
            StoryVideoHost.Children.Remove(old);
            old.Release();
        }
        StoryPreview.Effect = null;
        if (path is null && stream is null) return;
        // Видео целиком (как в Telegram), без обрезки краёв; по бокам или сверху-снизу — размытое превью той же истории.
        var video = new VlcVideoView { Stretch = System.Windows.Media.Stretch.Uniform, Opacity = 0 };
        video.FirstFrame += (_, _) =>
        {
            if (video != _storyVideo) return;
            video.Opacity = 1;
            StoryPreview.Effect = PreviewBlur;
        };
        video.MediaOpened += (_, _) =>
        {
            if (video != _storyVideo) return;
            AudioPlayer.Instance.Stop();
            MusicNow.Instance.Interrupt(MusicNow.Story); // видео в истории — музыка на паузу до закрытия историй
            if (_storyVm?.IsPaused == true) video.Pause();
            _storyTimer.Start();
        };
        video.MediaEnded += (_, _) =>
        {
            if (video != _storyVideo) return;
            _storyTimer.Stop();
            _storyVm?.Next();
        };
        video.MediaFailed += async (_, error) =>
        {
            if (video != _storyVideo) return;
            _storyTimer.Stop();
            TgCheck.Core.Services.Logging.Write("story video failed: " + error);
            // Видео не открылось — остаётся превью, через несколько секунд следующая история.
            var story = _storyVm?.Story;
            await Task.Delay(4000);
            if (video == _storyVideo && _storyVm is { } s && s.Story == story) s.Next();
        };
        _storyVideo = video;
        StoryVideoHost.Children.Add(video);
        if (stream is not null)
        {
            // Ещё качается — потоком: видео начинается, как только пришли нужные куски (как в Telegram).
            video.PlayStream(stream);
            if (_storyVm?.IsPaused == true) video.Pause();
            return;
        }
        video.SourcePath = path; // сначала файл: смена файла сбрасывает «играть»
        if (_storyVm?.IsPaused != true) video.Play();
    }

    // Чаты и свой профиль созданы один раз и просто переключаются — переход мгновенный.
    // Чужие профили открываются в отдельном контейнере поверх.
    private void ShowPage(bool animate)
    {
        if (_vm is null) return;
        var page = _vm.CurrentPage;
        FrameworkElement target;
        if (ReferenceEquals(page, _vm.Chats)) target = ChatsPage;
        else if (ReferenceEquals(page, _vm.MyProfile)) target = MyProfilePage;
        else if (ReferenceEquals(page, _vm.Music)) target = MusicPage;
        else
        {
            PageHost.Content = page;
            target = PageHost;
        }
        foreach (var el in new FrameworkElement[] { ChatsPage, MyProfilePage, MusicPage, PageHost })
            el.Visibility = el == target ? Visibility.Visible : Visibility.Collapsed;
        if (target != PageHost) PageHost.Content = null;
        if (!animate) return;
        // Чужой профиль — выезжает справа; свой профиль ↔ чаты (обе страницы уже готовы) — короткое мягкое проявление
        // с лёгким подъёмом, сразу, без пустого кадра и «уезда» в сторону.
        if (target == PageHost) Anim.SlideIn(target, 36, 220);
        else Anim.Rise(target);
    }

    // Esc закрывает верхний слой: истории → оверлеи → мини-профиль → чат → чужой профиль.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ShellViewModel shell && shell.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (DataContext is ShellViewModel { StoryViewer: { } stories } && Keyboard.FocusedElement is not TextBox)
        {
            if (e.Key == Key.Right) { stories.Next(); e.Handled = true; }
            else if (e.Key == Key.Left) { stories.PreviousCommand.Execute(null); e.Handled = true; }
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Открытый экран приложения — окну нужно, чтобы передать ему печать вне полей ввода.</summary>
    public static ShellView? Current { get; private set; }

    /// <summary>Печать вне полей ввода: в чатах — в поле сообщения или в поиск (истории и настройки — не трогаем).</summary>
    public bool RouteTyping(string text)
    {
        if (_vm is null || _vm.StoryViewer is not null || _vm.IsSettingsOpen) return false;
        if (!ReferenceEquals(_vm.CurrentPage, _vm.Chats) || !ChatsPage.IsVisible) return false;
        return ChatsPage.RouteTyping(text);
    }

    // Пока пишут ответ на историю — она стоит на паузе (как в Telegram).
    private void StoryReply_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm?.StoryViewer is { } s) s.IsPaused = true;
    }

    private void StoryReply_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm?.StoryViewer is { IsViewersOpen: false } s) s.IsPaused = false;
    }

    private void StoryReply_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm?.StoryViewer is not { } s) return;
        e.Handled = true;
        if (s.SendReplyCommand.CanExecute(null)) s.SendReplyCommand.Execute(null);
        Focus(); // убрать фокус из поля — история продолжится
    }
}
