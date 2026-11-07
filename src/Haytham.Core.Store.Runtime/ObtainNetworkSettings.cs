namespace Haytham.Core.Store.Runtime;

public sealed class ObtainNetworkSettings
{
    public required string UserAgent { get; init; }

    public string? Proxy { get; init; }

    public override string ToString() =>
        $"ObtainNetworkSettings {{ UserAgent = {UserAgent}, Proxy = {(Proxy is null ? "null" : "<redacted>")} }}";
}
