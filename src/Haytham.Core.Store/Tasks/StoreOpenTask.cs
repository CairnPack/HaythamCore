using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store;

public sealed partial class StoreOpenTask : RuntimeTask<StoreOpenInput, StoreOpenResult>
{
    private readonly StoreModule module;

    internal StoreOpenTask(StoreModule module) => this.module = module;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    // The host submits every store.open with the same queue key, so Runtime runs them one at a time.
    protected override ValueTask<StoreOpenResult> ExecuteAsync(TaskEvent<StoreOpenInput, StoreOpenResult> e) =>
        module.OpenAsync(e.Input, e.Cancellation);
}
