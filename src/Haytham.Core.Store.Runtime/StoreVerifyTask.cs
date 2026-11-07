using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store.Runtime;

public sealed record StoreVerifyInput(Sha1 Sha1, VerifyMode Mode);

public sealed partial class StoreVerifyTask : RuntimeTask<StoreVerifyInput, ObjectState>
{
    private readonly StoreModule module;

    internal StoreVerifyTask(StoreModule module) => this.module = module;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.CancelOnExit;

    protected override ValueTask<ObjectState> ExecuteAsync(TaskEvent<StoreVerifyInput, ObjectState> e)
    {
        module.RequireStore();
        throw new NotImplementedException("在接入步骤 3 实现");
    }
}
