namespace TgCheck.Core.Media;

/// <summary>
/// Очередь загрузок «последний запрос — первым»: то, что сейчас на экране (запрошено только что), качается раньше
/// давно заказанного и уже пролистанного. Обычная очередь отдавала места по порядку, и открытые стикеры и фото ждали чужие.
/// </summary>
internal sealed class LifoGate(int slots)
{
    private readonly object _lock = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    private int _free = slots;

    public Task WaitAsync()
    {
        lock (_lock)
        {
            if (_free > 0)
            {
                _free--;
                return Task.CompletedTask;
            }
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.AddFirst(waiter);
            return waiter.Task;
        }
    }

    public void Release()
    {
        TaskCompletionSource next;
        lock (_lock)
        {
            if (_waiters.First is null)
            {
                _free++;
                return;
            }
            next = _waiters.First.Value;
            _waiters.RemoveFirst();
        }
        next.SetResult();
    }
}
