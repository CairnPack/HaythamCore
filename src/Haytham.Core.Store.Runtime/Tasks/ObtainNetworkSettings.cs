namespace Haytham.Core.Store.Runtime;

public sealed class ObtainNetworkSettings
{
    public required string UserAgent { get; init; }

    /// <summary>An explicit proxy address, or null to use the system proxy.</summary>
    public string? Proxy { get; init; }

    public override string ToString() =>
        $"ObtainNetworkSettings {{ UserAgent = {UserAgent}, Proxy = {(Proxy is null ? "null" : "<redacted>")} }}";
}
