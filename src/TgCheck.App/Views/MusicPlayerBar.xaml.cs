using System.Windows.Controls;
using System.Windows.Input;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

public partial class MusicPlayerBar : UserControl
{
    public MusicPlayerBar() => InitializeComponent();

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
