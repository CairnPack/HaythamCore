namespace Haytham.Core.Store.Runtime;

public sealed record OpenedStore(IContentStore Store, StoreLocation Location);
