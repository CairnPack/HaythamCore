# Haytham.Core.Store.Runtime

将内容仓库接入 Haytham.Runtime：加载时打开唯一仓库，通过 `StoreStatusView` 发布可用状态，并登记校验、文件纳入、获取和打开仓库四个 Task。
当前仅实现加载与状态发布，四个 Task 的执行逻辑留待接入步骤 3。

宿主须创建 `StoreModuleOptions`、`StoreStatusView` 并放入 `RuntimeCapabilities` 能力表，同时提供 Runtime 必需的 `ILoggerFactory`。
仓库位置优先级、位置记录读写及预期打开失败的转换由宿主的 `StoreModuleOptions.Open` 负责。
所有 `store.open` 操作由宿主使用同一个队列键串行提交。
