using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Haytham.Core.Testing;

namespace Haytham.Core.Store.Tests;

public sealed class ObjectDownloaderTests : IDisposable
{
    const string Modrinth = "https://cdn.modrinth.invalid/data/sodium.jar";
    const string CurseForge = "https://edge.forgecdn.invalid/files/sodium.jar";
    const string Body = "sodium-fabric-0.5.13+mc1.20.1.jar";

    static readonly Sha1 BodySha1 = Sha1.FromBytes(SHA1.HashData(Encoding.UTF8.GetBytes(Body)));

    /// <summary>
    /// The read idle timeout is off, so every timer the downloader starts is a wait (backoff, <c>Retry-After</c>,
    /// lock queueing). Tests about the idle timeout turn it on.
    /// </summary>
    static readonly DownloadOptions Options = new()
    {
        UserAgent = "store-tests/1.0",
        ReadIdleTimeout = Timeout.InfiniteTimeSpan,
    };

    readonly TempStoreDirectory _dir = new();
    readonly FakeHttpHandler _http = new();
    readonly TestClock _clock = new();

    static CancellationToken Token => TestContext.Current.CancellationToken;

    ObjectDownloader Downloader(ContentStore store, DownloadOptions? options = null) =>
        new(store, new HttpClient(_http), options ?? Options, _clock);

    Task<FileLock> HoldDownloadLock(ContentStore store, ProgramInfo holder) =>
        FileLock.AcquireExclusiveAsync(
            store.Layout.DownloadLockPath(BodySha1),
            holder,
            $"download {BodySha1}",
            TimeSpan.FromSeconds(1),
            _clock,
            Token);

    static ObtainRequest Request(params string[] urls) =>
        new() { Sha1 = BodySha1, Urls = [.. urls.Select(u => new Uri(u))] };

    static FakeHttpHandler.Responder TooManyRequests(RetryConditionHeaderValue retryAfter) =>
        (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = retryAfter;
            return Task.FromResult(response);
        };

    /// <summary>A 200 whose body is <paramref name="body"/>, read as it streams rather than buffered first.</summary>
    static FakeHttpHandler.Responder Streamed(Stream body, long? contentLength = null) =>
        (_, _) =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = contentLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };

    /// <summary>A body that sends nothing until its read is cancelled.</summary>
    static ScriptedStream Stalled() =>
        new([], async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        });

    /// <summary>
    /// A body that sends part of the file, then holds its next read until that read is cancelled and
    /// <paramref name="release"/> completes, and fails it with <paramref name="error"/>, as a connection aborted
    /// mid-body may. <paramref name="reading"/> completes once that read has started.
    /// </summary>
    static ScriptedStream AbortedWhenCancelled(Exception error, TaskCompletionSource reading, Task release) =>
        new(Encoding.UTF8.GetBytes(Body[..10]), async cancellationToken =>
        {
            reading.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            await release;
            throw error;
        });

    /// <summary>What an aborted body read throws: a plain I/O error, an HTTP one, or a cancellation with no token.</summary>
    static Exception ReadError(string thrown) => thrown switch
    {
        "IOException" => new IOException("connection aborted"),
        "HttpIOException" => new HttpIOException(HttpRequestError.ResponseEnded, "connection reset mid-body"),
        "OperationCanceledException" => new OperationCanceledException("read cancelled"),
        _ => throw new ArgumentOutOfRangeException(nameof(thrown), thrown, null),
    };

    public void Dispose()
    {
        _http.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task Downloads_into_the_store_with_the_callers_user_agent()
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.OK, Body);

        var result = await _clock.WithWatchdogAsync(Downloader(store).ObtainAsync(Request(Modrinth), Token));

        Assert.Equal(new ObtainResult(BodySha1, Body.Length, ImportOutcome.Added, new Uri(Modrinth)), result);
        Assert.Equal(ObjectState.Valid, await store.VerifyAsync(BodySha1, VerifyMode.Full, Token));
        Assert.Equal(["store-tests/1.0"], _http.Requests.Single().Headers["User-Agent"]);
        Assert.Empty(_dir.TempEntries());
    }

    [Fact]
    public async Task An_object_already_in_the_store_is_not_downloaded()
    {
        var store = _dir.Create();
        await store.ImportBytesAsync(Encoding.UTF8.GetBytes(Body), cancellationToken: Token);

        var result = await _clock.WithWatchdogAsync(Downloader(store).ObtainAsync(Request(Modrinth), Token));

        Assert.Equal(ImportOutcome.AlreadyPresent, result.Outcome);
        Assert.Null(result.Source);
        Assert.Empty(_http.Requests);
    }

    public static TheoryData<long?, string?> ExpectationsTheStoredObjectMisses => new()
    {
        { Body.Length + 1, null },
        { null, new string('0', 128) },
    };

    [Theory]
    [MemberData(nameof(ExpectationsTheStoredObjectMisses))]
    public async Task A_stored_object_that_misses_the_expected_size_or_sha512_fails_as_an_import_would(
        long? size,
        string? sha512)
    {
        var store = _dir.Create();
        await store.ImportBytesAsync(Encoding.UTF8.GetBytes(Body), cancellationToken: Token);
        _http.Respond(Modrinth, HttpStatusCode.OK, Body);
        var imported = await Assert.ThrowsAsync<StoreException>(() => store.ImportBytesAsync(
            Encoding.UTF8.GetBytes(Body),
            new ExpectedContent { Sha1 = BodySha1, Size = size, Sha512 = sha512 },
            Token));

        var obtained = await Assert.ThrowsAsync<StoreException>(
            () => _clock.WithWatchdogAsync(
                Downloader(store).ObtainAsync(Request(Modrinth) with { Size = size, Sha512 = sha512 }, Token)));

        Assert.Equal(StoreError.ContentMismatch, obtained.Error);
        Assert.Equal(imported.Message, obtained.Message);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_stored_object_that_meets_the_expected_size_and_sha512_is_not_downloaded()
    {
        var store = _dir.Create();
        await store.ImportBytesAsync(Encoding.UTF8.GetBytes(Body), cancellationToken: Token);
        var request = Request(Modrinth) with
        {
            Size = Body.Length,
            Sha512 = Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(Body))),
        };

        var result = await _clock.WithWatchdogAsync(Downloader(store).ObtainAsync(request, Token));

        Assert.Equal(new ObtainResult(BodySha1, Body.Length, ImportOutcome.AlreadyPresent, null), result);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Server_errors_are_retried_with_a_doubling_delay()
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.ServiceUnavailable)
            .Respond(Modrinth, HttpStatusCode.BadGateway)
            .Respond(Modrinth, HttpStatusCode.OK, Body);

        var obtain = Downloader(store).ObtainAsync(Request(Modrinth), Token);
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(1));
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(2));
        var result = await _clock.WithWatchdogAsync(obtain);

        Assert.Equal(ImportOutcome.Added, result.Outcome);
        Assert.Equal(3, _http.Requests.Count);
    }

    [Fact]
    public async Task Too_many_requests_waits_for_retry_after()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5))))
            .Respond(Modrinth, HttpStatusCode.OK, Body);

        var obtain = Downloader(store).ObtainAsync(Request(Modrinth), Token);
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(5));
        await _clock.WithWatchdogAsync(obtain);

        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task A_retry_after_date_is_counted_from_the_clocks_now()
    {
        var store = _dir.Create();
        var date = _clock.GetUtcNow() + TimeSpan.FromSeconds(7);
        _http.Respond(HttpMethod.Get, Modrinth, TooManyRequests(new RetryConditionHeaderValue(date)))
            .Respond(Modrinth, HttpStatusCode.OK, Body);

        var obtain = Downloader(store).ObtainAsync(Request(Modrinth), Token);
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(7));
        await _clock.WithWatchdogAsync(obtain);

        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task A_retry_after_above_the_limit_moves_on_to_the_next_url_without_waiting()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromHours(1))))
            .Respond(CurseForge, HttpStatusCode.OK, Body);

        var result = await _clock.WithWatchdogAsync(Downloader(store).ObtainAsync(Request(Modrinth, CurseForge), Token));

        Assert.Equal(new Uri(CurseForge), result.Source);
        Assert.Empty(_clock.Timers);
    }

    [Fact]
    public async Task Not_found_is_not_retried_and_the_next_url_is_tried()
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.NotFound).Respond(CurseForge, HttpStatusCode.OK, Body);

        var result = await _clock.WithWatchdogAsync(Downloader(store).ObtainAsync(Request(Modrinth, CurseForge), Token));

        Assert.Equal(new Uri(CurseForge), result.Source);
        Assert.Equal(1, _http.Requests.Count(r => r.Url == new Uri(Modrinth)));
        Assert.Empty(_clock.Timers);
    }

    [Fact]
    public async Task A_wrong_file_at_one_url_is_dropped_and_the_next_url_is_tried()
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.OK, "something else").Respond(CurseForge, HttpStatusCode.OK, Body);

        var result = await _clock.WithWatchdogAsync(Downloader(store).ObtainAsync(Request(Modrinth, CurseForge), Token));

        Assert.Equal(new Uri(CurseForge), result.Source);
        Assert.Equal(1, _http.Requests.Count(r => r.Url == new Uri(Modrinth)));
        Assert.Empty(_dir.TempEntries());
    }

    [Fact]
    public async Task Network_errors_are_retried_up_to_the_limit_then_every_reason_is_reported()
    {
        var store = _dir.Create();
        _http.Fail(Modrinth, new HttpRequestException("connection reset"))
            .Respond(CurseForge, HttpStatusCode.Forbidden);

        var obtain = Downloader(store).ObtainAsync(Request(Modrinth, CurseForge), Token);
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(1));
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<StoreException>(() => _clock.WithWatchdogAsync(obtain));

        Assert.Equal(StoreError.DownloadFailed, error.Error);
        Assert.Contains($"{Modrinth}: connection reset after 3 attempts", error.Message);
        Assert.Contains($"{CurseForge}: HTTP 403", error.Message);
        Assert.Equal(3, _http.Requests.Count(r => r.Url == new Uri(Modrinth)));
        Assert.Equal(ObjectState.Missing, await store.VerifyAsync(BodySha1, VerifyMode.Full, Token));
        Assert.Empty(_dir.TempEntries());
    }

    [Fact]
    public async Task A_body_that_stops_sending_data_times_out_and_is_retried()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, Streamed(Stalled())).Respond(Modrinth, HttpStatusCode.OK, Body);
        var options = Options with { ReadIdleTimeout = TimeSpan.FromSeconds(30) };

        var obtain = Downloader(store, options).ObtainAsync(Request(Modrinth), Token);
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(30));
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(1));
        var result = await _clock.WithWatchdogAsync(obtain);

        Assert.Equal(ImportOutcome.Added, result.Outcome);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Empty(_dir.TempEntries());
    }

    [Fact]
    public async Task A_body_that_keeps_stalling_is_reported_as_an_idle_timeout()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, (request, cancellationToken) => Streamed(Stalled())(request, cancellationToken));
        var options = Options with { MaxAttemptsPerUrl = 2, ReadIdleTimeout = TimeSpan.FromSeconds(30) };

        var obtain = Downloader(store, options).ObtainAsync(Request(Modrinth), Token);
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(30));
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(1));
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<StoreException>(() => _clock.WithWatchdogAsync(obtain));

        Assert.Equal(StoreError.DownloadFailed, error.Error);
        Assert.Contains("No data received for 30s", error.Message);
        Assert.Contains("after 2 attempts", error.Message);
    }

    [Fact]
    public async Task Cancelling_while_the_body_is_stalled_throws_and_is_not_retried()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, Streamed(Stalled()));
        var options = Options with { ReadIdleTimeout = TimeSpan.FromSeconds(30) };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var obtain = Downloader(store, options).ObtainAsync(Request(Modrinth), cancel.Token);
        // The idle timer has started, so the body is being read; it never fires because the clock stays put.
        Assert.Equal(TimeSpan.FromSeconds(30), await _clock.NextTimerAsync());
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _clock.WithWatchdogAsync(obtain));

        Assert.Single(_http.Requests);
        Assert.Equal([TimeSpan.FromSeconds(30)], _clock.Timers);
        Assert.Empty(_dir.TempEntries());
    }

    [Theory]
    [InlineData("IOException", "last attempt")]
    [InlineData("IOException", "backoff above the limit")]
    [InlineData("IOException", "attempts left")]
    [InlineData("HttpIOException", "last attempt")]
    [InlineData("HttpIOException", "backoff above the limit")]
    [InlineData("HttpIOException", "attempts left")]
    [InlineData("OperationCanceledException", "last attempt")]
    [InlineData("OperationCanceledException", "backoff above the limit")]
    [InlineData("OperationCanceledException", "attempts left")]
    public async Task Cancelling_while_the_body_is_read_throws_the_callers_cancellation_whatever_the_read_throws(
        string thrown,
        string retries)
    {
        var store = _dir.Create();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _http.Respond(HttpMethod.Get, Modrinth, Streamed(AbortedWhenCancelled(ReadError(thrown), reading, Task.CompletedTask)));
        var options = retries switch
        {
            "last attempt" => Options with { MaxAttemptsPerUrl = 1 },
            "backoff above the limit" => Options with { MaxRetryAfter = TimeSpan.Zero },
            _ => Options,
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var obtain = Downloader(store, options).ObtainAsync(Request(Modrinth), cancel.Token);
        await reading.Task.WaitAsync(Token);
        await cancel.CancelAsync();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _clock.WithWatchdogAsync(obtain));

        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Single(_http.Requests);
        Assert.Empty(_clock.Timers);
        Assert.Equal(ObjectState.Missing, await store.VerifyAsync(BodySha1, VerifyMode.Full, Token));
        Assert.Empty(_dir.TempEntries());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelling_as_the_idle_timer_fires_throws_the_callers_cancellation_on_the_last_attempt(
        bool timerFirst)
    {
        var store = _dir.Create();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _http.Respond(
            HttpMethod.Get,
            Modrinth,
            Streamed(AbortedWhenCancelled(ReadError("HttpIOException"), reading, release.Task)));
        var options = Options with { MaxAttemptsPerUrl = 1, ReadIdleTimeout = TimeSpan.FromSeconds(30) };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var obtain = Downloader(store, options).ObtainAsync(Request(Modrinth), cancel.Token);
        await reading.Task.WaitAsync(Token);
        // The body read is held until both have happened, so the read fails with the timer fired and the caller
        // cancelled, in either order.
        if (timerFirst)
        {
            await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(30));
            await cancel.CancelAsync();
        }
        else
        {
            Assert.Equal(TimeSpan.FromSeconds(30), await _clock.NextTimerAsync());
            await cancel.CancelAsync();
            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        release.SetResult();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _clock.WithWatchdogAsync(obtain));

        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Single(_http.Requests);
        Assert.Equal([TimeSpan.FromSeconds(30)], _clock.Timers);
        Assert.Empty(_dir.TempEntries());
    }

    [Theory]
    [InlineData("last attempt")]
    [InlineData("backoff above the limit")]
    [InlineData("attempts left")]
    public async Task Cancelling_while_the_request_is_sent_throws_the_callers_cancellation_even_if_the_handler_throws_an_http_error(
        string retries)
    {
        var store = _dir.Create();
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _http.Respond(HttpMethod.Get, Modrinth, async (_, cancellationToken) =>
        {
            sending.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            // A custom handler may report the aborted request as a network failure rather than a cancellation.
            throw new HttpRequestException("connection reset");
        });
        var options = retries switch
        {
            "last attempt" => Options with { MaxAttemptsPerUrl = 1 },
            "backoff above the limit" => Options with { MaxRetryAfter = TimeSpan.Zero },
            _ => Options,
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var obtain = Downloader(store, options).ObtainAsync(Request(Modrinth), cancel.Token);
        await sending.Task.WaitAsync(Token);
        await cancel.CancelAsync();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _clock.WithWatchdogAsync(obtain));

        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Single(_http.Requests);
        Assert.Empty(_clock.Timers);
        Assert.Equal(ObjectState.Missing, await store.VerifyAsync(BodySha1, VerifyMode.Full, Token));
    }

    [Fact]
    public async Task A_network_error_while_reading_the_body_is_retried()
    {
        var store = _dir.Create();
        var broken = new ScriptedStream(
            Encoding.UTF8.GetBytes(Body[..10]),
            _ => Task.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "connection reset mid-body")));
        _http.Respond(HttpMethod.Get, Modrinth, Streamed(broken)).Respond(Modrinth, HttpStatusCode.OK, Body);

        var obtain = Downloader(store).ObtainAsync(Request(Modrinth), Token);
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(1));
        var result = await _clock.WithWatchdogAsync(obtain);

        Assert.Equal(ImportOutcome.Added, result.Outcome);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Empty(_dir.TempEntries());
    }

    [Fact]
    public void A_malformed_user_agent_is_rejected_by_the_constructor()
    {
        var store = _dir.Create();

        Assert.Throws<ArgumentException>(
            () => Downloader(store, new DownloadOptions { UserAgent = "name/1.0 contact@x.com" }));
    }

    public static TheoryData<string?, long?> MalformedExpectations => new()
    {
        { "not-hex", null },
        { new string('g', 128), null },
        { null, -1 },
    };

    [Theory]
    [MemberData(nameof(MalformedExpectations))]
    public async Task A_malformed_sha512_or_size_is_reported_before_any_request(string? sha512, long? size)
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.NotFound).Respond(CurseForge, HttpStatusCode.NotFound);
        var request = Request(Modrinth, CurseForge) with { Sha512 = sha512, Size = size };

        await Assert.ThrowsAsync<ArgumentException>(() => Downloader(store).ObtainAsync(request, Token));

        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_malformed_sha512_is_reported_even_when_the_object_is_already_in_the_store()
    {
        var store = _dir.Create();
        await store.ImportBytesAsync(Encoding.UTF8.GetBytes(Body), cancellationToken: Token);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Downloader(store).ObtainAsync(Request(Modrinth) with { Sha512 = "not-hex" }, Token));
    }

    [Fact]
    public async Task A_doubled_delay_above_the_limit_moves_on_and_is_not_called_retry_after()
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.ServiceUnavailable);
        var options = Options with { RetryDelay = TimeSpan.FromSeconds(40) };

        var obtain = Downloader(store, options).ObtainAsync(Request(Modrinth), Token);
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(40));
        var error = await Assert.ThrowsAsync<StoreException>(() => _clock.WithWatchdogAsync(obtain));

        Assert.Contains($"{Modrinth}: HTTP 503, backoff 80s is above the 60s limit", error.Message);
        Assert.DoesNotContain("Retry-After", error.Message);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(40)], _clock.Timers);
    }

    [Fact]
    public async Task A_network_error_backoff_above_the_limit_moves_on()
    {
        var store = _dir.Create();
        _http.Fail(Modrinth, new HttpRequestException("connection reset"));
        var options = Options with { RetryDelay = TimeSpan.FromSeconds(40) };

        var obtain = Downloader(store, options).ObtainAsync(Request(Modrinth), Token);
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromSeconds(40));
        var error = await Assert.ThrowsAsync<StoreException>(() => _clock.WithWatchdogAsync(obtain));

        Assert.Contains($"{Modrinth}: connection reset, backoff 80s is above the 60s limit", error.Message);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(40)], _clock.Timers);
    }

    [Fact]
    public async Task Many_attempts_do_not_overflow_the_backoff()
    {
        var store = _dir.Create();
        // Retry-After: 0 keeps every wait short while the attempt count, which the backoff doubles on, keeps growing.
        for (var i = 0; i < 63; i++)
            _http.Respond(HttpMethod.Get, Modrinth, TooManyRequests(new RetryConditionHeaderValue(TimeSpan.Zero)));
        _http.Respond(Modrinth, HttpStatusCode.ServiceUnavailable);
        var options = Options with { MaxAttemptsPerUrl = 64 };

        var error = await Assert.ThrowsAsync<StoreException>(
            () => _clock.WithWatchdogAsync(Downloader(store, options).ObtainAsync(Request(Modrinth), Token)));

        Assert.Contains($"{Modrinth}: HTTP 503 after 64 attempts", error.Message);
        Assert.Equal(64, _http.Requests.Count);
    }

    [Fact]
    public async Task A_content_length_that_differs_from_the_expected_size_moves_on_without_reading_the_body()
    {
        var store = _dir.Create();
        var unread = new ScriptedStream([], _ => throw new InvalidOperationException("The body was read."));
        _http.Respond(HttpMethod.Get, Modrinth, Streamed(unread, contentLength: 10_000_000))
            .Respond(CurseForge, HttpStatusCode.OK, Body);

        var result = await _clock.WithWatchdogAsync(
            Downloader(store).ObtainAsync(Request(Modrinth, CurseForge) with { Size = Body.Length }, Token));

        Assert.Equal(new Uri(CurseForge), result.Source);
        Assert.Equal(1, _http.Requests.Count(r => r.Url == new Uri(Modrinth)));
        Assert.Empty(_clock.Timers);
        Assert.Empty(_dir.TempEntries());
    }

    [Fact]
    public async Task Waits_for_the_download_lock_and_times_out_naming_the_holder()
    {
        var store = _dir.Create();
        using var held = await HoldDownloadLock(store, new ProgramInfo("other-launcher", "2.0"));
        var options = Options with { LockTimeout = TimeSpan.FromMilliseconds(200) };

        var obtain = Downloader(store, options).ObtainAsync(Request(Modrinth), Token);
        // Backoff doubles from 50 ms; the last wait is cut short to end at the timeout.
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromMilliseconds(50));
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromMilliseconds(100));
        await _clock.AdvancePastNextTimerAsync(TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<StoreException>(() => _clock.WithWatchdogAsync(obtain));

        Assert.Equal(StoreError.LockTimeout, error.Error);
        Assert.Contains("other-launcher 2.0", error.Message);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_waiter_finds_the_object_the_lock_holder_downloaded_and_does_not_download_again()
    {
        var store = _dir.Create();
        var held = await HoldDownloadLock(store, ProgramInfo.Unknown);

        var waiting = Downloader(store).ObtainAsync(Request(Modrinth), Token);
        Assert.Equal(TimeSpan.FromMilliseconds(50), await _clock.NextTimerAsync());
        await store.ImportBytesAsync(Encoding.UTF8.GetBytes(Body), cancellationToken: Token);
        held.Dispose();
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        var result = await _clock.WithWatchdogAsync(waiting);

        Assert.Equal(ImportOutcome.AlreadyPresent, result.Outcome);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_waiter_that_finds_a_stored_object_missing_the_expected_size_fails_without_downloading()
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.OK, Body);
        var held = await HoldDownloadLock(store, ProgramInfo.Unknown);

        var waiting = Downloader(store).ObtainAsync(Request(Modrinth) with { Size = Body.Length + 1 }, Token);
        // The first backoff shows the waiter has already looked in the store and is queueing for the lock.
        Assert.Equal(TimeSpan.FromMilliseconds(50), await _clock.NextTimerAsync());
        await store.ImportBytesAsync(Encoding.UTF8.GetBytes(Body), cancellationToken: Token);
        held.Dispose();
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<StoreException>(() => _clock.WithWatchdogAsync(waiting));

        Assert.Equal(StoreError.ContentMismatch, error.Error);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Concurrent_obtains_of_one_object_download_it_once()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, async (_, cancellationToken) =>
        {
            await Task.Delay(50, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) };
        });
        // The real clock: how often the three waiters retry the lock while the holder writes to disk depends on the
        // machine, so there is no fixed sequence of timers for the test to step through.
        var downloader = new ObjectDownloader(store, new HttpClient(_http), Options, TimeProvider.System);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => downloader.ObtainAsync(Request(Modrinth), Token))));

        Assert.Single(_http.Requests);
        Assert.Single(results, r => r.Outcome == ImportOutcome.Added);
    }

    [Fact]
    public async Task A_read_only_store_refuses_to_download()
    {
        Directory.CreateDirectory(_dir.Path);
        await File.WriteAllTextAsync(
            Path.Combine(_dir.Path, "format.json"),
            """{"format":2,"minWriterFormat":2,"storeId":"5f0c1c43-0d0e-4b52-9d1a-7d0c2e4b1a11"}""",
            Token);
        var store = ContentStore.Open(_dir.Path);

        var error = await Assert.ThrowsAsync<StoreException>(
            () => _clock.WithWatchdogAsync(Downloader(store).ObtainAsync(Request(Modrinth), Token)));

        Assert.Equal(StoreError.ReadOnly, error.Error);
    }

    /// <summary>A response body that sends <c>prefix</c>, then answers every later read with <c>then</c>.</summary>
    sealed class ScriptedStream(byte[] prefix, Func<CancellationToken, Task<int>> then) : Stream
    {
        int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == prefix.Length) return await then(cancellationToken);
            var count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
