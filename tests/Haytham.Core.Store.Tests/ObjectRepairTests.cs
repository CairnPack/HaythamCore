using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Haytham.Core.Testing;

namespace Haytham.Core.Store.Tests;

public class ObjectRepairTests
{
    static readonly byte[] Content = [1, 2, 3, 4, 5];
    static readonly byte[] Corrupt = [9, 8, 7];
    static readonly Sha1 Key = Sha1.FromBytes(SHA1.HashData(Content));
    static readonly ProgramInfo Program = new("repair-tests", "1.0");
    const string Url = "https://repair.invalid/object";

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static ContentStore Open(TempStoreDirectory dir, TestClock clock) =>
        new(new StoreLayout(dir.Path), StoreFormat.Read(new StoreLayout(dir.Path)), Program, clock);

    static Task<FileLock> HoldRepair(ContentStore store, TestClock clock) =>
        FileLock.AcquireExclusiveAsync(
            Path.Combine(store.Root, "locks", "repair", Key.ToString()[..3]),
            Program, $"repair {Key} (gate)", TimeSpan.FromMinutes(1), clock, Token);

    static async Task WaitingAsync(Task operation, TestClock clock)
    {
        var timer = clock.NextTimerAsync();
        Assert.Same(timer, await Task.WhenAny(operation, timer));
        Assert.Equal(TimeSpan.FromMilliseconds(50), await timer);
    }

    static async Task<ContentStore> CorruptStoreAsync(TempStoreDirectory dir)
    {
        var store = dir.Create();
        await store.ImportBytesAsync(Content, cancellationToken: Token);
        TempStoreDirectory.Tamper(store.GetObjectPath(Key), Corrupt);
        return store;
    }

    static async Task<ImportOutcome> ObtainAsync(ContentStore store, TestClock clock)
    {
        using var handler = new FakeHttpHandler().Respond(HttpMethod.Get, Url, (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) }));
        using var http = new HttpClient(handler);
        var downloader = new ObjectDownloader(store, http, new DownloadOptions
        {
            UserAgent = "repair-tests/1.0",
            ReadIdleTimeout = Timeout.InfiniteTimeSpan,
        }, clock);
        var result = await downloader.ObtainAsync(new ObtainRequest { Sha1 = Key, Urls = [new Uri(Url)] }, Token);
        Assert.Single(handler.Requests);
        return result.Outcome;
    }

    static async Task<ImportOutcome> ImportAsync(ContentStore store) =>
        (await store.ImportBytesAsync(Content, cancellationToken: Token)).Outcome;

    [Fact]
    public async Task A_repair_waits_for_the_shared_lock_and_cancellation_leaves_the_corrupt_object_in_place()
    {
        using var dir = new TempStoreDirectory();
        var initial = dir.Create();
        await initial.ImportBytesAsync(Content, cancellationToken: Token);
        TempStoreDirectory.Tamper(initial.GetObjectPath(Key), Corrupt);
        var clock = new TestClock();
        var store = Open(dir, clock);
        using var held = await HoldRepair(store, clock);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var import = store.ImportBytesAsync(Content, cancellationToken: cancel.Token);
        await WaitingAsync(import, clock);
        Assert.Equal(Corrupt, File.ReadAllBytes(store.GetObjectPath(Key)));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clock.WithWatchdogAsync(import));
        Assert.Equal(Corrupt, File.ReadAllBytes(store.GetObjectPath(Key)));
        Assert.Empty(dir.TempEntries());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Two_stale_repairs_quarantine_only_the_original_bad_object(bool download, bool downloadWins)
    {
        using var dir = new TempStoreDirectory();
        await CorruptStoreAsync(dir);
        var firstClock = new TestClock();
        var secondClock = new TestClock();
        var first = Open(dir, firstClock);
        var second = Open(dir, secondClock);
        var held = await HoldRepair(first, firstClock);
        using (held)
        {
            var importing = ImportAsync(first);
            await WaitingAsync(importing, firstClock);
            var other = download ? ObtainAsync(second, secondClock) : ImportAsync(second);
            await WaitingAsync(other, secondClock);
            // Both full checks have returned Corrupt, and both parts are complete and read-only before waiting.
            var parts = Directory.GetFiles(first.Layout.TempDirectory, "*.part");
            Assert.Equal(2, parts.Length);
            Assert.All(parts, p => Assert.True(File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly)));
            held.Dispose();

            var winner = downloadWins ? other : importing;
            var loser = downloadWins ? importing : other;
            var winnerClock = downloadWins ? secondClock : firstClock;
            var loserClock = downloadWins ? firstClock : secondClock;
            winnerClock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.Equal(ImportOutcome.ReplacedCorrupt, await winnerClock.WithWatchdogAsync(winner));
            loserClock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.Equal(ImportOutcome.AlreadyPresent, await loserClock.WithWatchdogAsync(loser));
        }

        Assert.Equal(Content, File.ReadAllBytes(first.GetObjectPath(Key)));
        Assert.Equal(Corrupt, File.ReadAllBytes(Assert.Single(Directory.GetFiles(first.Layout.TempDirectory))));
        Assert.Equal(ObjectState.Valid, await first.VerifyAsync(Key, VerifyMode.Full, Token));
        Assert.Equal(ObjectState.Valid, await second.VerifyAsync(Key, VerifyMode.Full, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_waiting_repair_rechecks_a_missing_or_normally_published_object(bool publishWhileWaiting)
    {
        using var dir = new TempStoreDirectory();
        await CorruptStoreAsync(dir);
        var clock = new TestClock();
        var store = Open(dir, clock);
        using var held = await HoldRepair(store, clock);
        var repair = store.ImportBytesAsync(Content, cancellationToken: Token);
        await WaitingAsync(repair, clock);
        File.Move(store.GetObjectPath(Key), store.Layout.NewCorruptPath(Key));
        if (publishWhileWaiting)
        {
            var result = await store.ImportBytesAsync(Content, cancellationToken: Token);
            Assert.Equal(ImportOutcome.Added, result.Outcome);
        }
        held.Dispose();
        clock.Advance(TimeSpan.FromMilliseconds(50));

        var repaired = await clock.WithWatchdogAsync(repair);

        Assert.Equal(publishWhileWaiting ? ImportOutcome.AlreadyPresent : ImportOutcome.Added, repaired.Outcome);
        Assert.Equal(Content, File.ReadAllBytes(store.GetObjectPath(Key)));
        Assert.Equal(Corrupt, File.ReadAllBytes(Assert.Single(Directory.GetFiles(store.Layout.TempDirectory))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_imports_do_not_take_the_repair_lock(bool alreadyPresent)
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();
        if (alreadyPresent) await store.ImportBytesAsync(Content, cancellationToken: Token);
        var clock = new TestClock();
        store = Open(dir, clock);
        using var held = await HoldRepair(store, clock);

        var result = await clock.WithWatchdogAsync(store.ImportBytesAsync(Content, cancellationToken: Token));

        Assert.Equal(alreadyPresent ? ImportOutcome.AlreadyPresent : ImportOutcome.Added, result.Outcome);
        Assert.Empty(clock.Timers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repair_lock_timeout_names_the_holder_and_source(bool download)
    {
        using var dir = new TempStoreDirectory();
        await CorruptStoreAsync(dir);
        var clock = new TestClock();
        var store = Open(dir, clock);
        using var held = await HoldRepair(store, clock);
        var operation = download ? ObtainAsync(store, clock) : ImportAsync(store);
        await WaitingAsync(operation, clock);
        clock.Advance(TimeSpan.FromMinutes(2));

        var error = await Assert.ThrowsAsync<StoreException>(() => clock.WithWatchdogAsync(operation));

        Assert.Equal(StoreError.LockTimeout, error.Error);
        Assert.Contains("repair-tests 1.0", error.Message);
        Assert.Contains($"repair {Key} ({(download ? "download" : "import")})", error.Message);
        Assert.Equal(Corrupt, File.ReadAllBytes(store.GetObjectPath(Key)));
        Assert.Empty(dir.TempEntries());
    }

    [Fact]
    public async Task A_repair_lock_is_exclusive_across_processes()
    {
        const string ChildRoot = "HAYTHAM_REPAIR_LOCK_TEST_ROOT";
        const string Checked = "child observed the held repair lock";
        if (Environment.GetEnvironmentVariable(ChildRoot) is { } root)
        {
            var error = await Assert.ThrowsAsync<StoreException>(() => FileLock.AcquireExclusiveAsync(
                new StoreLayout(root).RepairLockPath(Key), Program, "child repair", TimeSpan.Zero, TimeProvider.System, Token));
            Assert.Equal(StoreError.LockTimeout, error.Error);
            Console.WriteLine(Checked);
            return;
        }

        using var dir = new TempStoreDirectory();
        var store = dir.Create();
        var clock = new TestClock();
        using var held = await HoldRepair(store, clock);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(ObjectRepairTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(ObjectRepairTests).FullName}.{nameof(A_repair_lock_is_exclusive_across_processes)}");
        start.Environment[ChildRoot] = dir.Path;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(Token);
        var errors = child.StandardError.ReadToEndAsync(Token);
        try
        {
            await child.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.Equal(0, child.ExitCode);
            Assert.Contains(Checked, await output);
            Assert.Empty(await errors);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

}
