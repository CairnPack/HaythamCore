namespace Haytham.Core.Store;

/// <summary>Imports, verifies and obtains read-only objects in a content-addressed store keyed by SHA-1.</summary>
/// <remarks>
/// Create an instance with <see cref="ContentStoreFactory"/> and inject this interface into consumers. Share the
/// same instance for one store so downloads also share their concurrency limit. The caller chooses the store
/// location and supplies a borrowed HTTP client and a User-Agent on each obtain. I/O failures, content mismatches and
/// cancellation propagate from the underlying store and downloader.
/// The owner disposes the store after all operations have finished. Disposal does not remove stored files.
/// </remarks>
/// <example>
/// <code>
/// using IContentStore store = ContentStoreFactory.Open(
///     storePath, new DownloadOptions(), StoreOpenMode.CreateIfMissing);
/// var imported = await store.ImportBytesAsync(bytes, cancellationToken: cancellationToken);
/// using var http = new HttpClient();
/// var obtained = await store.ObtainAsync(request, http, "my-launcher/1.0", cancellationToken);
/// </code>
/// </example>
public interface IContentStore : IDisposable
{
    /// <summary>The absolute path of the store directory.</summary>
    string Root { get; }

    /// <summary>The persistent identity assigned when this store was created.</summary>
    Guid StoreId { get; }

    /// <summary>The store format version read from its format file.</summary>
    int Format { get; }

    /// <summary>Whether the format or locking configuration prevents imports and downloads.</summary>
    bool IsReadOnly { get; }

    /// <summary>Returns an object's path without checking that it exists or is intact.</summary>
    /// <remarks>Use <see cref="VerifyAsync"/> before relying on its contents. Stored objects must never be edited.</remarks>
    string GetObjectPath(Sha1 sha1);

    /// <summary>Checks whether an object is valid, missing or corrupt.</summary>
    /// <param name="sha1">The object's content key.</param>
    /// <param name="mode">Full hashes every time; Fast may reuse an unchanged file stamp and is not a safety guarantee.</param>
    /// <param name="cancellationToken">Cancels ongoing I/O; an immediate result may require no I/O.</param>
    Task<ObjectState> VerifyAsync(Sha1 sha1, VerifyMode mode, CancellationToken cancellationToken = default);

    /// <summary>Copies a stream into the store, validating its content before publishing it as a read-only object.</summary>
    /// <param name="content">The source stream. The caller retains ownership and disposes it.</param>
    /// <param name="expected">Optional SHA-1, size and SHA-512 checks; a mismatch fails the import.</param>
    /// <param name="cancellationToken">Cancels ongoing I/O.</param>
    /// <returns>The content key, size and whether the object was added, reused or repaired.</returns>
    /// <exception cref="StoreException">The store is read-only or the content does not match expectations.</exception>
    Task<ImportResult> ImportAsync(
        Stream content,
        ExpectedContent? expected = null,
        CancellationToken cancellationToken = default);

    /// <summary>Copies a file into the store, leaving the source unchanged and unshared with the stored object.</summary>
    /// <param name="path">The source file path.</param>
    /// <param name="expected">Optional content checks, with the same behavior as <see cref="ImportAsync"/>.</param>
    /// <param name="cancellationToken">Cancels ongoing I/O.</param>
    Task<ImportResult> ImportFileAsync(
        string path,
        ExpectedContent? expected = null,
        CancellationToken cancellationToken = default);

    /// <summary>Imports bytes with the same validation, reuse and repair behavior as <see cref="ImportAsync"/>.</summary>
    Task<ImportResult> ImportBytesAsync(
        byte[] content,
        ExpectedContent? expected = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reuses a matching stored object, or downloads and validates it from the request's candidate URLs.</summary>
    /// <remarks>
    /// Downloads use this call's client and User-Agent, with the store's shared retries and concurrency limit.
    /// A cross-process download lock prevents duplicate downloads of the same object.
    /// The client remains owned by the caller. All network work for this call ends before the returned task completes,
    /// including on failure or cancellation; the client may then be disposed.
    /// </remarks>
    /// <param name="request">The required SHA-1, optional size and SHA-512, and candidate HTTP(S) URLs in order.</param>
    /// <param name="http">The caller-owned HTTP client, kept alive until this operation completes.</param>
    /// <param name="userAgent">The User-Agent sent with every request in this operation.</param>
    /// <param name="cancellationToken">Cancels waits and ongoing I/O.</param>
    /// <returns>The stored object and the successful source URL, or a null source when the object was reused.</returns>
    /// <exception cref="StoreException">The store is read-only, a lock times out, or no candidate obtains valid content.</exception>
    Task<ObtainResult> ObtainAsync(
        ObtainRequest request,
        HttpClient http,
        string userAgent,
        CancellationToken cancellationToken = default);
}
