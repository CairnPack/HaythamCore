namespace Haytham.Core.Store.Runtime;

/// <param name="Path">Null or empty to resolve the host's location priority again; otherwise an existing store chosen by the user.</param>
public sealed record StoreOpenInput(string? Path);

public sealed record OpenedStore(IContentStore Store, StoreLocation Location);

/// <summary>Opens a store using the host's location resolution and location record handling.</summary>
/// <exception cref="StoreOpenException">An expected location or store opening failure.</exception>
public delegate ValueTask<OpenedStore> StoreOpener(StoreOpenInput input, CancellationToken cancellation);
