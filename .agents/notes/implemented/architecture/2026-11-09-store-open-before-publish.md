# Agent Note: HC 创建 Store，宿主在发布前决定是否允许使用

Status: implemented

## Problem

宿主创建实例再交给 HC，所以实例的创建者和持有者不是同一方。宿主在打开以后还要写自己的位置记录，写失败时说不清谁来释放实例。预期的打开失败先抛出、再由模块捕获，打开 Task 又把快照里的异常抛出一次，调用链不直观。

## Decision

- 宿主解析出具体的 `StoreLocation`，提交 `StoreOpenInput(Location, Mode, BeforePublish?)`。HC 不在加载事件里自动打开，不选择位置，也不认识宿主的位置记录。
- HC 按 `StoreOpenMode` 调用工厂。打开、读取格式和创建竞争都用返回值传递已知失败；还有公开调用方的 `ContentStore.Open`、`ContentStoreFactory.Open` 只是把失败转成异常的薄包装。
- `ContentStoreFactory.TryOpen` 返回 `haytham.store.storeMissing`、`haytham.store.notAStore`、`haytham.store.formatUnreadable`，文件系统故障返回 `haytham.store.io`。`StoreOpenFailure.Cause` 保留原始异常，宿主写日志时能分清拒绝访问和文件被占用。较新写入格式的仓库仍以只读方式打开。
- `BeforeStorePublish` 只在打开 Task 内、实例发布之前执行。拒绝时释放实例，发布带失败原因的 Unavailable；回调抛异常时释放实例，异常继续传播。
- 回调允许以后直接保存实例并发布 Ready，不再检查取消，因为宿主已经完成发布前的操作，这时放弃发布会让位置记录和内存状态不一致。
- Ready 时同位置的请求返回原快照，不调用工厂和回调；不同位置返回原快照和 `haytham.store.alreadyOpen`，状态不变。
- `IContentStore` 增加 `IDisposable`，默认实现释放下载用的并发信号量，不删除仓库文件。模块持有自己创建的实例，卸载时释放；直接使用工厂的调用方自己释放。
- 模块选项增加 `DownloadOptions`，因为工厂现在由 HC 调用，下载设置不能再由宿主的打开委托带进来。

## 笔记审计

- [Store Runtime 模块](../../proposed/architecture/2026-11-07-store-runtime-module.md)：部分取代。自动打开、宿主的打开委托和异常式的打开结果由本篇取代；Task 注册、排队和每次 Obtain 的网络约定仍然适用。
- [Store 消费契约](2026-10-30-store-consumer-contract.md)：部分重叠。组装和借用客户端的决定保留，释放的所有权由本篇补充。
- [首次打开与创建](../feature/2026-10-25-store-open-import-verify.md)：部分重叠。格式、创建竞争和只读行为保留，底层失败的传递方式由本篇更新。

## Alternatives considered

- 工厂继续抛业务异常，模块捕获后转换。调用处改动较少。不采用，因为已知失败仍然绕经异常，而调用方大多要继续执行。
- 回调允许以后再检查一次取消。能更及时地响应取消。不采用，原因见 Decision：宿主的记录已经写好，这时放弃发布会造成状态不一致。
- 回调异常和释放异常同时发生时用 `AggregateException` 合并。两个失败都不会丢。不采用，因为默认实现只释放一个 `SemaphoreSlim`，它的 `Dispose` 不会抛异常，这个分支只有测试替身才能走到。

## Testing

- `StoreModuleOpenTests` 覆盖允许、拒绝、回调异常、工厂失败时不调用回调、Ready 时的相同和不同位置、两种打开模式，以及允许以后取消仍然发布。模块测试直接调用内部的打开入口，不创建 `RuntimeHost`。
- `StoreOpenTests` 里三条创建竞争用例由检查抛出改为检查返回的失败，成功用例继续核对格式和身份，失败用例继续核对错误类别和文件未变。

## Consequences

好处是实例的创建、发布和释放都由 HC 管理，宿主可以在实例可用之前拒绝，已知失败不再绕经异常。

带来的副作用是打开的输入、输出、模块选项和释放约定都是公开 API 变更，宿主必须一起迁移。启动时的打开也要由宿主自己发起，所以每个宿主都要多写这一步。
