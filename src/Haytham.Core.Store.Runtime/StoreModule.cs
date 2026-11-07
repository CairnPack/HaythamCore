using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;

namespace Haytham.Core.Store.Runtime;

[RuntimeModule]
internal sealed class StoreModule(StoreModuleOptions options, StoreStatusView status) : IRuntimeModule
{
    private IContentStore? store;

    public void Connect(ModuleWiring wiring) => wiring.SubscribeRegister(OnRegistering);

    private ValueTask OnRegistering(RuntimeEvents.OnRegistering e, EventContext context)
    {
        e.Registry.RegisterTask(new StoreVerifyTask(this));
        e.Registry.RegisterTask(new StoreImportFileTask(this));
        e.Registry.RegisterTask(new StoreObtainTask(this));
        e.Registry.RegisterTask(new StoreOpenTask(this));
        e.Registry.Subscribe<RuntimeEvents.OnLoading>(OnLoading);
        e.Registry.Subscribe<RuntimeEvents.OnUnloading>(OnUnloading);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnLoading(RuntimeEvents.OnLoading e, EventContext context)
    {
        OpenedStore opened;
        try
        {
            opened = await options.Open(new StoreOpenInput(null), context.Cancellation).ConfigureAwait(false);
        }
        catch (StoreOpenException failure)
        {
            status.Publish(new StoreSnapshot(StoreAvailability.Unavailable, null, failure));
            return;
        }

        Volatile.Write(ref store, opened.Store);
        status.Publish(new StoreSnapshot(StoreAvailability.Ready, opened.Location, null));
    }

    private ValueTask OnUnloading(RuntimeEvents.OnUnloading e, EventContext context)
    {
        Volatile.Write(ref store, null);
        return ValueTask.CompletedTask;
    }

    internal IContentStore RequireStore() =>
        Volatile.Read(ref store) ?? throw new StoreUnavailableException(status.Current);
}
