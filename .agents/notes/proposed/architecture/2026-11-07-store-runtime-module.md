# Agent Note: 把 Store 接成 Haytham.Runtime 的模块

Status: proposed

本篇记录用户 2026-11-07 确认的做法。模块的加载与状态发布、四个 Task 的登记、退出策略和执行逻辑，以及每次 Obtain 借用客户端与 User-Agent 的 Store 接口均已实现。模块与 Task 的行为由宿主的进程隔离测试覆盖，HC 不另建 RuntimeHost；宿主侧验证仍属剩余范围，因此本篇保留 proposed。

## Problem

HaythamRuntime（CairnPack/HaythamRuntime）已以 NuGet 包 `Haytham.Runtime` 0.1.0 发布，它提供模块生命周期、Task 的接纳与排队、取消、退出策略和编译期生成的模块清单。HC 与 Gebinde 的消费者（首先是 Haytham Launcher 的原生库）要在同一个 Runtime 宿主里使用 Store，由 Runtime 负责执行和收尾，而不是各自管理 Store 的打开、操作和退出。

约束有三条：

- 直接用工厂打开 Store、不启动 Runtime 的用法 must 保留。
- 仓库位置的优先级、位置记录、“位置不存在时不新建空仓库”这些是产品规则，不属于 HC。
- 外接硬盘没插上这类打开失败时，宿主应用要能照常运行，用户修好后原地重试，不要求重启进程。

## Proposal

### 项目与依赖

- 模块直接写在 `src/Haytham.Core.Store` 里（用户 2026-11-09 确认），`Haytham.Core.Store` 依赖 `Haytham.Runtime`；不另建接入项目。
- 模块主类 `StoreModule.cs` must 放在项目根目录，方便找到入口，和 MC 模组把主类放在包根目录的习惯一致。其余源码按职责放在 `Module/`（选项、快照、状态视图）、`Tasks/`（四个 Task、输入与网络设置）、`Opening/`（位置、打开结果、打开委托），两个异常放进已有的 `Errors/`；命名空间沿用项目统一的 `Haytham.Core.Store`。
- 仓库启用中央包管理（`Directory.Packages.props`），Runtime 的版本只写在这一处。使用 HC 的宿主 must 和 HC 用同一个 Runtime 版本，因为它们在同一次发布里编在一起。
- 新依赖都是 MIT，已登记在 `THIRD_PARTY_NOTICES.md`。

### 模块与状态

- `StoreModule` 在 `OnLoading` 调用宿主提供的 `StoreOpener`。成功就保存 Store 并发布 Ready；宿主抛 `StoreOpenException`（预期的打开失败，带稳定的错误键）就发布 Unavailable 和原因，`OnLoading` 正常结束，宿主照常启动。其他异常算内部缺陷，照常往上抛，never 降成“不可用”。
- 模块内部的打开方法 `OpenAsync` 把预期的打开失败作为 Unavailable 快照返回（快照带着那个 `StoreOpenException`），不抛出；`OnLoading` 直接等待它，不写 try/catch。只有 `StoreOpenTask` 在拿到 Unavailable 快照时把快照里的失败抛出，因为请求打开的调用方要把它当成错误处理。
- 可用状态只有 `Unavailable`、`Ready`。宿主把 `StoreStatusView` 放进能力表，从它同步读取最新快照（可用性、位置及来源、最近一次打开失败）；它不暴露 Store 实例。
- Store 实例放在模块的私有字段里，Task 每次执行时取；不可用时抛 `StoreUnavailableException`（`haytham.store.unavailable`）。不增加切换仓库接口或锁。
- 位置规则全部在宿主的 `StoreOpener` 里，HC 只决定何时打开、何时发布状态。

### Task

| Task | 输入 → 输出 | 退出策略 |
|---|---|---|
| `StoreVerifyTask` | `StoreVerifyInput(Sha1, VerifyMode)` → `ObjectState` | CancelOnExit |
| `StoreImportFileTask` | `StoreImportFileInput(Path, ExpectedContent?)` → `ImportResult` | Wait |
| `StoreObtainTask` | `ObtainRequest` → `ObtainResult` | Wait |
| `StoreOpenTask` | `StoreOpenInput(Path?)` → `StoreSnapshot` | Wait |

- Verify、ImportFile、Obtain 在 Store 不可用时直接失败，不等待用户修好；否则退出策略为 Wait 的 Obtain 会让安全退出一直等下去。
- `StoreOpenTask`：已 Ready 且没给路径或路径相同，返回当前快照；位置用平台的 `Path.GetRelativePath` 比较，消除相对路径与末尾分隔符等表示差异；已 Ready 但路径不同，抛错误键为 `haytham.store.alreadyOpen` 的 `StoreOpenException`，不改变状态；Unavailable 时调用 `StoreOpener`，成功发布 Ready，只有 `StoreOpenException` 才发布 Unavailable，并作为这次 Task 的错误抛给调用方；它带的位置（解析出位置之后才失败时由宿主填入）放进快照的 `location`，界面据此说明是哪个文件夹打不开，其他异常直接向上传播。宿主 must 用同一个队列键提交所有打开请求，由 Runtime 串行，所以模块里不加锁。

### 每次 Obtain 的网络设置

- `IContentStore.ObtainAsync(ObtainRequest request, HttpClient http, string userAgent, CancellationToken cancellationToken = default)` 与 `ObjectDownloader.ObtainAsync` 使用每次调用借出的客户端和 User-Agent。客户端由调用方持有，Store never 释放它；成功、失败或取消时，方法完成前本次网络工作 must 已结束。
- `ContentStoreFactory.Open(root, options, mode, program)` 和 `Create(store, options)` 不接收客户端，`DownloadOptions` 不再包含 User-Agent。重试、超时和并发设置仍由同一个 Store 内的 downloader 共用，客户端改变不重建并发信号量。
- `StoreObtainTask` 在 `RequireStore()` 成功后调用一次 `options.ReadNetworkSettings()`。按快照创建本次专用的 `SocketsHttpHandler` 与 `HttpClient`：Proxy 为 null 时不设置 handler.Proxy，沿用系统代理；非 null 时设置 `new WebProxy(proxy)`。本次 Obtain 完成后释放二者。
- 下载并发数由宿主的 opener 在打开时写进 `DownloadOptions.MaxConcurrentRequests`，HC 模块不经手这个值。网络设置读取、代理构造和 User-Agent 参数错误直接向上传播，never 改成 Store 不可用。
- Verify、ImportFile、Obtain 将本次 Task 的取消令牌交给对应的 Store 操作。`TaskEvent.Output.ReportProgress` 可报告进度，但当前三个 HC 操作没有进度输出，所以不新增进度。

### 剩余范围

- 宿主侧进程隔离测试：打开失败时宿主仍进入 Running、重试成功后宿主和模块没有重建、业务 Task 在不可用时立即失败、Ready 时同路径成功和异路径拒绝、只有预期打开失败发布 Unavailable、连续重试按队列串行，以及每次获取读取网络设置并正确处理代理、释放资源。Runtime 一个进程只能创建一次宿主，所以需要不同宿主的测试要放在单独的进程里。
- NuGet 0.1.0 的 `TaskEvent`、`EventContext`、`ModuleWiring` 构造函数都是 internal，`RuntimeTask.ExecuteCoreAsync` 也是 internal，没有发现不创建宿主就能公开执行模块或 Task 的入口。HC 不通过反射执行内部入口，不增加重置单例接口。
- HC 的工厂、下载、修复测试使用新签名，保留原行为断言；非法 User-Agent 改在 Obtain 开始时拒绝，已存在对象也不能掩盖错误。`Each_obtain_uses_its_own_client_and_user_agent` 验证同一 Store 的两次获取分别使用当次客户端和 User-Agent。

## Alternatives considered

- **单独建 `Haytham.Core.Store.Runtime` 接入项目，`Haytham.Core.Store` 保持不依赖任何包** — 最有力的理由是 HC 原有的“Store 可依赖：无”规则，以及不启动 Runtime 的使用者不必带上 Runtime。第一版就是这样实现的，2026-11-09 合并。不采用：拆 Runtime 本来就是为了让使用者方便地用上它，CairnPack 的产品都跑在 Runtime 上，“可依赖：无”这条规则已经过时；拆成两个项目只会让一个模块的代码分在两处。Runtime 是 MIT，带上它不影响 HC 的授权约束，不启动 Runtime 时直接用工厂的用法也照样保留。
- **`OpenAsync` 发布 Unavailable 后重新抛出，`OnLoading` 用空的 catch 接住** — 最有力的理由是打开 Task 不用再判断快照，失败直接从 `OpenAsync` 传上去。第一版就是这样写的，2026-11-09 改掉。不采用：只看 `OnLoading` 的人看到一个什么都不做的 catch，会以为错误被吞掉了，要去读 `OpenAsync` 才知道失败已经发布。预期的失败作为返回的状态表达出来，只有真要把它当错误的调用方才抛出，读起来更直接。
- **打开失败时让 `OnLoading` 抛错、整个宿主启动失败** — 最有力的理由是状态简单，模块要么可用要么不存在。不采用：外接硬盘没插上时宿主应用会打不开，用户也没法在应用里修正位置后重试。
- **用模块内的锁和 Opening 状态处理并发的打开请求** — 最有力的理由是不依赖宿主怎么提交。不采用：Runtime 的队列键已经能把打开请求串行，锁、Opening 状态和对应的错误键都是多出来的。
- **在 HC 事件总线上挂监听器完成打开和操作**（[HC 生命周期接入](2026-10-30-hc-lifecycle-event-bus.md)的候选形状） — 最有力的理由是接近 MC 事件系统的使用体验。不采用：Runtime 已经提供生命周期事件和 Task，HC 再建一条总线就是第二套执行机制。

## Acceptance criteria

- `dotnet build` 0 警告，生成器没有 HRT 诊断；`dotnet test` 和 `dotnet format whitespace . --folder --verify-no-changes` 通过。
- 宿主侧剩余范围完成后，上面列的模块与 Task 行为有进程隔离测试覆盖，再改为 implemented。
- 本篇通过 `npm run verify-notes`。

## Risks

- `Haytham.Runtime` 是 0.x，公开接口可能变化；升级时 HC 与宿主一起改版本。
- Runtime Task 为每次 Obtain 创建 HttpClient，不同 Task 之间不共享连接池；直接调用 Store 的调用方仍可借出同一个客户端。目前 Runtime 没有连接复用的性能要求。

## 笔记审计

- [HC 生命周期接入](2026-10-30-hc-lifecycle-event-bus.md)：部分重叠。那篇记录的使用体验目标（消费方不逐一管理生命周期）由 Runtime 模块满足；HcEvent 总线和 `hcEvent` 外观的候选形状不再推进。
- [工厂的链式配置](2026-10-30-store-factory-fluent-configuration.md)：部分重叠。那篇的参数事实已更新为工厂只接收下载设置、每次 Obtain 借入 HttpClient 与 User-Agent，配置回调的候选结论不变。
- [Store 消费契约](../../implemented/architecture/2026-10-30-store-consumer-contract.md)：部分重叠。`IContentStore` 的契约不变；Runtime 模块只是它的另一种使用方式。
