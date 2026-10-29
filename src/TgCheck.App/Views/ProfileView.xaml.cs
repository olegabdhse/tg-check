using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

public partial class ProfileView : UserControl
{
    public ProfileView() => InitializeComponent();

    // Сетка подарков — порциями: подходим к концу — добавляем следующую.
    private void Gifts_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer sv && DataContext is ViewModels.ProfileViewModel vm
            && sv.VerticalOffset + sv.ViewportHeight > sv.ExtentHeight - 900)
            vm.ShowMoreGifts();
    }

    /// <summary>Visible, только если все значения true.</summary>
    public static IMultiValueConverter AllTrueToVisibility { get; } = new AllTrueConverter();

    /// <summary>Умножает число на ConverterParameter.</summary>
    public static IValueConverter Scale { get; } = new ScaleConverter();

    public static IValueConverter Not { get; } = new NotConverter();

    // Адаптив: в узком окне колонка с информацией уже.
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        InfoColumn.Width = new GridLength(ActualWidth < 1000 ? 300 : 370);

    private sealed class AllTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double d && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var k)
                ? Math.Max(0, d * k)
                : double.PositiveInfinity;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }
}
