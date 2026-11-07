using Haytham.Runtime.Tasks;

namespace Haytham.Core.Store.Runtime;

public sealed record StoreImportFileInput(string Path, ExpectedContent? Expected = null);

public sealed partial class StoreImportFileTask : RuntimeTask<StoreImportFileInput, ImportResult>
{
    private readonly StoreModule module;

    internal StoreImportFileTask(StoreModule module) => this.module = module;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override ValueTask<ImportResult> ExecuteAsync(TaskEvent<StoreImportFileInput, ImportResult> e)
    {
        module.RequireStore();
        throw new NotImplementedException("在接入步骤 3 实现");
    }
}
