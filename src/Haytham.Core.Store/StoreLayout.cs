namespace Haytham.Core.Store;

/// <summary>Where things live inside a store (store format v1).</summary>
sealed class StoreLayout(string root)
{
    /// <summary>Prefix of the temporary file that becomes <c>format.json</c> when a store is created.</summary>
    public const string FormatTempPrefix = ".format-";

    public string Root { get; } = root;

    public string FormatFile { get; } = Path.Combine(root, "format.json");

    public string ObjectsDirectory { get; } = Path.Combine(root, "objects");

    public string TempDirectory { get; } = Path.Combine(root, "tmp");

    public string DownloadLocksDirectory { get; } = Path.Combine(root, "locks", "download");

    /// <summary>
    /// Download locks are sharded by the first 3 hex digits of the key, at most 4096 files: one file per object
    /// would grow with the store, and deleting lock files after use races on POSIX.
    /// </summary>
    public string DownloadLockPath(Sha1 sha1) => Path.Combine(DownloadLocksDirectory, sha1.ToString()[..3]);

    public string ObjectPath(Sha1 sha1)
    {
        var hex = sha1.ToString();
        return Path.Combine(ObjectsDirectory, hex[..2], hex);
    }

    public string NewFormatTempPath() => Path.Combine(Root, $"{FormatTempPrefix}{Guid.NewGuid():N}.tmp");

    public string NewPartPath() => Path.Combine(TempDirectory, $"object-{Guid.NewGuid():N}.part");

    public string NewCorruptPath(Sha1 sha1) => Path.Combine(TempDirectory, $"corrupt-{sha1}-{Guid.NewGuid():N}");
}
