using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store.Runtime;

public sealed partial class StoreObtainTask : RuntimeTask<ObtainRequest, ObtainResult>
{
    private readonly StoreModule module;

    internal StoreObtainTask(StoreModule module) => this.module = module;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override ValueTask<ObtainResult> ExecuteAsync(TaskEvent<ObtainRequest, ObtainResult> e)
    {
        module.RequireStore();
        throw new NotImplementedException("在接入步骤 3 实现");
    }
}
