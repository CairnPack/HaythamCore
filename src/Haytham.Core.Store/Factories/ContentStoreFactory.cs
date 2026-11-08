namespace Haytham.Core.Store;

/// <summary>Creates the default <see cref="IContentStore"/> by composing the existing store and downloader.</summary>
/// <remarks>
/// Reuse the returned instance for one store to share its download concurrency limit. The caller supplies a
/// borrowed HTTP client and a User-Agent on each obtain. This factory does not choose a store path.
/// </remarks>
public static class ContentStoreFactory
{
    /// <summary>Opens a store and exposes its import, verification and download operations through one contract.</summary>
    /// <param name="root">The store directory chosen by the caller.</param>
    /// <param name="options">Shared download settings, including retries and the concurrency limit.</param>
    /// <param name="mode">Whether a missing or empty directory may become a store. The default only opens an existing store.</param>
    /// <param name="program">The program identity shown in lock holder records; defaults to the process name.</param>
    public static IContentStore Open(
        string root,
        DownloadOptions options,
        StoreOpenMode mode = StoreOpenMode.OpenExisting,
        ProgramInfo? program = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(ContentStore.Open(root, mode, program), options);
    }

    /// <summary>Uses an existing store instance, adding its default downloader without opening another store.</summary>
    /// <param name="store">The existing store shared with other consumers.</param>
    /// <param name="options">Shared download settings, including retries and the concurrency limit.</param>
    public static IContentStore Create(ContentStore store, DownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        return new DefaultContentStore(store, new ObjectDownloader(store, options));
    }
}
