# Agent Note: 把 Store 接成 Haytham.Runtime 的模块（Haytham.Core.Store.Runtime）

Status: proposed

本篇记录用户 2026-11-07 确认的做法。骨架已经实现：新项目、全部公开类型、模块的加载与状态发布、四个 Task 的登记与退出策略；四个 Task 的执行逻辑和 Store 接口的调整还没有做，见下文“剩余范围”。

## Problem

HaythamRuntime（CairnPack/HaythamRuntime）已以 NuGet 包 `Haytham.Runtime` 0.1.0 发布，它提供模块生命周期、Task 的接纳与排队、取消、退出策略和编译期生成的模块清单。HC 与 Gebinde 的消费者（首先是 Haytham Launcher 的原生库）要在同一个 Runtime 宿主里使用 Store，由 Runtime 负责执行和收尾，而不是各自管理 Store 的打开、操作和退出。

约束有三条：

- `Haytham.Core.Store` 的“可依赖：无”不变，直接用工厂打开 Store、不启动 Runtime 的用法 must 保留。
- 仓库位置的优先级、位置记录、“位置不存在时不新建空仓库”这些是产品规则，不属于 HC。
- 外接硬盘没插上这类打开失败时，宿主应用要能照常运行，用户修好后原地重试，不要求重启进程。

## Proposal

### 项目与依赖

- 新项目 `src/Haytham.Core.Store.Runtime`，包名 `CairnPack.Haytham.Core.Store.Runtime`，依赖 `Haytham.Core.Store` 和 `Haytham.Runtime`；`Haytham.Core.Store` 本身不加依赖。
- 仓库启用中央包管理（`Directory.Packages.props`），Runtime 的版本只写在这一处。使用 HC 的宿主 must 和 HC 用同一个 Runtime 版本，因为它们在同一次发布里编在一起。
- 新依赖都是 MIT，已登记在 `THIRD_PARTY_NOTICES.md`。

### 模块与状态

- `StoreModule` 在 `OnLoading` 调用宿主提供的 `StoreOpener`。成功就保存 Store 并发布 Ready；宿主抛 `StoreOpenException`（预期的打开失败，带稳定的错误键）就发布 Unavailable 和原因，`OnLoading` 正常结束，宿主照常启动。其他异常算内部缺陷，照常往上抛，never 降成“不可用”。
- 可用状态只有 `Unavailable`、`Ready`。宿主把 `StoreStatusView` 放进能力表，从它同步读取最新快照（可用性、位置及来源、最近一次打开失败）；它不暴露 Store 实例。
- Store 实例放在模块的私有字段里，Task 每次执行时取；不可用时抛 `StoreUnavailableException`（`haytham.store.unavailable`）。以后在运行中更换仓库时只需替换这个字段，不需要重建宿主。
- 位置规则全部在宿主的 `StoreOpener` 里，HC 只决定何时打开、何时发布状态。

### Task

| Task | 输入 → 输出 | 退出策略 |
|---|---|---|
| `StoreVerifyTask` | `StoreVerifyInput(Sha1, VerifyMode)` → `ObjectState` | CancelOnExit |
| `StoreImportFileTask` | `StoreImportFileInput(Path, ExpectedContent?)` → `ImportResult` | Wait |
| `StoreObtainTask` | `ObtainRequest` → `ObtainResult` | Wait |
| `StoreOpenTask` | `StoreOpenInput(Path?)` → `StoreSnapshot` | Wait |

- Verify、ImportFile、Obtain 在 Store 不可用时直接失败，不等待用户修好；否则退出策略为 Wait 的 Obtain 会让安全退出一直等下去。
- `StoreOpenTask`：已 Ready 且没给路径或路径相同，返回当前快照；已 Ready 但路径不同，抛 `haytham.store.alreadyOpen`，不切换仓库；Unavailable 时调用 `StoreOpener`，成功发布 Ready，失败发布 Unavailable 并抛出。宿主 must 用同一个队列键提交所有打开请求，由 Runtime 串行，所以模块里不加锁。

### 剩余范围

- 四个 Task 的 `ExecuteAsync`（现在明确抛 `NotImplementedException`）。
- 下载设置改成每次 Obtain 现读：`ObtainAsync` 改为接收调用方借出的 `HttpClient` 和本次的 User-Agent，工厂不再固定它们；下载并发额度仍在打开时确定。这一步要同步修改[工厂的链式配置提案](2026-10-30-store-factory-fluent-configuration.md)里固定 HttpClient 与 User-Agent 的部分。
- 测试：打开失败时宿主仍进入 Running、重试成功后宿主和模块没有重建、业务 Task 在不可用时立即失败、连续重试按队列串行。Runtime 一个进程只能创建一次宿主，所以需要不同宿主的测试要放在单独的进程里。

## Alternatives considered

- **把 Runtime 接入直接写进 `Haytham.Core.Store`** — 最有力的理由是少一个项目。不采用：Store 是单独发布、只依赖自己的包，Gebinde 的转接和不启动 Runtime 的使用者不应被迫带上 Runtime。
- **打开失败时让 `OnLoading` 抛错、整个宿主启动失败** — 最有力的理由是状态简单，模块要么可用要么不存在。不采用：外接硬盘没插上时宿主应用会打不开，用户也没法在应用里修正位置后重试。
- **用模块内的锁和 Opening 状态处理并发的打开请求** — 最有力的理由是不依赖宿主怎么提交。不采用：Runtime 的队列键已经能把打开请求串行，锁、Opening 状态和对应的错误键都是多出来的。
- **在 HC 事件总线上挂监听器完成打开和操作**（[HC 生命周期接入](2026-10-30-hc-lifecycle-event-bus.md)的候选形状） — 最有力的理由是接近 MC 事件系统的使用体验。不采用：Runtime 已经提供生命周期事件和 Task，HC 再建一条总线就是第二套执行机制。

## Acceptance criteria

- `dotnet build HaythamCore.slnx -c Release` 0 警告，生成器没有 HRT 诊断；`dotnet test --solution HaythamCore.slnx -c Release --no-build` 通过。
- 剩余范围完成后，上面列的测试都有对应用例，并改为 implemented。
- 本篇通过 `npm run verify-notes`。

## Risks

- `Haytham.Runtime` 是 0.x，公开接口可能变化；升级时 HC 与宿主一起改版本。
- 每次 Obtain 由调用方借出 HttpClient，不同 Obtain 之间不共享连接池；目前没有连接复用的性能要求。

## 笔记审计

- [HC 生命周期接入](2026-10-30-hc-lifecycle-event-bus.md)：部分重叠。那篇记录的使用体验目标（消费方不逐一管理生命周期）由 Runtime 模块满足；HcEvent 总线和 `hcEvent` 外观的候选形状不再推进。
- [工厂的链式配置](2026-10-30-store-factory-fluent-configuration.md)：部分重叠。下载设置改为每次 Obtain 现读时，那篇里工厂固定 HttpClient 与 User-Agent 的部分要一起改。
- [Store 消费契约](../../implemented/architecture/2026-10-30-store-consumer-contract.md)：部分重叠。`IContentStore` 的契约不变；Runtime 模块只是它的另一种使用方式。
