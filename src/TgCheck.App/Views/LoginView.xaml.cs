using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TgCheck.App.Helpers;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

public partial class LoginView : UserControl
{
    private LoginViewModel? _vm;
    private bool _formatting;
    private bool _syncingPassword;

    public LoginView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Focus(string.IsNullOrEmpty(CodeBox.Text) ? CodeBox : NumberBox);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.FocusNumberRequested -= FocusNumber;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as LoginViewModel;
        if (_vm is not null)
        {
            _vm.FocusNumberRequested += FocusNumber;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void FocusNumber() => Focus(NumberBox);

    private void Focus(TextBox box) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        Keyboard.Focus(box);
        box.CaretIndex = box.Text.Length;
    });

    // Переводит фокус на поле, как только оно появилось (следующий шаг входа).
    private void FocusWhenVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is Control el)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                Keyboard.Focus(el);
                if (el is TextBox tb) tb.CaretIndex = tb.Text.Length;
            });
    }

    // ============================================================ Пароль (PasswordBox не поддерживает binding)

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword || _vm is null) return;
        _vm.Password = ((PasswordBox)sender).Password;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LoginViewModel.Password) || _vm is null) return;
        _syncingPassword = true;
        foreach (var pb in new[] { PasswordBox, QrPasswordBox })
            if (pb.Password != _vm.Password) pb.Password = _vm.Password;
        _syncingPassword = false;
    }

    // ============================================================ Номер телефона

    private void DigitsOnly(object sender, TextCompositionEventArgs e)
    {
        if (!e.Text.All(char.IsAsciiDigit)) e.Handled = true;
    }

    private void CodeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Стрелка вправо в конце кода / пробел — переход к номеру.
        if ((e.Key == Key.Right && CodeBox.CaretIndex == CodeBox.Text.Length) || e.Key == Key.Space)
        {
            Focus(NumberBox);
            e.Handled = true;
        }
    }

    private void NumberBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var tb = NumberBox;
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Back || tb.SelectionLength > 0) return;

        // Пустое поле — Backspace уводит в код страны.
        if (tb.Text.Length == 0 || tb.CaretIndex == 0)
        {
            if (tb.Text.Length == 0) Focus(CodeBox);
            return;
        }

        // Перед курсором разделитель (пробел/дефис) — удаляем предыдущую цифру, а не разделитель.
        var caret = tb.CaretIndex;
        if (char.IsAsciiDigit(tb.Text[caret - 1])) return;
        var i = caret - 1;
        while (i >= 0 && !char.IsAsciiDigit(tb.Text[i])) i--;
        if (i < 0) return;
        e.Handled = true;
        var digitsBefore = tb.Text[..i].Count(char.IsAsciiDigit);
        ApplyFormatted(tb, tb.Text.Remove(i, 1), digitsBefore);
    }

    private void NumberBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_formatting || _vm is null) return;
        var tb = NumberBox;

        // Вставили полный номер с «+» — разбираем на код страны и номер.
        if (tb.Text.Contains('+'))
        {
            _vm.ApplyFullNumber(tb.Text);
            tb.CaretIndex = tb.Text.Length;
            return;
        }

        var digitsBefore = tb.Text[..tb.CaretIndex].Count(char.IsAsciiDigit);
        ApplyFormatted(tb, tb.Text, digitsBefore);
    }

    private void ApplyFormatted(TextBox tb, string raw, int digitsBeforeCaret)
    {
        if (_vm is null) return;
        var formatted = _vm.FormatNational(raw);
        _formatting = true;
        if (tb.Text != formatted) tb.Text = formatted;
        _formatting = false;

        // Ставим курсор после той же по счёту цифры.
        var pos = 0;
        for (var seen = 0; pos < formatted.Length && seen < digitsBeforeCaret; pos++)
            if (char.IsAsciiDigit(formatted[pos])) seen++;
        tb.CaretIndex = pos;
    }

    // ============================================================ Выбор страны

    private void CountryPopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(CountrySearchBox));

    private void CountrySearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var first = _vm?.CountriesView.Cast<Country>().FirstOrDefault();
            if (first is not null) _vm!.SelectCountryCommand.Execute(first);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && CountryList.Items.Count > 0)
        {
            CountryList.SelectedIndex = 0;
            (CountryList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm is not null)
        {
            _vm.IsCountryPickerOpen = false;
            e.Handled = true;
        }
    }

    private void CountryList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Country c) _vm?.SelectCountryCommand.Execute(c);
    }

    private void CountryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CountryList.SelectedItem is Country c)
        {
            _vm?.SelectCountryCommand.Execute(c);
            e.Handled = true;
        }
    }
}
