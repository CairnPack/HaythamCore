namespace Haytham.Core.Store;

public enum StoreOpenMode
{
    /// <summary>Fail with <see cref="StoreError.StoreMissing"/> when there is no store at the path.</summary>
    OpenExisting,

    /// <summary>Create the store when the directory is missing or empty.</summary>
    CreateIfMissing,
}
