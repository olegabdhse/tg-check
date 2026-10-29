using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TgCheck.App.Controls;

/// <summary>
/// Число в кружке-счётчике, выровненное по фактическому контуру цифр, а не по строке шрифта:
/// у обычного TextBlock цифры смещены из-за места под выносные элементы, и в маленьком кружке это видно.
/// </summary>
public sealed class BadgeText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(BadgeText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(BadgeText),
        new FrameworkPropertyMetadata(11d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(BadgeText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private Geometry? _glyphs;

    private Geometry Build()
    {
        var text = new FormattedText(Text ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return text.BuildGeometry(new Point(0, 0));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _glyphs = Build();
        var b = _glyphs.Bounds;
        return b.IsEmpty ? new Size(0, FontSize) : new Size(Math.Ceiling(b.Width), FontSize);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_glyphs is null || _glyphs.Bounds.IsEmpty) return;
        var b = _glyphs.Bounds;
        // Центр контура цифр — в центр элемента (с округлением до пикселя, чтобы не было размытия).
        var dx = Math.Round((ActualWidth - b.Width) / 2 - b.X);
        var dy = Math.Round((ActualHeight - b.Height) / 2 - b.Y);
        dc.PushTransform(new TranslateTransform(dx, dy));
        dc.DrawGeometry(Foreground, null, _glyphs);
        dc.Pop();
    }
}
