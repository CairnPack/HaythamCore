# Haytham Core

A reusable Minecraft launcher core in C#: a content-addressed store (CAS) with garbage collection, version metadata, Java runtimes, mod loader installation, server runtimes, accounts and the game process.

Haytham Core is maintained by CairnPack and is under early development; nothing is usable yet. It knows nothing about modpacks: its launch entry point takes a game directory, a version and a loader.

## Building

Requires the .NET 10 SDK.

```sh
dotnet build HaythamCore.slnx
```

## Store 使用入口

外部消费方从 [IContentStore](src/Haytham.Core.Store/Api/IContentStore.cs) 阅读仓库操作的契约，从 [ContentStoreFactory](src/Haytham.Core.Store/Factories/ContentStoreFactory.cs) 创建默认实现，再通过构造函数把 `IContentStore` 注入业务模块。请求、结果和枚举位于 `Models/`，错误类型位于 `Errors/`；命名空间统一为 `Haytham.Core.Store`。

下面的 `storePath`、`filePath` 和 `cancellationToken` 由调用方提供：

```csharp
using Haytham.Core.Store;

using IContentStore store = ContentStoreFactory.Open(
    storePath,
    new DownloadOptions { MaxConcurrentRequests = 8 },
    StoreOpenMode.CreateIfMissing);

ImportResult imported = await store.ImportFileAsync(filePath, cancellationToken: cancellationToken);
ObjectState state = await store.VerifyAsync(imported.Sha1, VerifyMode.Full, cancellationToken);

using var http = new HttpClient();
var request = new ObtainRequest { Sha1 = imported.Sha1, Urls = [new Uri("https://example.com/object.jar")] };
ObtainResult obtained = await store.ObtainAsync(request, http, "my-launcher/1.0", cancellationToken);
```

`ObtainAsync(request, http, userAgent, cancellationToken)` 通过同一个接口复用已有对象或下载缺失对象，每次调用传入当时的 HTTP 客户端和 User-Agent。已有 `ContentStore` 实例时，使用 `ContentStoreFactory.Create(existingStore, options)`，直接复用该实例。调用方在组装入口创建并复用接口实例，使下载共享同一并发额度；重试等设置仍在 `DownloadOptions` 里。Store 只借用客户端，不释放它；本次调用的网络工作在方法完成（包括失败或取消）前结束，调用方此后可以释放客户端。工厂默认只打开已有仓库，创建仓库需要显式指定 `CreateIfMissing`，仓库路径和 User-Agent 没有库内默认值。

既有 `ContentStore` 和 `ObjectDownloader` 的直接调用方式继续保留。

## 作为 Haytham.Runtime 模块使用

Store 同时是 [Haytham.Runtime](https://github.com/CairnPack/HaythamRuntime) 的模块，入口是项目根目录的 [StoreModule](src/Haytham.Core.Store/StoreModule.cs)。模块登记校验、文件纳入、获取和打开仓库四个 Task，通过 `StoreStatusView` 发布可用状态；加载时不自动打开仓库。`Module/` 放模块的选项与状态，`Tasks/` 放 Task 和它们的输入、输出，`Opening/` 放打开回调、位置和失败；命名空间同样是 `Haytham.Core.Store`。

宿主须创建 `StoreModuleOptions`（提供 `DownloadOptions` 和 `ReadNetworkSettings`）、`StoreStatusView` 并放进 Runtime 的能力表。宿主解析具体 `StoreLocation`，启动时自行提交 `StoreOpenInput(Location, Mode, BeforePublish?)`。宿主根据是否允许创建选择 `CreateIfMissing` 或 `OpenExisting`。所有打开请求由宿主用同一个队列键提交，由 Runtime 串行执行。

HC 调用工厂创建实例，打开失败返回 `StoreOpenResult` 并发布带 `StoreOpenFailure` 的 Unavailable。可选的 `BeforePublish` 在实例打开后、发布前执行：返回 `PublishDecision.Allow` 才发布 Ready；返回 `PublishDecision.Reject(failure)` 则释放实例并发布 Unavailable。回调抛异常时释放实例后重新抛出。允许后不再检查取消，以保证宿主的发布前操作与 Ready 状态一致。已 Ready 时，同位置返回原快照，异位置返回原快照和 `haytham.store.alreadyOpen`，状态不变。模块拥有实例并在卸载时释放。

直接使用工厂时，可以调用 `ContentStoreFactory.TryOpen` 获得实例或失败；`ContentStoreFactory.Open` 和 `ContentStore.Open` 保留抛异常的入口。实例所有者须在操作完成后释放它，释放不删除仓库文件。

| Task | 行为 | 退出策略 |
|---|---|---|
| `StoreVerifyTask` | 使用当前 Store 校验对象 | CancelOnExit |
| `StoreImportFileTask` | 使用当前 Store 纳入文件 | Wait |
| `StoreObtainTask` | 使用当前 Store 和本次读取的网络设置获取对象 | Wait |
| `StoreOpenTask` | 打开指定位置，发布前等待宿主决定，返回快照和本次失败 | Wait |

## Microsoft sign-in

Haytham Core has no default Microsoft client ID. Every launcher that uses it must register its own Azure application and pass the client ID in.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
