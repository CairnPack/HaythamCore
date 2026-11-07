namespace Haytham.Core.Store.Runtime;

public enum StoreAvailability
{
    Unavailable,
    Ready,
}

public sealed record StoreSnapshot(
    StoreAvailability Availability,
    StoreLocation? Location,
    StoreOpenException? LastOpenFailure);
