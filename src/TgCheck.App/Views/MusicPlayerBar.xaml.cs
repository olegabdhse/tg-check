using System.Windows.Controls;
using System.Windows.Input;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

public partial class MusicPlayerBar : UserControl
{
    public MusicPlayerBar() => InitializeComponent();

    /// <summary>Крестик только сворачивает плеер (в островке): музыка играет дальше. Иначе — закрыть плеер.</summary>
    public bool HideOnly
    {
        get => _hideOnly;
        set
        {
            _hideOnly = value;
            // В островке обложка с волнами уже стоит слева от плеера — здесь её не повторяем.
            Waves.Visibility = value ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        }
    }

    private bool _hideOnly;

    /// <summary>Нажат крестик при HideOnly.</summary>
    public event Action? HideRequested;

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (HideOnly) HideRequested?.Invoke();
        else NowPlaying.Instance.CloseMusic();
    }

    private void SeekTo(MouseEventArgs e) =>
        NowPlaying.Instance.Seek(Math.Clamp(e.GetPosition(Seek).X / Math.Max(1, Seek.ActualWidth), 0, 1));

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
