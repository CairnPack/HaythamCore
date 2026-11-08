namespace Haytham.Core.Store;

/// <summary>An expected opening failure identified by a stable error key and optional message arguments.</summary>
/// <param name="location">Where opening was tried; null when the location itself could not be resolved.</param>
public sealed class StoreOpenException(
    string errorKey,
    IReadOnlyDictionary<string, object?>? arguments = null,
    Exception? innerException = null,
    StoreLocation? location = null) : Exception(errorKey, innerException)
{
    public string ErrorKey { get; } = errorKey;

    public IReadOnlyDictionary<string, object?>? Arguments { get; } = arguments;

    public StoreLocation? Location { get; } = location;
}
