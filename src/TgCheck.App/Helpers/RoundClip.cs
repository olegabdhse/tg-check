using System.Windows;
using System.Windows.Media;

namespace TgCheck.App.Helpers;

/// <summary>
/// Скругление углов через Clip (дёшево, на видеокарте) вместо OpacityMask с VisualBrush,
/// который рисуется программно и тормозит анимации.
/// </summary>
public static class RoundClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0d, OnChanged));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double v) => d.SetValue(RadiusProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.SizeChanged -= OnSizeChanged;
        el.SizeChanged += OnSizeChanged;
        Apply(el);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement el)
    {
        var r = GetRadius(el);
        var clip = new RectangleGeometry(new Rect(0, 0, el.ActualWidth, el.ActualHeight), r, r);
        clip.Freeze();
        el.Clip = clip;
    }
}
