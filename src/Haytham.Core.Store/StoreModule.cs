using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;

namespace Haytham.Core.Store;

[RuntimeModule]
internal sealed class StoreModule(StoreModuleOptions options, StoreStatusView status) : IRuntimeModule
{
    private IContentStore? store;

    public void Connect(ModuleWiring wiring) => wiring.SubscribeRegister(OnRegistering);

    private ValueTask OnRegistering(RuntimeEvents.OnRegistering e, EventContext context)
    {
        e.Registry.RegisterTask(new StoreVerifyTask(this));
        e.Registry.RegisterTask(new StoreImportFileTask(this));
        e.Registry.RegisterTask(new StoreObtainTask(this, options));
        e.Registry.RegisterTask(new StoreOpenTask(this));
        e.Registry.Subscribe<RuntimeEvents.OnLoading>(OnLoading);
        e.Registry.Subscribe<RuntimeEvents.OnUnloading>(OnUnloading);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnLoading(RuntimeEvents.OnLoading e, EventContext context)
    {
        try
        {
            await OpenAsync(new StoreOpenInput(null), context.Cancellation).ConfigureAwait(false);
        }
        catch (StoreOpenException)
        {
            // OpenAsync published the expected failure; the host can finish loading and retry later.
        }
    }

    internal async ValueTask<StoreSnapshot> OpenAsync(StoreOpenInput input, CancellationToken cancellation)
    {
        var current = status.Current;
        if (current.Availability == StoreAvailability.Ready)
        {
            if (string.IsNullOrEmpty(input.Path) || Path.GetRelativePath(current.Location!.Root, input.Path) == ".")
                return current;

            throw new StoreOpenException("haytham.store.alreadyOpen");
        }

        OpenedStore opened;
        try
        {
            opened = await options.Open(input, cancellation).ConfigureAwait(false);
        }
        catch (StoreOpenException failure)
        {
            status.Publish(new StoreSnapshot(StoreAvailability.Unavailable, failure.Location, failure));
            throw;
        }

        Volatile.Write(ref store, opened.Store);
        var snapshot = new StoreSnapshot(StoreAvailability.Ready, opened.Location, null);
        status.Publish(snapshot);
        return snapshot;
    }

    private ValueTask OnUnloading(RuntimeEvents.OnUnloading e, EventContext context)
    {
        Volatile.Write(ref store, null);
        return ValueTask.CompletedTask;
    }

    internal IContentStore RequireStore() =>
        Volatile.Read(ref store) ?? throw new StoreUnavailableException(status.Current);
}
