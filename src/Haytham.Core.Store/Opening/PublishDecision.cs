namespace Haytham.Core.Store;

public sealed record PublishDecision(StoreOpenFailure? Failure)
{
    public static PublishDecision Allow { get; } = new(Failure: null);

    public static PublishDecision Reject(StoreOpenFailure failure) => new(failure);
}
