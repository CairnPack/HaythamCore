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
    readonly ContentStore _store;
    readonly HttpClient _http;
    readonly DownloadOptions _options;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly SemaphoreSlim _requests;

    public ObjectDownloader(ContentStore store, HttpClient http, DownloadOptions options)
        : this(store, http, options, Task.Delay)
    {
    }

    /// <param name="delay">Every wait (retries, lock backoff) goes through here, so tests do not sleep.</param>
    internal ObjectDownloader(
        ContentStore store,
        HttpClient http,
        DownloadOptions options,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.UserAgent);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttemptsPerUrl, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentRequests, 1);
        _store = store;
        _http = http;
        _options = options;
        _delay = delay;
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
        if (await IsPresentAsync(sha1, cancellationToken)) return Present(sha1);

        // Held across retries and Retry-After waits: whoever waits for this lock wants this very object.
        using var held = await FileLock.AcquireExclusiveAsync(
            _store.Layout.DownloadLockPath(sha1),
            _store.Program,
            $"download {sha1}",
            _options.LockTimeout,
            _delay,
            cancellationToken);

        // Whoever held the lock before may have just downloaded it.
        if (await IsPresentAsync(sha1, cancellationToken)) return Present(sha1);

        var expected = new ExpectedContent { Sha1 = sha1, Size = request.Size, Sha512 = request.Sha512 };
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
            TimeSpan? wait;
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
                    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return await _store.Importer.ImportAsync(body, expected, cancellationToken);
                }

                var status = (int)response.StatusCode;
                if (!IsRetryable(response.StatusCode))
                {
                    failures.Add($"{url}: HTTP {status}");
                    return null;
                }

                failure = $"HTTP {status}";
                wait = RetryAfter(response.Headers.RetryAfter) ?? Backoff(attempt);
                if (wait > _options.MaxRetryAfter)
                {
                    failures.Add($"{url}: HTTP {status} with Retry-After {wait.Value.TotalSeconds:0}s, above the limit");
                    return null;
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

            await _delay(wait.Value, cancellationToken);
        }
    }

    async Task<bool> IsPresentAsync(Sha1 sha1, CancellationToken cancellationToken) =>
        await _store.Verifier.VerifyAsync(sha1, VerifyMode.Fast, cancellationToken) == ObjectState.Valid;

    ObtainResult Present(Sha1 sha1) =>
        new(sha1, new FileInfo(_store.Layout.ObjectPath(sha1)).Length, ImportOutcome.AlreadyPresent, null);

    TimeSpan Backoff(int attempt) => _options.RetryDelay * Math.Pow(2, attempt - 1);

    static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    static TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Network failures only. Errors writing the part file are ordinary <see cref="IOException"/>s and are not
    /// retried: a full disk stays full. An <see cref="HttpClient"/> timeout surfaces as a cancellation that the
    /// caller did not ask for.
    /// </summary>
    static bool IsTransient(Exception e, CancellationToken cancellationToken) =>
        e is HttpRequestException or HttpIOException
        || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}
