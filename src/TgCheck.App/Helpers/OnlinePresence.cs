using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TgCheck.Core.Services;

namespace TgCheck.App.Helpers;

/// <summary>
/// Свой статус «в сети», как в официальном клиенте: пока окно приложения активно и с ним что-то делают — «в сети»;
/// 10 секунд без действий, окно свёрнуто или ушло в фон — «был(а) недавно».
/// </summary>
public sealed class OnlinePresence : IDisposable
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(10);
    // «В сети» на сервере живёт ограниченное время — пока пользователь активен, подтверждаем раз в минуту.
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(60);

    private readonly TelegramService _tg;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastInput = DateTime.UtcNow;
    private DateTime _lastSent;
    private bool? _online;
    private bool _wasWindowActive;

    public OnlinePresence(TelegramService tg)
    {
        _tg = tg;
        InputManager.Current.PreProcessInput += OnInput;
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    // Любое действие в окнах приложения: мышь, колесо, клавиатура.
    private void OnInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not (MouseEventArgs or KeyEventArgs or TextCompositionEventArgs)) return;
        // Островок — не «работа в приложении»: клики и наведение на него статус «в сети» не дают.
        var target = e.StagingItem.Input is MouseEventArgs ? Mouse.DirectlyOver as DependencyObject : Keyboard.FocusedElement as DependencyObject;
        if (target is not null && Window.GetWindow(target) is Views.IslandWindow) return;
        _lastInput = DateTime.UtcNow;
    }

    private void Check()
    {
        var windowActive = Application.Current?.Windows.OfType<Window>().Any(w => w.IsActive && w is not Views.IslandWindow && w.WindowState != WindowState.Minimized) == true;
        // Вернулись в окно (панель задач, Alt+Tab, развернули) — это тоже действие: «в сети» сразу, не дожидаясь движения мыши.
        if (windowActive && !_wasWindowActive) _lastInput = DateTime.UtcNow;
        _wasWindowActive = windowActive;
        var active = windowActive && DateTime.UtcNow - _lastInput < IdleAfter;
        if (active == _online && (!active || DateTime.UtcNow - _lastSent < Refresh)) return;
        _online = active;
        _lastSent = DateTime.UtcNow;
        _ = _tg.SetOnlineAsync(active);
    }

    public void Dispose()
    {
        _timer.Stop();
        InputManager.Current.PreProcessInput -= OnInput;
        if (_online == true) _ = _tg.SetOnlineAsync(false);
    }
}
