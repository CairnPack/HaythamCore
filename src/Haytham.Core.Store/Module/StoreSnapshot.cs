namespace Haytham.Core.Store;

public enum StoreAvailability
{
    Unavailable,
    Ready,
}

public sealed record StoreSnapshot(
    StoreAvailability Availability,
    StoreLocation? Location,
    StoreOpenException? LastOpenFailure);
