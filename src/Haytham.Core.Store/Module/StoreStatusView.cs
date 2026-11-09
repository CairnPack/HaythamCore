namespace Haytham.Core.Store;

/// <summary>A host-owned view of the module's latest immutable snapshot.</summary>
public sealed class StoreStatusView
{
    private StoreSnapshot current = new(StoreAvailability.Unavailable, null, null);

    public StoreSnapshot Current => Volatile.Read(ref current);

    /// <summary>
    /// Records why the host could not ask HC to open a store, for example a broken location record. Without this the
    /// snapshot would say Unavailable and give no reason. Call it only while the store is not Ready.
    /// </summary>
    /// <param name="location">Where the host tried to open, or null when the location itself could not be resolved.</param>
    public void PublishHostFailure(StoreLocation? location, StoreOpenFailure failure) =>
        Publish(new StoreSnapshot(StoreAvailability.Unavailable, location, failure));

    internal void Publish(StoreSnapshot snapshot) => Volatile.Write(ref current, snapshot);
}
