# Haytham.Core.Store.Runtime

将内容仓库接入 Haytham.Runtime：加载时打开唯一仓库，通过 `StoreStatusView` 发布可用状态，并登记校验、文件纳入、获取和打开仓库四个 Task。
加载与四个 Task 的执行逻辑均已实现。`Module/` 放模块、配置和状态，`Tasks/` 放 Task、输入与本次获取使用的网络设置，`Opening/` 放打开结果、位置、委托和异常；公开命名空间保持 `Haytham.Core.Store.Runtime`。

宿主须创建 `StoreModuleOptions`、`StoreStatusView` 并放入 `RuntimeCapabilities` 能力表，同时提供 Runtime 必需的 `ILoggerFactory`。
仓库位置优先级、位置记录读写及预期打开失败的转换由宿主的 `StoreModuleOptions.Open` 负责。
所有 `store.open` 操作由宿主使用同一个队列键串行提交。

| Task | 行为 | 退出策略 |
|---|---|---|
| `StoreVerifyTask` | 使用当前 Store 校验对象 | CancelOnExit |
| `StoreImportFileTask` | 使用当前 Store 纳入文件 | Wait |
| `StoreObtainTask` | 使用当前 Store 和本次网络设置获取对象 | Wait |
| `StoreOpenTask` | 返回已打开仓库的快照，或在不可用时重试打开 | Wait |

校验、纳入和获取任务开始时调用 `RequireStore()`；不可用时立即抛出 `StoreUnavailableException`，不等待用户修好。三种操作均传递本次任务的取消令牌，当前 HC 操作没有进度输出。

打开任务在 Ready 时，未给路径或路径与当前位置相同就返回当前快照；路径不同则抛出错误键为 `haytham.store.alreadyOpen` 的 `StoreOpenException`，不改变状态。不可用时调用宿主的 `Open`，成功保存 Store 并发布 Ready；只有 `StoreOpenException` 会发布带失败原因的 Unavailable 后重新抛出，其他异常直接向上传播。加载时的预期打开失败保留 Unavailable，允许宿主继续启动。

`StoreObtainTask` 在开始时读取一次 `ReadNetworkSettings()`，为本次调用创建 `SocketsHttpHandler` 和 `HttpClient`。`Proxy` 为 null 时使用系统代理，非 null 时创建 `WebProxy`。调用 `ObtainAsync(request, http, userAgent, cancellationToken)` 完成后释放 client 和 handler，包括失败与取消。下载并发数由宿主的 opener 在打开时写进 `DownloadOptions.MaxConcurrentRequests`，同一个 Store 的各次获取共享这个额度。

Runtime 的 `TaskEvent`、`EventContext` 和 `ModuleWiring` 没有公开构造入口，Task 的执行入口也不是公开方法；模块及 Task 的行为由宿主的进程隔离测试覆盖，HC 测试不创建额外的 `RuntimeHost`。
