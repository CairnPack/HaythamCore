namespace Haytham.Core.Store.Runtime;

public sealed class StoreModuleOptions
{
    public required StoreOpener Open { get; init; }

    public required int DownloadConcurrency { get; init; }

    public required Func<ObtainNetworkSettings> ReadNetworkSettings { get; init; }
}
