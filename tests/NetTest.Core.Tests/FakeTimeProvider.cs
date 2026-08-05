using System.Threading;

namespace NetTest.Core.Tests;

/// <summary>
/// 虚拟时间提供器：时间只随 Advance/SetUtcNow 前进，CreateTimer 基于虚拟时钟调度，
/// 使 Task.Delay(TimeProvider) 等等待可在测试中即时推进，无需真实等待。
/// 仅支持单次 timer（Task.Delay 语义），periodic timer 不做重复调度。
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now;
    private readonly List<FakeTimer> _timers = new();

    public FakeTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    /// <summary>当前已创建的 timer 数量（含已触发的）。测试用于确认调度器已进入等待。</summary>
    public int TimerCount
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count;
            }
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        List<FakeTimer> due;
        lock (_lock)
        {
            _now = value;
            due = TakeDueTimersLocked();
        }

        FireDue(due);
    }

    public void Advance(TimeSpan delta)
    {
        List<FakeTimer> due;
        lock (_lock)
        {
            _now += delta;
            due = TakeDueTimersLocked();
        }

        FireDue(due);
    }

    private List<FakeTimer> TakeDueTimersLocked()
    {
        var due = new List<FakeTimer>();
        foreach (FakeTimer timer in _timers)
        {
            if (timer.DueAtUtc is not null && timer.DueAtUtc <= _now)
            {
                timer.DueAtUtc = null; // 单次触发后失效（Task.Delay 语义）
                due.Add(timer);
            }
        }

        return due;
    }

    private static void FireDue(List<FakeTimer> due)
    {
        // 回调在锁外执行：回调可能再次 Advance/GetUtcNow。
        foreach (FakeTimer timer in due)
        {
            timer.Callback(timer.State);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state, dueTime);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAtUtc;

        public FakeTimer(FakeTimeProvider provider, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
            _dueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? null : provider.GetUtcNow() + dueTime;
        }

        public DateTimeOffset? DueAtUtc
        {
            get => _dueAtUtc;
            set => _dueAtUtc = value;
        }

        public TimerCallback Callback => _callback;

        public object? State => _state;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_provider._lock)
            {
                _dueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? null : _provider.GetUtcNow() + dueTime;
            }

            return true;
        }

        public void Dispose()
        {
            lock (_provider._lock)
            {
                _dueAtUtc = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
