using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store;

public sealed record StoreVerifyInput(Sha1 Sha1, VerifyMode Mode);

public sealed partial class StoreVerifyTask : RuntimeTask<StoreVerifyInput, ObjectState>
{
    private readonly StoreModule module;

    internal StoreVerifyTask(StoreModule module) => this.module = module;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.CancelOnExit;

    protected override async ValueTask<ObjectState> ExecuteAsync(TaskEvent<StoreVerifyInput, ObjectState> e)
    {
        var store = module.RequireStore();
        return await store.VerifyAsync(e.Input.Sha1, e.Input.Mode, e.Cancellation).ConfigureAwait(false);
    }
}
