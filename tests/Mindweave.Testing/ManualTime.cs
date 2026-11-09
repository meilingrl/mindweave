using Mindweave.Application;

namespace Mindweave.Testing;

/// <summary>
/// A test clock, as both <see cref="TimeProvider"/> and <see cref="IClock"/>. Two kinds:
/// <list type="bullet">
/// <item><c>new ManualTime(start)</c> stands still until the test moves it (<see cref="Now"/>, <see cref="Advance"/>); its
/// timers fire only then, on the test's thread.</item>
/// <item><see cref="Running"/> runs in real time from its start, plus whatever the test adds with <see cref="Advance"/>,
/// which also moves the timestamps elapsed times are measured with; its timers are real ones.</item>
/// </list>
/// </summary>
public sealed class ManualTime : TimeProvider, IClock
{
    /// <summary>Where a standing clock starts unless told otherwise.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(8));

    private readonly List<Timer> _timers = [];
    private readonly bool _running;
    private readonly long _origin;
    private DateTimeOffset _now;
    private long _ahead;

    /// <summary>A clock standing at <paramref name="start"/> (<see cref="DefaultStart"/> when null) until the test moves it.</summary>
    public ManualTime(DateTimeOffset? start = null)
    {
        _now = start ?? DefaultStart;
    }

    private ManualTime(DateTimeOffset start, bool running)
    {
        _now = start;
        _running = running;
        _origin = System.GetTimestamp();
    }

    /// <summary>A clock that runs in real time from <paramref name="start"/> (the real time now when null).</summary>
    public static ManualTime Running(DateTimeOffset? start = null) => new(start ?? DateTimeOffset.UtcNow, running: true);

    /// <summary>Timers created on a standing clock and not yet disposed.</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>The time now; set it to move a standing clock (its due timers fire as with <see cref="Advance"/>).</summary>
    public DateTimeOffset Now
    {
        get => GetUtcNow();
        set => Advance(value - GetUtcNow());
    }

    public DateTimeOffset UtcNow => GetUtcNow();

    public override DateTimeOffset GetUtcNow()
    {
        if (_running)
        {
            return _now + System.GetElapsedTime(_origin) + TimeSpan.FromTicks(Interlocked.Read(ref _ahead));
        }

        lock (_timers)
        {
            return _now;
        }
    }

    public override long GetTimestamp() =>
        _running
            ? System.GetTimestamp() + (long)(TimeSpan.FromTicks(Interlocked.Read(ref _ahead)).TotalSeconds * TimestampFrequency)
            : base.GetTimestamp();

    /// <summary>Moves the clock forward by <paramref name="by"/>; on a standing clock, due timers then fire on this thread.</summary>
    public void Advance(TimeSpan by)
    {
        if (_running)
        {
            Interlocked.Add(ref _ahead, by.Ticks);
            return;
        }

        Timer[] due;
        lock (_timers)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due <= _now)];
            foreach (var timer in due)
            {
                timer.Due = _now + timer.Period;
            }
        }

        foreach (var timer in due)
        {
            timer.Callback(timer.State);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (_running)
        {
            return base.CreateTimer(callback, state, dueTime, period);
        }

        var timer = new Timer(this, callback, state, period);
        lock (_timers)
        {
            timer.Due = _now + dueTime;
            _timers.Add(timer);
        }

        return timer;
    }

    private sealed class Timer(ManualTime owner, TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public TimeSpan Period { get; private set; } = period;

        public DateTimeOffset Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._timers)
            {
                Due = owner._now + dueTime;
                Period = period;
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._timers)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
