namespace Haytham.Core.Store;

public sealed record StoreOpenFailure(
    string ErrorKey,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    Exception? Cause = null);
