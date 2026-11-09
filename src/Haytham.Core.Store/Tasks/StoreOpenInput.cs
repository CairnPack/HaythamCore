namespace Haytham.Core.Store;

public sealed record StoreOpenInput(StoreLocation Location, StoreOpenMode Mode, BeforeStorePublish? BeforePublish = null);
