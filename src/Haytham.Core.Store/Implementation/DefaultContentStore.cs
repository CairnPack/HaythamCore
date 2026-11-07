namespace Haytham.Core.Store;

/// <summary>Adapts the existing store and downloader to the consumer contract without changing their behavior.</summary>
sealed class DefaultContentStore(ContentStore store, ObjectDownloader downloader) : IContentStore
{
    public string Root => store.Root;

    public Guid StoreId => store.StoreId;

    public int Format => store.Format;

    public bool IsReadOnly => store.IsReadOnly;

    public string GetObjectPath(Sha1 sha1) => store.GetObjectPath(sha1);

    public Task<ObjectState> VerifyAsync(Sha1 sha1, VerifyMode mode, CancellationToken cancellationToken = default) =>
        store.VerifyAsync(sha1, mode, cancellationToken);

    public Task<ImportResult> ImportAsync(
        Stream content,
        ExpectedContent? expected = null,
        CancellationToken cancellationToken = default) =>
        store.ImportAsync(content, expected, cancellationToken);

    public Task<ImportResult> ImportFileAsync(
        string path,
        ExpectedContent? expected = null,
        CancellationToken cancellationToken = default) =>
        store.ImportFileAsync(path, expected, cancellationToken);

    public Task<ImportResult> ImportBytesAsync(
        byte[] content,
        ExpectedContent? expected = null,
        CancellationToken cancellationToken = default) =>
        store.ImportBytesAsync(content, expected, cancellationToken);

    public Task<ObtainResult> ObtainAsync(ObtainRequest request, CancellationToken cancellationToken = default) =>
        downloader.ObtainAsync(request, cancellationToken);
}
