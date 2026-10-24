using System.Text.Json;

namespace Haytham.Core.Store.Tests;

public class FileLockTests
{
    static readonly ProgramInfo Program = new("store-tests", "1.0");

    readonly TestClock _clock = new();

    static CancellationToken Token => TestContext.Current.CancellationToken;

    Task<FileLock> Acquire(string path, TimeSpan timeout) =>
        FileLock.AcquireExclusiveAsync(path, Program, "test", timeout, _clock, Token);

    [Fact]
    public async Task A_second_handle_in_the_same_process_waits_and_times_out_naming_the_holder()
    {
        using var dir = new TempStoreDirectory();
        var path = Path.Combine(dir.Path, "locks", "download", "abc");

        using (await Acquire(path, TimeSpan.FromSeconds(1)))
        {
            var waiting = Acquire(path, TimeSpan.FromMilliseconds(200));
            await _clock.AdvancePastNextTimerAsync(TimeSpan.FromMilliseconds(50));
            await _clock.AdvancePastNextTimerAsync(TimeSpan.FromMilliseconds(100));
            await _clock.AdvancePastNextTimerAsync(TimeSpan.FromMilliseconds(50));
            var error = await Assert.ThrowsAsync<StoreException>(() => waiting);

            Assert.Equal(StoreError.LockTimeout, error.Error);
            Assert.Contains($"store-tests 1.0 (PID {Environment.ProcessId})", error.Message);
            // The holder record is dated by the same clock.
            Assert.Contains($"since {_clock.Start:u}", error.Message);
        }

        using var again = await Acquire(path, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_holder_record_exists_only_while_the_lock_is_held()
    {
        using var dir = new TempStoreDirectory();
        var path = Path.Combine(dir.Path, "locks", "download", "abc");
        var holders = path + ".holders";

        using (await Acquire(path, TimeSpan.FromSeconds(1)))
        {
            var record = Assert.Single(Directory.GetFiles(holders, "*.json"));
            Assert.StartsWith($"{Environment.ProcessId}-", Path.GetFileName(record));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(record, Token));
            Assert.Equal("store-tests", json.RootElement.GetProperty("program").GetString());
            Assert.Equal("test", json.RootElement.GetProperty("operation").GetString());
        }

        Assert.Empty(Directory.GetFiles(holders));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Records_of_ended_processes_are_removed_by_the_next_holder_and_never_shown()
    {
        using var dir = new TempStoreDirectory();
        var path = Path.Combine(dir.Path, "locks", "download", "abc");
        var holders = Directory.CreateDirectory(path + ".holders").FullName;
        var stale = Path.Combine(holders, "999999-0.json");
        await File.WriteAllTextAsync(
            stale,
            """{"format":1,"program":"ghost","version":"0","pid":999999,"startTime":"2001-01-01T00:00:00Z","operation":"x","since":"2001-01-01T00:00:00Z"}""",
            Token);

        Assert.Equal("an unknown holder", LockHolders.Describe(path));
        using (await Acquire(path, TimeSpan.FromSeconds(1)))
            Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task Waiting_can_be_cancelled()
    {
        using var dir = new TempStoreDirectory();
        var path = Path.Combine(dir.Path, "locks", "download", "abc");
        using var held = await Acquire(path, TimeSpan.FromSeconds(1));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var waiting = FileLock.AcquireExclusiveAsync(path, Program, "test", TimeSpan.FromMinutes(1), _clock, cancel.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(50), await _clock.NextTimerAsync());
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
