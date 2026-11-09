namespace Haytham.Core.Store;

public sealed class StoreModuleOptions
{
    /// <summary>Shared download settings used when the module creates its store.</summary>
    public required DownloadOptions DownloadOptions { get; init; }

    /// <summary>Reads the current User-Agent and HTTP client at the start of each obtain.</summary>
    public required Func<ObtainNetworkSettings> ReadNetworkSettings { get; init; }
}
