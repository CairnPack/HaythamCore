# Agent Note: Store 源码按职责分目录

Status: implemented

## Problem

`Haytham.Core.Store` 原先把 18 个 C# 文件平铺在项目根目录，仓库入口、对象操作、下载、锁和落盘格式混在一起。查找某项实现需要先逐个判断文件名，新增文件也缺少明确的归属。

## Decision

本篇记录按职责分目录的决定。`Api/` 同时容纳具体入口和公共模型的分类由[消费方契约与默认创建器](2026-10-30-store-consumer-contract.md)部分取代；其余内部职责分类与保留命名空间的理由继续有效。

当前目录如下，项目文件留在根目录：

| 目录 | 文件 | 职责 |
| --- | --- | --- |
| `Api/` | `IContentStore.cs` | 集中呈现消费方能力与使用约束的接口契约 |
| `Factories/` | `ContentStoreFactory.cs` | 默认实现的创建与组装 |
| `Implementation/` | `ContentStore.cs`、`DefaultContentStore.cs` | 既有仓库入口与统一契约的薄适配器 |
| `Models/` | `ImportTypes.cs`、`ObtainTypes.cs`、`ProgramInfo.cs`、`Sha1.cs`、`StoreOpenMode.cs`、`VerificationTypes.cs` | 公共请求、结果、枚举与值类型 |
| `Errors/` | `StoreException.cs` | 公共错误分类与异常类型 |
| `Objects/` | `ObjectImporter.cs`、`ObjectVerifier.cs`、`VerificationRecords.cs` | 对象纳入、校验和内存中的校验记录 |
| `Downloading/` | `ObjectDownloader.cs`、`IdleTimeoutStream.cs` | 下载编排与响应体空闲超时 |
| `Locking/` | `FileLock.cs`、`LockHolders.cs`、`ProcessIdentity.cs` | 跨进程文件锁、占用者记录与进程身份 |
| `Persistence/` | `StoreLayout.cs`、`StoreFormat.cs`、`StoreJsonContext.cs` | 仓库路径、格式文件与 JSON 序列化 |
| `Helpers/` | `CallerCancellation.cs`、`ContentHasher.cs` | 跨对象与下载流程复用的取消识别、流复制与哈希 |

目录用于源码导航，现有类型继续使用 `Haytham.Core.Store` 命名空间，访问级别与实现方法体保持一致。SDK 项目递归纳入源码，无需手写文件列表。`Helpers` 只放有具体主题的共用机制；不再并列建立含义重叠的 `Utils`。下载专用的 `IdleTimeoutStream` 跟随下载实现。

已有笔记审计：纳入与校验、下载锁、坏对象修复以及显式失败的笔记负责运行时行为，职责分类不取代其决定。消费方契约的提取理由由对应笔记维护。

## Alternatives considered

- **目录与命名空间同步分层。** 好处是二者一致，IDE 可以按目录推导命名空间。本次只是整理源码，同步更改公开类型的命名空间会迫使调用方修改引用，并改变类型身份，因此保留原命名空间。
- **同时建立 `Helpers` 和 `Utils`。** 好处是沿用常见的工具分类名称，但当前共用机制只有取消识别与内容哈希，两类名称没有可操作的区分标准，因此只保留 `Helpers`，具体文件仍按主题命名。

## Verification

- 项目根目录没有剩余的手写 C# 文件，迁移影响的文档链接同步更新。
- 当前的声明拆分、既有实现保持检查、构建与测试结果见[消费方契约的验证](2026-10-30-store-consumer-contract.md#verification)。
- 最初全解决方案的格式检查报告未改动的 `tests/Haytham.Core.Testing.Tests/FakeHttpHandlerTests.cs` 存在 CRLF 换行问题；其内容继续保留，Store 及其测试项目的格式检查通过。
- `npm run verify-notes` 校验目录、格式与归档记录。

## Consequences

- 收益：仓库入口、对象操作、下载、锁和落盘机制有明确的源码位置，新增文件按同一职责归类。
- 代价：源码路径变化会影响指向旧文件位置的文档和 IDE 书签；仓库内链接随迁移更新，外部书签需要重新定位。
- 边界：目录分类不等同于类型可见性，例如下载实现仍包含公开的 `ObjectDownloader`；公开命名空间和运行时行为保持一致。
