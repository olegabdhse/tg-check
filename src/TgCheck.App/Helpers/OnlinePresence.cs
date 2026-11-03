using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TgCheck.Core.Services;

namespace TgCheck.App.Helpers;

/// <summary>
/// Свой статус «в сети»: только пока главное окно открыто и с ним работают. Две минуты без действий, окно свёрнуто или
/// убрано в трей — «был(а) недавно». В режиме призрака «в сети» не отправляется вообще.
/// </summary>
public sealed class OnlinePresence : IDisposable
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2);
    // «В сети» на сервере живёт ограниченное время, а другое устройство (телефон ушёл в фон) может его сбросить —
    // пока пользователь активен, подтверждаем каждые 30 секунд.
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);

    private readonly TelegramService _tg;
    private readonly Action<bool> _changed;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastInput = DateTime.UtcNow;
    private DateTime _lastSent;
    private bool? _online;
    private bool _wasWindowActive;

    /// <param name="changed">Свой статус изменился: сервер об этом не сообщает, а строка в профиле должна его показывать.</param>
    public OnlinePresence(TelegramService tg, Action<bool> changed)
    {
        _tg = tg;
        _changed = changed;
        InputManager.Current.PreProcessInput += OnInput;
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    // Действие в главном окне или в окне, открытом из него (просмотр фото): мышь, колесо, клавиатура.
    // Островок и мини-окна сюда не относятся: статус «в сети» они не дают.
    private void OnInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not (MouseEventArgs or KeyEventArgs or TextCompositionEventArgs)) return;
        var target = e.StagingItem.Input is MouseEventArgs ? Mouse.DirectlyOver as DependencyObject : Keyboard.FocusedElement as DependencyObject;
        if (target is not null && Window.GetWindow(target) is { } window && window != Application.Current?.MainWindow && window.Owner != Application.Current?.MainWindow) return;
        _lastInput = DateTime.UtcNow;
    }

    private void Check()
    {
        var main = Application.Current?.MainWindow;
        var windowOpen = main is { IsVisible: true } && main.WindowState != WindowState.Minimized;
        var windowActive = windowOpen && main!.IsActive;
        // Вернулись в окно (панель задач, Alt+Tab, развернули) — это тоже действие: «в сети» сразу, не дожидаясь движения мыши.
        if (windowActive && !_wasWindowActive) _lastInput = DateTime.UtcNow;
        _wasWindowActive = windowActive;
        var active = !GhostMode.Enabled && windowOpen && DateTime.UtcNow - _lastInput < IdleAfter;
        if (active == _online && (!active || DateTime.UtcNow - _lastSent < Refresh)) return;
        if (active != _online)
        {
            Logging.Write($"presence: {(active ? "в сети" : "не в сети")} (окно открыто: {windowOpen}, призрак: {GhostMode.Enabled})");
            _changed(active);
        }
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
