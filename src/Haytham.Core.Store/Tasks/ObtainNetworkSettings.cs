namespace Haytham.Core.Store;

/// <summary>What one obtain uses to reach the network, read once when it starts.</summary>
public sealed class ObtainNetworkSettings
{
    public required string UserAgent { get; init; }

    /// <summary>Borrowed from the host, which keeps it open until every running obtain has finished.</summary>
    public required HttpClient Http { get; init; }
}
