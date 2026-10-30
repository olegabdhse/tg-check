using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TgCheck.App.Controls;

/// <summary>
/// Круглый аватар: фото или инициалы на цветном фоне, с необязательной рамкой (истории, непрочитанные).
/// Толщина рамки и зазор пропорциональны размеру — на любом размере выглядит аккуратно.
/// </summary>
public sealed class AvatarView : Grid
{
    public static readonly DependencyProperty SourceProperty = Reg(nameof(Source), typeof(ImageSource));
    public static readonly DependencyProperty FillProperty = Reg(nameof(Fill), typeof(Brush));
    public static readonly DependencyProperty InitialsProperty = Reg(nameof(Initials), typeof(string));
    public static readonly DependencyProperty RingProperty = Reg(nameof(Ring), typeof(Brush));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public string? Initials { get => (string?)GetValue(InitialsProperty); set => SetValue(InitialsProperty, value); }
    /// <summary>Кисть рамки; null — только тонкий контур.</summary>
    public Brush? Ring { get => (Brush?)GetValue(RingProperty); set => SetValue(RingProperty, value); }

    private static DependencyProperty Reg(string name, Type type) => DependencyProperty.Register(name, type, typeof(AvatarView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange, (d, _) => ((AvatarView)d).Refresh()));

    private readonly Ellipse _ring = new() { IsHitTestVisible = false };
    private readonly Grid _face = new();
    private readonly Ellipse _bg = new();
    private readonly TextBlock _initials = new()
    {
        Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Ellipse _photo = new();
    // «Избранное» (инициалы «★») — закладка, как в Telegram.
    private readonly System.Windows.Shapes.Path _bookmark = new()
    {
        Data = Geometry.Parse("M2,0 H12 A2,2 0 0 1 14,2 V18 L7,13.5 L0,18 V2 A2,2 0 0 1 2,0 Z"), Fill = Brushes.White, Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
    };
    // Тонкая обводка по краю, когда цветной рамки нет — у любого аватара есть аккуратный контур.
    private readonly Ellipse _edge = new() { IsHitTestVisible = false, StrokeThickness = 1, Stroke = EdgeBrush };
    private static readonly Brush EdgeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public AvatarView()
    {
        _face.Children.Add(_bg);
        _face.Children.Add(_initials);
        _face.Children.Add(_bookmark);
        _face.Children.Add(_photo);
        _face.Children.Add(_edge);
        Children.Add(_ring);
        Children.Add(_face);
        RenderOptions.SetBitmapScalingMode(_photo, BitmapScalingMode.HighQuality);
        SizeChanged += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var size = Math.Min(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        if (double.IsNaN(size) || size <= 0) size = 40;

        var hasRing = Ring is not null;
        var thickness = Math.Round(Math.Max(1.5, size * 0.045) * 2) / 2;
        var gap = Math.Round(Math.Max(1.5, size * 0.04) * 2) / 2;
        _ring.Stroke = Ring;
        _ring.StrokeThickness = thickness;
        _ring.Visibility = hasRing ? Visibility.Visible : Visibility.Collapsed;
        _face.Margin = hasRing ? new Thickness(thickness + gap) : new Thickness(0);
        _edge.Visibility = hasRing ? Visibility.Collapsed : Visibility.Visible;

        _bg.Fill = Fill ?? Brushes.SlateGray;
        var saved = Initials == "★";
        _initials.Text = saved ? null : Initials;
        _bookmark.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        _bookmark.Height = Math.Max(8, size * 0.42);
        _initials.FontSize = Math.Max(8, (size - (hasRing ? 2 * (thickness + gap) : 0)) * 0.38);
        _photo.Fill = Source is null ? null : new ImageBrush(Source) { Stretch = Stretch.UniformToFill };
        _photo.Visibility = Source is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
