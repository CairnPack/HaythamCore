namespace Haytham.Core.Store;

/// <summary>How <see cref="ObjectDownloader"/> talks to servers. Every default can be changed by the caller.</summary>
public sealed record DownloadOptions
{
    /// <summary>Sent with every request. The library never makes one up: each program names itself.</summary>
    public required string UserAgent { get; init; }

    /// <summary>Tries per candidate URL, counting the first; only I/O errors and 408, 429 and 5xx are retried.</summary>
    public int MaxAttemptsPerUrl { get; init; } = 3;

    /// <summary>Wait before the second try when the server gives no <c>Retry-After</c>; it doubles after that.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest wait before a retry, whether the server asked for it with <c>Retry-After</c> or it is the doubled
    /// <see cref="RetryDelay"/>; a longer one moves on to the next URL.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a response body may go without sending any data before the attempt is given up as a network error
    /// and retried. The time is counted per read, so a slow but steady download is never cut off, and time spent
    /// writing to disk does not count. <see cref="Timeout.InfiniteTimeSpan"/> turns it off.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> stops counting once the response headers arrive, so without this a server
    /// that sends headers and then nothing would hold the download lock until the caller gives up.
    /// </remarks>
    public TimeSpan ReadIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to queue for the download lock while another download of the same shard runs.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Requests in flight at once through one downloader.</summary>
    public int MaxConcurrentRequests { get; init; } = 8;
}

/// <summary>An object to obtain: its key, what else is known about it, and where it can be downloaded.</summary>
public sealed record ObtainRequest
{
    public required Sha1 Sha1 { get; init; }

    public long? Size { get; init; }

    /// <summary>SHA-512 as 128 hex characters, when the source provides one.</summary>
    public string? Sha512 { get; init; }

    /// <summary>Candidate URLs, tried in order (for example Modrinth, then CurseForge).</summary>
    public required IReadOnlyList<Uri> Urls { get; init; }
}

/// <param name="Source">The URL the object came from; <see langword="null"/> when it was already in the store.</param>
public sealed record ObtainResult(Sha1 Sha1, long Size, ImportOutcome Outcome, Uri? Source);
