namespace Haytham.Core.Store;

/// <summary>
/// Once the caller has cancelled, the caller's cancellation wins over whatever the cut-short operation threw: an
/// aborted connection may surface as an <see cref="IOException"/> or <see cref="HttpRequestException"/>, a linked
/// token as a cancellation for that token. Those become an <see cref="OperationCanceledException"/> for the caller's
/// token, with the original kept as the inner exception, so that nobody above retries or reports a failure.
/// </summary>
static class CallerCancellation
{
    /// <summary>
    /// Whether <paramref name="e"/> is to be replaced: the caller has cancelled and it is an I/O, HTTP, timeout or
    /// other-token cancellation error. The caller's own cancellation is left to pass through as it is, and so are
    /// errors no cancellation causes, such as a <see cref="StoreException"/> or a misused API.
    /// </summary>
    internal static bool Replaces(Exception e, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        && (e is IOException or HttpRequestException or TimeoutException
            || (e is OperationCanceledException cancelled && cancelled.CancellationToken != cancellationToken));

    internal static OperationCanceledException For(Exception e, CancellationToken cancellationToken) =>
        new("The operation was cancelled.", e, cancellationToken);
}
