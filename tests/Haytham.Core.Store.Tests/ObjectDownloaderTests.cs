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
}
