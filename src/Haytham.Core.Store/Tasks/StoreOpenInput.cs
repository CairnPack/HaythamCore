namespace Haytham.Core.Store;

/// <param name="Path">Null or empty to resolve the host's location priority again; otherwise an existing store chosen by the user.</param>
public sealed record StoreOpenInput(string? Path);
