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

IContentStore store = ContentStoreFactory.Open(
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

## Microsoft sign-in

Haytham Core has no default Microsoft client ID. Every launcher that uses it must register its own Azure application and pass the client ID in.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
