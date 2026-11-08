using System.Runtime.ExceptionServices;
using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store;

public sealed partial class StoreOpenTask : RuntimeTask<StoreOpenInput, StoreSnapshot>
{
    private readonly StoreModule module;

    internal StoreOpenTask(StoreModule module) => this.module = module;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    // The host submits every store.open with the same queue key, so Runtime runs them one at a time.
    protected override async ValueTask<StoreSnapshot> ExecuteAsync(TaskEvent<StoreOpenInput, StoreSnapshot> e)
    {
        var snapshot = await module.OpenAsync(e.Input, e.Cancellation).ConfigureAwait(false);
        // Whoever asked to open the Store gets the failure as the task's error, unlike the open at load.
        if (snapshot.LastOpenFailure is { } failure)
            ExceptionDispatchInfo.Throw(failure);
        return snapshot;
    }
}
