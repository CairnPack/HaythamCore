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

    readonly TempStoreDirectory _dir = new();
    readonly FakeHttpHandler _http = new();
    readonly List<TimeSpan> _delays = [];

    static CancellationToken Token => TestContext.Current.CancellationToken;

    ObjectDownloader Downloader(ContentStore store, DownloadOptions? options = null) =>
        new(
            store,
            new HttpClient(_http),
            options ?? new DownloadOptions { UserAgent = "store-tests/1.0" },
            (wait, cancellationToken) =>
            {
                lock (_delays) _delays.Add(wait);
                return Task.Delay(1, cancellationToken);
            });

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

        var result = await Downloader(store).ObtainAsync(Request(Modrinth), Token);

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

        var result = await Downloader(store).ObtainAsync(Request(Modrinth), Token);

        Assert.Equal(ImportOutcome.AlreadyPresent, result.Outcome);
        Assert.Null(result.Source);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Server_errors_are_retried_with_a_doubling_delay()
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.ServiceUnavailable)
            .Respond(Modrinth, HttpStatusCode.BadGateway)
            .Respond(Modrinth, HttpStatusCode.OK, Body);

        var result = await Downloader(store).ObtainAsync(Request(Modrinth), Token);

        Assert.Equal(ImportOutcome.Added, result.Outcome);
        Assert.Equal(3, _http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], _delays);
    }

    [Fact]
    public async Task Too_many_requests_waits_for_retry_after()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5))))
            .Respond(Modrinth, HttpStatusCode.OK, Body);

        await Downloader(store).ObtainAsync(Request(Modrinth), Token);

        Assert.Equal([TimeSpan.FromSeconds(5)], _delays);
    }

    [Fact]
    public async Task A_retry_after_above_the_limit_moves_on_to_the_next_url_without_waiting()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromHours(1))))
            .Respond(CurseForge, HttpStatusCode.OK, Body);

        var result = await Downloader(store).ObtainAsync(Request(Modrinth, CurseForge), Token);

        Assert.Equal(new Uri(CurseForge), result.Source);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Not_found_is_not_retried_and_the_next_url_is_tried()
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.NotFound).Respond(CurseForge, HttpStatusCode.OK, Body);

        var result = await Downloader(store).ObtainAsync(Request(Modrinth, CurseForge), Token);

        Assert.Equal(new Uri(CurseForge), result.Source);
        Assert.Equal(1, _http.Requests.Count(r => r.Url == new Uri(Modrinth)));
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task A_wrong_file_at_one_url_is_dropped_and_the_next_url_is_tried()
    {
        var store = _dir.Create();
        _http.Respond(Modrinth, HttpStatusCode.OK, "something else").Respond(CurseForge, HttpStatusCode.OK, Body);

        var result = await Downloader(store).ObtainAsync(Request(Modrinth, CurseForge), Token);

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

        var error = await Assert.ThrowsAsync<StoreException>(
            () => Downloader(store).ObtainAsync(Request(Modrinth, CurseForge), Token));

        Assert.Equal(StoreError.DownloadFailed, error.Error);
        Assert.Contains($"{Modrinth}: connection reset after 3 attempts", error.Message);
        Assert.Contains($"{CurseForge}: HTTP 403", error.Message);
        Assert.Equal(3, _http.Requests.Count(r => r.Url == new Uri(Modrinth)));
        Assert.Equal(ObjectState.Missing, await store.VerifyAsync(BodySha1, VerifyMode.Full, Token));
        Assert.Empty(_dir.TempEntries());
    }

    [Fact(Timeout = 10_000)]
    public async Task A_body_that_stops_sending_data_times_out_and_is_retried()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, Streamed(Stalled())).Respond(Modrinth, HttpStatusCode.OK, Body);
        var options = new DownloadOptions { UserAgent = "store-tests/1.0", ReadIdleTimeout = TimeSpan.FromMilliseconds(100) };

        var result = await Downloader(store, options).ObtainAsync(Request(Modrinth), TestContext.Current.CancellationToken);

        Assert.Equal(ImportOutcome.Added, result.Outcome);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], _delays);
        Assert.Empty(_dir.TempEntries());
    }

    [Fact(Timeout = 10_000)]
    public async Task A_body_that_keeps_stalling_is_reported_as_an_idle_timeout()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, (request, cancellationToken) => Streamed(Stalled())(request, cancellationToken));
        var options = new DownloadOptions
        {
            UserAgent = "store-tests/1.0",
            MaxAttemptsPerUrl = 2,
            ReadIdleTimeout = TimeSpan.FromMilliseconds(100),
        };

        var error = await Assert.ThrowsAsync<StoreException>(
            () => Downloader(store, options).ObtainAsync(Request(Modrinth), TestContext.Current.CancellationToken));

        Assert.Equal(StoreError.DownloadFailed, error.Error);
        Assert.Contains("No data received for 0.1s", error.Message);
        Assert.Contains("after 2 attempts", error.Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task Cancelling_while_the_body_is_stalled_throws_and_is_not_retried()
    {
        var store = _dir.Create();
        _http.Respond(HttpMethod.Get, Modrinth, Streamed(Stalled()));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Downloader(store).ObtainAsync(Request(Modrinth), cancel.Token));

        Assert.Single(_http.Requests);
        Assert.Empty(_delays);
        Assert.Empty(_dir.TempEntries());
    }

    [Fact]
    public async Task A_network_error_while_reading_the_body_is_retried()
    {
        var store = _dir.Create();
        var broken = new ScriptedStream(
            Encoding.UTF8.GetBytes(Body[..10]),
            _ => Task.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "connection reset mid-body")));
        _http.Respond(HttpMethod.Get, Modrinth, Streamed(broken)).Respond(Modrinth, HttpStatusCode.OK, Body);

        var result = await Downloader(store).ObtainAsync(Request(Modrinth), Token);

        Assert.Equal(ImportOutcome.Added, result.Outcome);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], _delays);
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
        var options = new DownloadOptions { UserAgent = "store-tests/1.0", RetryDelay = TimeSpan.FromSeconds(40) };

        var error = await Assert.ThrowsAsync<StoreException>(
            () => Downloader(store, options).ObtainAsync(Request(Modrinth), Token));

        Assert.Contains($"{Modrinth}: HTTP 503, backoff 80s is above the 60s limit", error.Message);
        Assert.DoesNotContain("Retry-After", error.Message);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(40)], _delays);
    }

    [Fact]
    public async Task A_network_error_backoff_above_the_limit_moves_on()
    {
        var store = _dir.Create();
        _http.Fail(Modrinth, new HttpRequestException("connection reset"));
        var options = new DownloadOptions { UserAgent = "store-tests/1.0", RetryDelay = TimeSpan.FromSeconds(40) };

        var error = await Assert.ThrowsAsync<StoreException>(
            () => Downloader(store, options).ObtainAsync(Request(Modrinth), Token));

        Assert.Contains($"{Modrinth}: connection reset, backoff 80s is above the 60s limit", error.Message);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(40)], _delays);
    }

    [Fact]
    public async Task Many_attempts_do_not_overflow_the_backoff()
    {
        var store = _dir.Create();
        // Retry-After: 0 keeps every wait short while the attempt count, which the backoff doubles on, keeps growing.
        for (var i = 0; i < 63; i++)
            _http.Respond(HttpMethod.Get, Modrinth, TooManyRequests(new RetryConditionHeaderValue(TimeSpan.Zero)));
        _http.Respond(Modrinth, HttpStatusCode.ServiceUnavailable);
        var options = new DownloadOptions { UserAgent = "store-tests/1.0", MaxAttemptsPerUrl = 64 };

        var error = await Assert.ThrowsAsync<StoreException>(
            () => Downloader(store, options).ObtainAsync(Request(Modrinth), Token));

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

        var result = await Downloader(store).ObtainAsync(Request(Modrinth, CurseForge) with { Size = Body.Length }, Token);

        Assert.Equal(new Uri(CurseForge), result.Source);
        Assert.Equal(1, _http.Requests.Count(r => r.Url == new Uri(Modrinth)));
        Assert.Empty(_delays);
        Assert.Empty(_dir.TempEntries());
    }

    [Fact]
    public async Task Waits_for_the_download_lock_and_times_out_naming_the_holder()
    {
        var store = _dir.Create();
        using var held = await FileLock.AcquireExclusiveAsync(
            store.Layout.DownloadLockPath(BodySha1),
            new ProgramInfo("other-launcher", "2.0"),
            $"download {BodySha1}",
            TimeSpan.FromSeconds(1),
            Task.Delay,
            Token);
        var options = new DownloadOptions { UserAgent = "store-tests/1.0", LockTimeout = TimeSpan.FromMilliseconds(200) };

        var error = await Assert.ThrowsAsync<StoreException>(
            () => Downloader(store, options).ObtainAsync(Request(Modrinth), Token));

        Assert.Equal(StoreError.LockTimeout, error.Error);
        Assert.Contains("other-launcher 2.0", error.Message);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_waiter_finds_the_object_the_lock_holder_downloaded_and_does_not_download_again()
    {
        var store = _dir.Create();
        var held = await FileLock.AcquireExclusiveAsync(
            store.Layout.DownloadLockPath(BodySha1),
            ProgramInfo.Unknown,
            "download",
            TimeSpan.FromSeconds(1),
            Task.Delay,
            Token);

        var waiting = Downloader(store).ObtainAsync(Request(Modrinth), Token);
        await store.ImportBytesAsync(Encoding.UTF8.GetBytes(Body), cancellationToken: Token);
        held.Dispose();
        var result = await waiting;

        Assert.Equal(ImportOutcome.AlreadyPresent, result.Outcome);
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
        var downloader = Downloader(store);

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
            () => Downloader(store).ObtainAsync(Request(Modrinth), Token));

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
