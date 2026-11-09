namespace Haytham.Core.Store;

public sealed record StoreOpenResult(StoreSnapshot Snapshot, StoreOpenFailure? Failure);
