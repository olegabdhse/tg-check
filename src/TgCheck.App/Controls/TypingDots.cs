using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TgCheck.App.Controls;

/// <summary>Анимированные три точки «печатает…». Анимация идёт только пока точки видны.</summary>
public sealed class TypingDots : StackPanel
{
    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush), typeof(Brush), typeof(TypingDots), new PropertyMetadata(Brushes.DeepSkyBlue, (d, e) =>
        {
            foreach (var dot in ((TypingDots)d).Children.OfType<Ellipse>()) dot.Fill = (Brush)e.NewValue;
        }));

    public Brush DotBrush { get => (Brush)GetValue(DotBrushProperty); set => SetValue(DotBrushProperty, value); }

    public TypingDots()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < 3; i++)
            Children.Add(new Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 3, 0), Fill = DotBrush, RenderTransform = new TranslateTransform() });
        IsVisibleChanged += (_, _) => Update();
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Stop();
    }

    private void Update()
    {
        if (IsVisible && IsLoaded) Start();
        else Stop();
    }

    private void Start()
    {
        var i = 0;
        foreach (var dot in Children.OfType<Ellipse>())
        {
            var begin = TimeSpan.FromMilliseconds(i++ * 160);
            var bounce = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin, Duration = TimeSpan.FromMilliseconds(1100) };
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-3.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new SineEase()));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new SineEase()));
            dot.RenderTransform.BeginAnimation(TranslateTransform.YProperty, bounce);
            var fade = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin, Duration = TimeSpan.FromMilliseconds(1100) };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520))));
            dot.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void Stop()
    {
        foreach (var dot in Children.OfType<Ellipse>())
        {
            dot.RenderTransform.BeginAnimation(TranslateTransform.YProperty, null);
            dot.BeginAnimation(OpacityProperty, null);
        }
    }
}
