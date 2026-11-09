namespace Haytham.Core.Store;

/// <summary>Called after the store is open and before anyone else can use it. Runs inside the open task only.</summary>
public delegate ValueTask<PublishDecision> BeforeStorePublish(StoreLocation location, CancellationToken cancellation);
