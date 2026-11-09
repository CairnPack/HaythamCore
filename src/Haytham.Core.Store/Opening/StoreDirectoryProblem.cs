namespace Haytham.Core.Store;

internal sealed record StoreDirectoryProblem(StoreError Error, string Message, Exception? Cause = null)
{
    internal StoreException ToException() => new(Error, Message, Cause);

    internal StoreOpenFailure ToFailure() => new(Error switch
    {
        StoreError.StoreMissing => "haytham.store.storeMissing",
        StoreError.NotAStore => "haytham.store.notAStore",
        StoreError.FormatUnreadable => "haytham.store.formatUnreadable",
        _ => throw new InvalidOperationException($"Unexpected opening error: {Error}."),
    }, Cause: ToException());
}
