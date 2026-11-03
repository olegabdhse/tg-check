namespace TgCheck.Core.Services;

/// <summary>
/// Можно ли возвращаться на туннель. Короткую проверку туннель может проходить и тогда, когда данные через него не идут
/// (подключение рвётся, файлы зависают): без паузы программа снова и снова уходила бы с рабочего способа на нерабочий.
/// Каждый провал подряд удваивает паузу: 10 минут, 20, 40… до 2 часов. Время — в миллисекундах (Environment.TickCount64).
/// </summary>
public sealed class TunnelGuard
{
    private const long FirstPauseMs = 10 * 60_000;
    private const long LongestPauseMs = 120 * 60_000;
    /// <summary>Столько туннель должен проработать после возврата, чтобы возврат считался удачным.</summary>
    private const long TrialMs = 10 * 60_000;
    private const long StuckWindowMs = 10 * 60_000;
    private const int StucksToFail = 2;

    private readonly object _lock = new();
    private readonly List<long> _stucks = new();
    private long _blockedUntil, _trialUntil;
    private int _failures;

    public bool CanReturn(long now)
    {
        lock (_lock) return now >= _blockedUntil;
    }

    /// <summary>Идёт возврат на туннель или он ещё не проработал положенного.</summary>
    public bool InTrial(long now)
    {
        lock (_lock) return now < _trialUntil;
    }

    public void Returned(long now)
    {
        lock (_lock)
        {
            _trialUntil = now + TrialMs;
            _stucks.Clear();
        }
    }

    /// <summary>Туннель не справился. Возвращает длину паузы в минутах.</summary>
    public int Failed(long now)
    {
        lock (_lock)
        {
            var pause = Math.Min(LongestPauseMs, FirstPauseMs << Math.Min(_failures, 8));
            _failures++;
            _blockedUntil = now + pause;
            _trialUntil = 0;
            _stucks.Clear();
            return (int)(pause / 60_000);
        }
    }

    /// <summary>Файлы на туннеле зависли и пришлось сбрасывать соединения. True — это уже повтор: туннель не тянет данные.</summary>
    public bool FilesStuck(long now)
    {
        lock (_lock)
        {
            _stucks.RemoveAll(t => now - t > StuckWindowMs);
            _stucks.Add(now);
            return _stucks.Count >= StucksToFail;
        }
    }

    /// <summary>Возврат прошёл испытание: следующий провал снова начнётся с короткой паузы.</summary>
    public void Tick(long now)
    {
        lock (_lock)
        {
            if (_trialUntil == 0 || now < _trialUntil) return;
            _trialUntil = 0;
            _failures = 0;
        }
    }
}
