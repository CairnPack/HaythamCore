namespace Haytham.Core.Store.Runtime;

/// <summary>An expected opening failure identified by a stable error key and optional message arguments.</summary>
public sealed class StoreOpenException(
    string errorKey,
    IReadOnlyDictionary<string, object?>? arguments = null,
    Exception? innerException = null) : Exception(errorKey, innerException)
{
    public string ErrorKey { get; } = errorKey;

    public IReadOnlyDictionary<string, object?>? Arguments { get; } = arguments;
}
