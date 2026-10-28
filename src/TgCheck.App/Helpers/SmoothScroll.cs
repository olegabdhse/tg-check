using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TgCheck.App.Helpers;

/// <summary>
/// Плавная прокрутка колёсиком мыши для ScrollViewer: offset догоняет цель с экспоненциальным
/// затуханием, независимо от частоты кадров. Прокрутка ползунком/клавиатурой работает как обычно.
/// </summary>
public static class SmoothScroll
{
    private const double WheelStep = 1.1;      // пикселей на единицу Delta (120 → ~130 px)
    private const double Sharpness = 16;       // чем больше, тем быстрее догоняет

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Animator), typeof(SmoothScroll));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Для ListBox/ItemsControl ищем внутренний ScrollViewer после построения шаблона.
        if (d is ItemsControl ic && e.NewValue is true)
        {
            void Attach(object? s, RoutedEventArgs a)
            {
                ic.Loaded -= Attach;
                if (FindScrollViewer(ic) is { } inner) SetIsEnabled(inner, true);
            }
            if (ic.IsLoaded) Attach(null, null!);
            else ic.Loaded += Attach;
            return;
        }
        if (d is not ScrollViewer sv) return;
        if (e.NewValue is true)
        {
            var animator = new Animator(sv);
            sv.SetValue(StateProperty, animator);
            sv.PreviewMouseWheel += animator.OnWheel;
            sv.ScrollChanged += animator.OnScrollChanged;
            sv.Unloaded += animator.OnUnloaded;
        }
        else if (sv.GetValue(StateProperty) is Animator animator)
        {
            sv.PreviewMouseWheel -= animator.OnWheel;
            sv.ScrollChanged -= animator.OnScrollChanged;
            sv.Unloaded -= animator.OnUnloaded;
            animator.Stop();
            sv.ClearValue(StateProperty);
        }
    }

    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>
    /// Сдвинуть позицию на delta (контент добавился сверху) — и текущую, и цель плавной прокрутки,
    /// чтобы анимация не утянула ленту обратно к началу.
    /// </summary>
    public static void ShiftBy(ScrollViewer sv, double delta)
    {
        if (sv.GetValue(StateProperty) is Animator a) a.Shift(delta);
        sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
    }

    /// <summary>Плавно прокрутить к offset (например, «вниз к новым сообщениям»).</summary>
    public static void ScrollTo(ScrollViewer sv, double offset)
    {
        if (sv.GetValue(StateProperty) is Animator a) a.AnimateTo(offset);
        else sv.ScrollToVerticalOffset(offset);
    }

    private sealed class Animator(ScrollViewer sv)
    {
        private double _target;
        private bool _running;
        private TimeSpan _last;

        public void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (sv.ScrollableHeight <= 0 || Keyboard.Modifiers != ModifierKeys.None) return;

            var from = _running ? _target : sv.VerticalOffset;
            var to = Math.Clamp(from - e.Delta * WheelStep, 0, sv.ScrollableHeight);

            // Упёрлись в край — отдаём событие родителю (вложенные списки).
            if (Math.Abs(to - sv.VerticalOffset) < 0.5 && !_running) return;

            e.Handled = true;
            _target = to;
            Start();
        }

        public void Shift(double delta) => _target += delta;

        public void AnimateTo(double offset)
        {
            _target = Math.Clamp(offset, 0, sv.ScrollableHeight);
            Start();
        }

        public void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!_running) _target = sv.VerticalOffset;
            else if (_target > sv.ScrollableHeight) _target = sv.ScrollableHeight;
        }

        public void OnUnloaded(object sender, RoutedEventArgs e) => Stop();

        private void Start()
        {
            if (_running) return;
            _running = true;
            _last = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
            if (now == _last) return; // WPF может вызвать Rendering несколько раз за кадр
            var dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _last).TotalSeconds, 0.05);
            _last = now;

            var current = sv.VerticalOffset;
            var diff = _target - current;
            if (Math.Abs(diff) < 0.5)
            {
                sv.ScrollToVerticalOffset(_target);
                Stop();
                return;
            }
            sv.ScrollToVerticalOffset(current + diff * (1 - Math.Exp(-Sharpness * dt)));
        }
    }
}
