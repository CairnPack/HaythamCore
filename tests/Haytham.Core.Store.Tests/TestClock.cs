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

    /// <summary>
    /// Only guards against an operation that waits for a timer the test never moves the clock past; once the test has
    /// moved the clock as far as it expects, the rest is disk and CPU work that finishes far sooner than this.
    /// </summary>
    static readonly TimeSpan OperationGuard = TimeSpan.FromSeconds(30);

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
            throw new InvalidOperationException(
                $"The code under test started no further timer. {Observed()}", e);
        }
    }

    /// <summary>
    /// Waits for an operation that the test has moved the clock far enough for, and fails the test if it is still
    /// running after <see cref="OperationGuard"/> of real time. Without it, an implementation that waits one timer
    /// longer than the test expects leaves the operation waiting on a clock nobody moves, and the test hangs until the
    /// test runner kills it. The operation's own result or exception is passed on unchanged.
    /// </summary>
    public async Task<T> WithWatchdogAsync<T>(Task<T> operation)
    {
        try
        {
            return await operation.WaitAsync(OperationGuard, TimeProvider.System);
        }
        catch (TimeoutException) when (!operation.IsCompleted)
        {
            throw Stuck();
        }
    }

    /// <inheritdoc cref="WithWatchdogAsync{T}(Task{T})"/>
    public async Task WithWatchdogAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(OperationGuard, TimeProvider.System);
        }
        catch (TimeoutException) when (!operation.IsCompleted)
        {
            throw Stuck();
        }
    }

    /// <summary>Waits for the next timer, checks its due time, and moves the clock past it.</summary>
    public async Task AdvancePastNextTimerAsync(TimeSpan expected)
    {
        Assert.Equal(expected, await NextTimerAsync());
        Advance(expected);
    }

    InvalidOperationException Stuck() => new(
        $"The operation was still running after {OperationGuard.TotalSeconds:0}s of real time, so it is probably " +
        $"waiting for a timer that the test did not move the clock past. {Observed()}");

    /// <summary>Which timers the code started and how far the test moved the clock, to show where it got stuck.</summary>
    string Observed()
    {
        var timers = Timers;
        var started = timers.Count == 0 ? "none" : string.Join(", ", timers.Select(t => t.ToString("c")));
        return $"Timers started, in order: [{started}]. Clock moved by {GetUtcNow() - Start:c}.";
    }
}
