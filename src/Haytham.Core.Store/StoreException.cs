namespace Haytham.Core.Store;

public enum StoreError
{
    /// <summary>The directory has no <c>format.json</c> and the caller did not ask to create a store.</summary>
    StoreMissing,

    /// <summary>The directory already holds other files but no <c>format.json</c>; it is never taken over.</summary>
    NotAStore,

    /// <summary><c>format.json</c> exists but cannot be read; it is never rewritten.</summary>
    FormatUnreadable,

    /// <summary>The store was written by a newer format that this program must not write to.</summary>
    ReadOnly,

    /// <summary>The content does not match the expected size, SHA-1 or SHA-512.</summary>
    ContentMismatch,

    /// <summary>A lock was not free before the timeout; the message names the holders.</summary>
    LockTimeout,

    /// <summary>Every candidate URL failed; the message lists why each one did.</summary>
    DownloadFailed,
}

/// <summary>A store failure that callers can tell apart by <see cref="Error"/> instead of parsing the message.</summary>
public sealed class StoreException(StoreError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public StoreError Error { get; } = error;
}
