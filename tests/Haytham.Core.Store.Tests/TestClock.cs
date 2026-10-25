using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Haytham.Core.Store.Tests;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports every timer as it starts, so a test can see what the code waits for
/// and move the clock past exactly that. No timer fires until the test moves the clock.
/// </summary>
sealed class TestClock : FakeTimeProvider
{
    /// <summary>Only guards against a test that expects a wait the code never starts; nothing waits this long.</summary>
    static readonly TimeSpan NoTimerGuard = TimeSpan.FromSeconds(10);

    readonly Channel<TimeSpan> _started = Channel.CreateUnbounded<TimeSpan>();
    readonly List<TimeSpan> _timers = [];

    /// <summary>Due times of every timer started so far, in order.</summary>
    public IReadOnlyList<TimeSpan> Timers
    {
        get
        {
            lock (_timers) return [.. _timers];
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_timers) _timers.Add(dueTime);
            _started.Writer.TryWrite(dueTime);
        }

        return timer;
    }

    /// <summary>Waits for the next timer to start and returns its due time; timers are handed out in order.</summary>
    public async Task<TimeSpan> NextTimerAsync()
    {
        try
        {
            return await _started.Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(NoTimerGuard, TimeProvider.System);
        }
        catch (TimeoutException e)
        {
            throw new InvalidOperationException("The code under test started no further timer.", e);
        }
    }

    /// <summary>Waits for the next timer, checks its due time, and moves the clock past it.</summary>
    public async Task AdvancePastNextTimerAsync(TimeSpan expected)
    {
        Assert.Equal(expected, await NextTimerAsync());
        Advance(expected);
    }
}
