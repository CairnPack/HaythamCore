namespace Haytham.Core.Store.Runtime;

/// <summary>Opens a store using the host's location resolution and location record handling.</summary>
/// <exception cref="StoreOpenException">An expected location or store opening failure.</exception>
public delegate ValueTask<OpenedStore> StoreOpener(StoreOpenInput input, CancellationToken cancellation);
