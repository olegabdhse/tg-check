using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
            if (DataContext is ShellViewModel { StoryViewer: { } s } && StoryVideo.NaturalDuration.HasTimeSpan)
                s.ReportVideoProgress(StoryVideo.Position.TotalSeconds / StoryVideo.NaturalDuration.TimeSpan.TotalSeconds);
        };
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) ShowPage(animate: true);
        if (e.PropertyName == nameof(ShellViewModel.StoryViewer) && _vm?.StoryViewer is { } stories)
            stories.PropertyChanged += OnStoryChanged;
    }

    // Видео истории: MediaElement в ручном режиме открывает файл только после Play().
    private void OnStoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StoriesViewModel.IsPaused) && sender is StoriesViewModel { VideoPath: not null } paused)
        {
            if (paused.IsPaused) StoryVideo.Pause(); else StoryVideo.Play();
            return;
        }
        if (e.PropertyName != nameof(StoriesViewModel.VideoPath) || sender is not StoriesViewModel s) return;
        if (s.VideoPath is null) { StoryVideo.Stop(); _storyTimer.Stop(); return; }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { StoryVideo.Play(); _storyTimer.Start(); });
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
        else
        {
            PageHost.Content = page;
            target = PageHost;
        }
        foreach (var el in new FrameworkElement[] { ChatsPage, MyProfilePage, PageHost })
            el.Visibility = el == target ? Visibility.Visible : Visibility.Collapsed;
        if (target != PageHost) PageHost.Content = null;
        if (animate) Anim.SlideIn(target, target == PageHost ? 36 : -20, 220);
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

    private void StoryVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        AudioPlayer.Instance.Stop();
        StoryVideo.Play();
        _storyTimer.Start();
    }

    private void StoryVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        _storyTimer.Stop();
        StoryVideo.Stop();
        if (DataContext is ShellViewModel { StoryViewer: { } s }) s.Next();
    }
}
