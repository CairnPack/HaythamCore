# Agent Note: Store 的消费方契约与默认创建器

Status: implemented

## Problem

外部开发者要阅读 `ContentStore` 与 `ObjectDownloader` 的实现文件，才能拼出 Store 的完整调用方式。前一次目录分类把具体实现和公共数据类型一起放在 `Api/`，仍不能让读者用一个短文件了解可用能力、输入、输出和使用约束。

本篇记录用户已确认的决定：以人类可读性为目的提取 `IContentStore`，提供默认创建器并重新分类源码；现有算法、锁、下载重试、校验、取消与异常行为保持一致。

## Decision

- `Api/IContentStore.cs` 集中声明仓库身份、路径、只读状态、对象纳入、验证与获取，并用英文 XML 文档说明行为和约束。接口不包含打开仓库的静态方法，创建职责独立。
- `Factories/ContentStoreFactory.cs` 提供 `Open` 和 `Create`。前者调用现有 `ContentStore.Open`，后者接收已有的同一个 `ContentStore` 实例；两者按默认组装方式返回 `IContentStore`。工厂接收 `DownloadOptions`，重试、超时与并发额度由 Store 共享；调用方在每次 `ObtainAsync(request, http, userAgent, cancellationToken)` 显式提供借用的 `HttpClient` 和 User-Agent。
- `Implementation/DefaultContentStore.cs` 是内部薄适配器，将仓库方法交给现有 `ContentStore`，将 `ObtainAsync` 交给现有 `ObjectDownloader`。具体类和命名空间保留；工厂与 Obtain 的参数采用每次获取传入网络设置的签名。
- 具体的 `ContentStore` 移入 `Implementation/`；公共请求、结果、枚举和值类型集中到 `Models/`，错误类型集中到 `Errors/`。从 `ContentStore`、`ObjectVerifier` 中搬出公共枚举；从 `ProgramInfo` 中搬出内部 `ProcessIdentity` 到 `Locking/`。这些移动只调整声明位置，不改方法体。
- `Objects/`、`Downloading/`、`Locking/`、`Persistence/` 和 `Helpers/` 继续按既有职责承载实现。每次 Obtain 借用调用方的 HTTP 客户端，不创建隐藏的全局状态，不接管其释放；调用方在组装入口复用同一个仓库和返回的接口实例。

本决定为公开库增加一个以消费方阅读为目的的接口，不要求存在第二套存储实现，也不为内部每个类复制一份接口。Gebinde 仍声明自己的存储契约，由其转接项目接入，双方的产品依赖方向保持一致。

### 既有记录审计

- [按职责分目录](../../implemented/architecture/2026-10-30-store-source-layout.md)的命名空间与内部职责分类继续有效；其中 `Api/` 同时放具体入口和模型的分类由本篇部分取代，两篇互链。
- 纳入与校验、下载锁、坏对象修复和显式失败的笔记负责运行时行为，与本次契约提取无冲突，不取代其决定。
- 仓库没有其他定义 Store 消费方接口或默认工厂的活跃架构笔记。
- [回调与链式配置候选方案](../../proposed/architecture/2026-10-30-store-factory-fluent-configuration.md)讨论额外的创建语法，尚未实现，不改变本篇现有契约与资源所有权。
- [HC 宿主生命周期与事件总线](../../proposed/architecture/2026-10-30-hc-lifecycle-event-bus.md)记录注册后分阶段执行 Store 操作的方向，尚未实现；候选宿主在本篇的直接 API 之上组装，不替换当前契约。

## Alternatives considered

- **只让既有 `ContentStore` 实现基础仓库接口。** 好处是无需新适配器，但下载仍需单独阅读和组装，无法通过一个契约了解当前 Store 模块的全部主要操作，因此统一暴露对象获取，由薄适配器复用两个既有入口。
- **把下载逻辑合入 `ContentStore`。** 好处是具体类本身就实现统一契约，但需要改动现有实现和构造依赖，超出用户要求，因此保留现有两套实现。
- **仅增加使用文档。** 好处是没有新的接口类型，但方法签名和使用约束仍散在实现文件中，不能达到用户希望直接阅读短契约文件的目的。

## Verification

- `Api/` 只包含 `IContentStore.cs`。既有公开类型的命名空间、可见性保持一致；工厂不接收 HTTP 客户端，Obtain 每次接收客户端和 User-Agent。
- 对照改动前保存的 18 个源文件：15 个文件的 SHA-256 完全一致；其余 3 个逐一比较拆出的声明和剩余实现正文，均与原文一致。
- `dotnet build HaythamCore.slnx -c Release` 通过，0 警告、0 错误。
- `dotnet test --solution HaythamCore.slnx -c Release --no-build` 通过，151 项成功、0 失败、0 跳过。原有 143 项保持通过，新增 8 项覆盖默认打开模式、仓库身份、三种纳入入口及校验、下载与纳入共享对象、HTTP 配置与所有权、取消令牌和只读错误传播。
- `dotnet format whitespace src/Haytham.Core.Store/Haytham.Core.Store.csproj --verify-no-changes --no-restore` 和对应的 `tests/Haytham.Core.Store.Tests/Haytham.Core.Store.Tests.csproj` 检查均通过。
- README 提供工厂创建与构造函数注入的说明，迁移涉及的源码链接同步更新。
- `npm run verify-notes` 检查笔记目录、格式和归档记录。

## Consequences

- 收益：消费方只读一份短契约即可了解主要能力，默认工厂封装现有两套入口的组装；内部实现与既有调用方式保持一致。
- 代价：公开契约与具体实现需要同步维护；以接口调用的集成测试检查转发和配置是否接对。
- 所有权：工厂返回的适配器在每次 Obtain 借用 `HttpClient`，调用方必须在本次调用完成前保持其有效并负责最终释放，工厂不提供隐藏的全局客户端或仓库实例。
- 范围：本篇只集中当前已有能力，不增加 GC、会话、Pin 等尚未实现的操作，不把 Gebinde 的存储协议搬进 Haytham Core。
