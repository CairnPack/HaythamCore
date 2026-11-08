namespace Haytham.Core.Store.Runtime;

public sealed class StoreModuleOptions
{
    public required StoreOpener Open { get; init; }

    /// <summary>Reads one snapshot of the current User-Agent and proxy at the start of each obtain.</summary>
    public required Func<ObtainNetworkSettings> ReadNetworkSettings { get; init; }
}
