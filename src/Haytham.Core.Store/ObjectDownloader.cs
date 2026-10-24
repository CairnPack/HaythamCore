using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Haytham.Core.Store;

/// <summary>
/// Gets an object into the store by downloading it (<c>obtain</c>). One file is downloaded once at a time,
/// whichever URL it comes from: the download lock is keyed by SHA-1, so a second caller, in this process or
/// another, waits and then finds the object already there. Content goes through the same checks as an import.
/// </summary>
public sealed class ObjectDownloader
{
    /// <summary>The longest single wait or timer, about 24.8 days, which stays inside what
    /// <see cref="Task.Delay(TimeSpan, TimeProvider)"/> and the
    /// <see cref="CancellationTokenSource(TimeSpan, TimeProvider)"/> timer accept.</summary>
    static readonly TimeSpan LongestTimer = TimeSpan.FromMilliseconds(int.MaxValue);

    readonly ContentStore _store;
    readonly HttpClient _http;
    readonly DownloadOptions _options;
    readonly TimeProvider _timeProvider;
    readonly SemaphoreSlim _requests;

    public ObjectDownloader(ContentStore store, HttpClient http, DownloadOptions options)
        : this(store, http, options, TimeProvider.System)
    {
    }

    /// <param name="timeProvider">
    /// The clock for every wait and timeout: retry backoff, <c>Retry-After</c> dates, queueing for the download
    /// lock, the time written into lock holder records, and <see cref="DownloadOptions.ReadIdleTimeout"/>.
    /// </param>
    public ObjectDownloader(ContentStore store, HttpClient http, DownloadOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        // Checked here once, so a bad option fails now rather than on every obtain after it has taken a lock.
        ArgumentException.ThrowIfNullOrWhiteSpace(options.UserAgent);
        using (var probe = new HttpRequestMessage())
            if (!probe.Headers.UserAgent.TryParseAdd(options.UserAgent))
                throw new ArgumentException(
                    $"'{options.UserAgent}' is not a valid User-Agent; a contact address goes in parentheses.",
                    nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttemptsPerUrl, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentRequests, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRetryAfter, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxRetryAfter, LongestTimer);
        if (options.ReadIdleTimeout != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ReadIdleTimeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ReadIdleTimeout, LongestTimer);
        }

        _store = store;
        _http = http;
        _options = options;
        _timeProvider = timeProvider;
        _requests = new SemaphoreSlim(options.MaxConcurrentRequests);
    }

    public async Task<ObtainResult> ObtainAsync(ObtainRequest request, CancellationToken cancellationToken = default)
    {
        _store.ThrowIfReadOnly();
        if (request.Urls.Count == 0) throw new ArgumentException("At least one URL is needed.", nameof(request));
        foreach (var url in request.Urls)
            if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
                throw new ArgumentException($"'{url}' is not an absolute HTTP(S) URL.", nameof(request));
        var sha1 = request.Sha1;
        var expected = new ExpectedContent { Sha1 = sha1, Size = request.Size, Sha512 = request.Sha512 };
        // Checked before anything else: a 404 everywhere or an object already in the store would hide the mistake.
        var expectedSha512 = ObjectImporter.Validate(expected, nameof(request));

        // A stored object that misses the expected size or SHA-512 fails here as an import would: under the same
        // SHA-1, no download can bring other content.
        if (await _store.Importer.FindExistingAsync(expected, expectedSha512, cancellationToken) is { } existing)
            return Present(existing);

        // Held across retries and Retry-After waits: whoever waits for this lock wants this very object.
        using var held = await FileLock.AcquireExclusiveAsync(
            _store.Layout.DownloadLockPath(sha1),
            _store.Program,
            $"download {sha1}",
            _options.LockTimeout,
            _timeProvider,
            cancellationToken);

        // Whoever held the lock before may have just downloaded it.
        if (await _store.Importer.FindExistingAsync(expected, expectedSha512, cancellationToken) is { } downloaded)
            return Present(downloaded);

        var failures = new List<string>();
        foreach (var url in request.Urls)
        {
            var imported = await TryUrlAsync(url, expected, failures, cancellationToken);
            if (imported is not null) return new ObtainResult(imported.Sha1, imported.Size, imported.Outcome, url);
        }

        throw new StoreException(StoreError.DownloadFailed, $"Could not download {sha1}: {string.Join("; ", failures)}.");
    }

    /// <summary>Tries one URL with retries. Returns <see langword="null"/> and adds a reason when it gives up.</summary>
    async Task<ImportResult?> TryUrlAsync(
        Uri url,
        ExpectedContent expected,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            string failure;
            TimeSpan wait;
            var waitSource = "backoff";
            await _requests.WaitAsync(cancellationToken);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.UserAgent.ParseAdd(_options.UserAgent);
                using var response = await _http.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    // An error page or another build of the file is known to be wrong before any of it is read.
                    if (expected.Size is { } size
                        && response.Content.Headers.ContentLength is { } length
                        && length != size)
                    {
                        failures.Add($"{url}: Content-Length {length} bytes, expected {size}");
                        return null;
                    }

                    await using var body = new IdleTimeoutStream(
                        await response.Content.ReadAsStreamAsync(cancellationToken),
                        _options.ReadIdleTimeout,
                        _timeProvider);
                    return await _store.Importer.ImportAsync(body, expected, cancellationToken);
                }

                var status = (int)response.StatusCode;
                if (!IsRetryable(response.StatusCode))
                {
                    failures.Add($"{url}: HTTP {status}");
                    return null;
                }

                failure = $"HTTP {status}";
                if (RetryAfter(response.Headers.RetryAfter, _timeProvider.GetUtcNow()) is { } retryAfter)
                {
                    wait = retryAfter;
                    waitSource = "Retry-After";
                }
                else
                {
                    wait = Backoff(attempt);
                }
            }
            catch (StoreException e) when (e.Error == StoreError.ContentMismatch)
            {
                // A wrong file at this URL will be wrong again; the next candidate may have the right one.
                failures.Add($"{url}: {e.Message}");
                return null;
            }
            catch (Exception e) when (IsTransient(e, cancellationToken))
            {
                failure = e.Message;
                wait = Backoff(attempt);
            }
            finally
            {
                _requests.Release();
            }

            if (attempt >= _options.MaxAttemptsPerUrl)
            {
                failures.Add($"{url}: {failure} after {attempt} attempts");
                return null;
            }

            // The wait holds the download lock; past the limit, the next candidate URL is the better bet.
            var limit = _options.MaxRetryAfter;
            if (wait > limit)
            {
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{url}: {failure}, {waitSource} {wait.TotalSeconds:0}s is above the {limit.TotalSeconds:0}s limit"));
                return null;
            }

            await Task.Delay(wait, _timeProvider, cancellationToken);
        }
    }

    static ObtainResult Present(ImportResult existing) => new(existing.Sha1, existing.Size, existing.Outcome, null);

    /// <summary>
    /// <see cref="DownloadOptions.RetryDelay"/> doubled for every earlier attempt. It stops at
    /// <see cref="TimeSpan.MaxValue"/> instead of overflowing, which a large <see cref="DownloadOptions.MaxAttemptsPerUrl"/>
    /// can reach when <c>Retry-After: 0</c> answers kept the earlier waits short.
    /// </summary>
    TimeSpan Backoff(int attempt)
    {
        var wait = _options.RetryDelay;
        for (var i = 1; i < attempt && wait < TimeSpan.MaxValue; i++)
            wait = wait > TimeSpan.MaxValue - wait ? TimeSpan.MaxValue : wait + wait;
        return wait;
    }

    static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Network failures only. Errors writing the part file are ordinary <see cref="IOException"/>s and are not
    /// retried: a full disk stays full. An <see cref="HttpClient"/> timeout surfaces as a cancellation that the
    /// caller did not ask for; a body that stops sending data, as the <see cref="TimeoutException"/> from
    /// <see cref="IdleTimeoutStream"/>.
    /// </summary>
    static bool IsTransient(Exception e, CancellationToken cancellationToken) =>
        e is HttpRequestException or HttpIOException or TimeoutException
        || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}
