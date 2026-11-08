using System.Net;
using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store.Runtime;

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
        var handler = new SocketsHttpHandler();
        if (network.Proxy is not null)
            handler.Proxy = new WebProxy(network.Proxy);
        using var http = new HttpClient(handler);
        return await store.ObtainAsync(e.Input, http, network.UserAgent, e.Cancellation).ConfigureAwait(false);
    }
}
