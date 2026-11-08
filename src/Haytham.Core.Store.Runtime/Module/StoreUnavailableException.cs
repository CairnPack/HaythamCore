namespace Haytham.Core.Store.Runtime;

public sealed class StoreUnavailableException(StoreSnapshot snapshot) : Exception("haytham.store.unavailable")
{
    public string ErrorKey => "haytham.store.unavailable";

    public StoreSnapshot Snapshot { get; } = snapshot;
}
