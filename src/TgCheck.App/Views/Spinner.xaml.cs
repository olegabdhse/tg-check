using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TgCheck.App.Views;

/// <summary>Индикатор загрузки. Крутится только пока виден — скрытые спиннеры не нагружают отрисовку.</summary>
public partial class Spinner : UserControl
{
    private static readonly DoubleAnimation Spin = new(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };

    public Spinner()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Update();
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Rot.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void Update() =>
        Rot.BeginAnimation(RotateTransform.AngleProperty, IsVisible && IsLoaded ? Spin : null);
}
