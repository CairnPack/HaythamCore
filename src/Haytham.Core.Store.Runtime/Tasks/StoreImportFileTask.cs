using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store.Runtime;

public sealed record StoreImportFileInput(string Path, ExpectedContent? Expected = null);

public sealed partial class StoreImportFileTask : RuntimeTask<StoreImportFileInput, ImportResult>
{
    private readonly StoreModule module;

    internal StoreImportFileTask(StoreModule module) => this.module = module;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override async ValueTask<ImportResult> ExecuteAsync(TaskEvent<StoreImportFileInput, ImportResult> e)
    {
        var store = module.RequireStore();
        return await store.ImportFileAsync(e.Input.Path, e.Input.Expected, e.Cancellation).ConfigureAwait(false);
    }
}
