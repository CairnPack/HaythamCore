using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store.Runtime;

public sealed partial class StoreOpenTask : RuntimeTask<StoreOpenInput, StoreSnapshot>
{
    private readonly StoreModule module;

    internal StoreOpenTask(StoreModule module) => this.module = module;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    // Ready with a null/empty path or the same path: return the current snapshot.
    // Ready with a different path: throw an exception with error key haytham.store.alreadyOpen.
    // Unavailable: call options.Open; on success retain the store and publish Ready.
    // On StoreOpenException, publish Unavailable with the failure and rethrow it.
    // The host submits all store.open operations with the same queue key; no lock is needed here.
    protected override ValueTask<StoreSnapshot> ExecuteAsync(TaskEvent<StoreOpenInput, StoreSnapshot> e)
    {
        throw new NotImplementedException("在接入步骤 3 实现");
    }
}
