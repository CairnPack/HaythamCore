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
        e.Registry.Subscribe<RuntimeEvents.OnUnloading>(OnUnloading);
        return ValueTask.CompletedTask;
    }

    internal async ValueTask<StoreOpenResult> OpenAsync(StoreOpenInput input, CancellationToken cancellation)
    {
        var current = status.Current;
        if (current.Availability == StoreAvailability.Ready)
        {
            var failure = Path.GetRelativePath(current.Location!.Root, input.Location.Root) == "."
                ? null : new StoreOpenFailure("haytham.store.alreadyOpen");
            return new StoreOpenResult(current, failure);
        }

        cancellation.ThrowIfCancellationRequested();
        var opened = ContentStoreFactory.TryOpen(input.Location.Root, options.DownloadOptions, input.Mode);
        if (opened.Failure is { } openFailure)
            return PublishUnavailable(input.Location, openFailure);

        return await PublishOpenedStoreAsync(opened.Store!, input, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<StoreOpenResult> PublishOpenedStoreAsync(
        IContentStore opened, StoreOpenInput input, CancellationToken cancellation)
    {
        var decision = await DecidePublicationAsync(opened, input, cancellation).ConfigureAwait(false);
        if (decision.Failure is { } failure)
        {
            opened.Dispose();
            return PublishUnavailable(input.Location, failure);
        }

        // The host has committed its pre-publication work, so cancellation must not leave it without a ready store.
        Volatile.Write(ref store, opened);
        var snapshot = new StoreSnapshot(StoreAvailability.Ready, input.Location, null);
        status.Publish(snapshot);
        return new StoreOpenResult(snapshot, null);
    }

    private static async ValueTask<PublishDecision> DecidePublicationAsync(
        IContentStore opened, StoreOpenInput input, CancellationToken cancellation)
    {
        try
        {
            return input.BeforePublish is { } beforePublish
                ? await beforePublish(input.Location, cancellation).ConfigureAwait(false)
                : PublishDecision.Allow;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    private StoreOpenResult PublishUnavailable(StoreLocation location, StoreOpenFailure failure)
    {
        var snapshot = new StoreSnapshot(StoreAvailability.Unavailable, location, failure);
        status.Publish(snapshot);
        return new StoreOpenResult(snapshot, failure);
    }

    private ValueTask OnUnloading(RuntimeEvents.OnUnloading e, EventContext context)
    {
        Interlocked.Exchange(ref store, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    internal IContentStore RequireStore() =>
        Volatile.Read(ref store) ?? throw new StoreUnavailableException(status.Current);
}
