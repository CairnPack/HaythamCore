using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store;

public sealed partial class StoreObtainTask : RuntimeTask<ObtainRequest, ObtainResult>
{
    private readonly StoreModule module;
    private readonly StoreModuleOptions options;

    internal StoreObtainTask(StoreModule module, StoreModuleOptions options)
    {
        this.module = module;
        this.options = options;
    }

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override async ValueTask<ObtainResult> ExecuteAsync(TaskEvent<ObtainRequest, ObtainResult> e)
    {
        var store = module.RequireStore();
        var network = options.ReadNetworkSettings();
        return await store.ObtainAsync(e.Input, network.Http, network.UserAgent, e.Cancellation).ConfigureAwait(false);
    }
}
