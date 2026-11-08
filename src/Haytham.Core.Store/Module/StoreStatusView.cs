namespace Haytham.Core.Store;

/// <summary>A host-owned view of the module's latest immutable snapshot.</summary>
public sealed class StoreStatusView
{
    private StoreSnapshot current = new(StoreAvailability.Unavailable, null, null);

    public StoreSnapshot Current => Volatile.Read(ref current);

    internal void Publish(StoreSnapshot snapshot) => Volatile.Write(ref current, snapshot);
}
