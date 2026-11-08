namespace Haytham.Core.Store;

public sealed record OpenedStore(IContentStore Store, StoreLocation Location);
