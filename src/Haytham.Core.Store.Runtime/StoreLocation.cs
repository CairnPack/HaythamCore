namespace Haytham.Core.Store.Runtime;

public enum StoreLocationSource
{
    Explicit,
    Environment,
    LocationRecord,
    Default,
}

public sealed record StoreLocation(string Root, StoreLocationSource Source);
