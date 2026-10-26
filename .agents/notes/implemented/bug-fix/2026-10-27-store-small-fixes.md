# Agent Note: Store 的四处小缺陷：空目录判断、并发创建、占用者记录

Status: implemented

## Problem

对 `Haytham.Core.Store` 的审查找出四处问题，它们都出在“外部环境”一侧：用户选的目录、同时运行的另一个程序、文件系统的故障。按 Haytham Launcher 开发者指南 §13（错误与兜底），外部环境的故障要处理，并且让失败能被看到，不能悄悄吞掉。

1. 判断“目录为空”时，忽略所有以 `.format-` 开头的条目，包括普通目录和任意文件。用户把仓库建在一个放着 `.format-personal/` 或 `.format-notes` 的目录里时，这个目录会被当成空目录接管。
2. 两个程序第一次同时创建仓库时，后到者先看到 `format.json` 不存在，随后先到者写完了 `format.json`，后到者接着做的“目录非空”检查就抛 `NotAStore`。创建 `format.json` 这一步的竞争已经处理过，前面的检查没有。
3. 拿锁超时时，消息里要列出占用者。读单条记录有保护，但列目录本身和迭代过程没有，目录消失、权限变化或枚举出错时，异常会从 `LockHolders.Describe` 逸出，顶替本该抛出的 `StoreError.LockTimeout`。
4. 写占用者记录时先写临时文件再改名。序列化或改名失败后，`catch` 返回 `null`，临时文件留在 `.holders/` 里；之后只扫描 `*.json`，这个文件没有任何人再管。

## Decision

### 空目录的判断

- `ContentStore` 判断目录是否为空时，只忽略名字完全是 `.format-<32 位小写十六进制>.tmp` 的普通文件，也就是 `StoreLayout.NewFormatTempPath` 生成的那种名字。判断放在 `StoreLayout.IsFormatTempName`，格式与生成它的地方放在同一个类里。
- 同前缀的目录、名字不完全匹配的文件（缺后缀、长度不对、大写字母、多余的后缀）都算已有内容，抛 `StoreError.NotAStore`。
- 是否普通文件，由 `DirectoryInfo.EnumerateFileSystemInfos` 返回的 `FileInfo`／`DirectoryInfo` 类型判断，不再对每个条目另外调用 `File.Exists`。这样先到者在枚举与检查之间把临时文件改名走，也不会让后到者误判。

### 并发创建

- `format.json` 不存在、并且允许创建时，`ContentStore.Open` 调用 `ContentStore.CreateOrJoin`。它先判断目录是否有别的内容；有的话，抛 `NotAStore` 之前再看一次 `format.json`，存在就走和已有仓库相同的读取路径（`StoreFormat.Read`，格式损坏照常抛 `FormatUnreadable`，从不覆盖）；没有才抛 `NotAStore`。目录为空时照旧创建。
- `CreateOrJoin` 是 `internal`，这样测试可以直接构造“第一次检查已经过时”的状态，不需要在 `Open` 里加钩子或委托，也不加任何公开接口。
- `Open` 与 `CreateOrJoin` 读已有仓库用的是同一个 `StoreFormat.Read`；读出的格式文件再交给同一个 `ContentStore` 构造函数，只读判断（较新的写入格式、文件锁被关掉）也在那里做，所以两条路径没有各自的一份读取逻辑。
- 对应的测试模拟的是交错的结果，不是交错本身：它直接构造“`format.json` 已经出现”的目录再调用 `CreateOrJoin`，没有让两个创建者真的交错。

### 占用者的显示

- `LockHolders.Describe` 不再让文件系统异常逸出。列目录或迭代中出现 `IOException`、`UnauthorizedAccessException` 时，已读到的活着的占用者照常显示；一个都没有就显示 `an unknown holder`；两种情况都在后面加一段 `(the holder records could not be fully listed: <异常类型>: <消息第一行>)`。这段文字进入 `LockTimeout` 的消息，是库里唯一的诊断出口：库目前没有日志设施。
- 主错误 `LockTimeout` 不受影响：`FileLock.AcquireExclusiveAsync` 拼消息时调用的仍是 `Describe`，它不再抛出。
- `Describe` 有一个 `internal` 的可选参数 `listRecords`，只有测试用它换掉“列目录”这一步，以便在不依赖操作系统权限的情况下确定性地制造枚举失败。它不是公开接口。
- 目录不存在不算故障：它只表示没有人登记过，仍显示 `an unknown holder`，不带失败说明。

### 临时文件的清理

- `LockHolders.Register` 在 `finally` 里尝试删除本次调用创建的临时文件，写成功并改名后文件已不存在，删除不起作用。清理失败（`IOException`、`UnauthorizedAccessException`）被吞掉，不会让拿锁失败；这是已有约定“记录写不进去不影响拿锁”的一部分。
- 只删本次创建的那一个文件，不扫描删除 `.holders/` 里别的未知文件，也不清理别的进程的临时文件。
- 清理失败时没有任何提示。这与 `ObjectImporter.DeletePart` 一致；它只会留下一个无害的小文件，不影响锁，也没有别处可读的诊断出口。

## Alternatives considered

- **用名字前缀 `.format-` 匹配所有条目，只额外要求是文件** — 改动最小。不采用，因为 `.format-notes` 这样的普通文件也会被当成临时文件而忽略。
- **用 `File.Exists` 判断是否普通文件** — 写法简单。不采用，因为它与枚举之间有空档：临时文件被先到者改名走，条目会被当成“别的内容”。用枚举返回的类型没有这个空档。
- **并发创建时在 `Open` 里重试整个流程** — 能覆盖更多交错。不采用，因为只有“抛错之前再看一次 `format.json`”这一处有问题，多看一次就够了，重试会让其余路径也变得难以推理。
- **给 `Open` 加测试钩子来注入交错** — 能覆盖第一次检查本身。不采用，因为要在生产代码里加只为测试存在的委托；拆出 `internal` 方法已经能让测试确定性地覆盖修复的那一步。
- **枚举失败时让异常照常抛出** — 失败能立刻被看到。不采用，因为占用者只是附加显示，丢掉 `LockTimeout` 会让调用方分不清“等锁超时”和“文件系统坏了”。
- **启动时扫描并删除 `.holders/` 里所有未知文件** — 能清掉历史遗留。不采用：占用者记录目录里只有我们自己写的文件，但扫描删除会扩大清理的范围，别的进程正在写的临时文件也可能被误删。

## Consequences

- **好处**：用户目录里恰好有 `.format-` 开头的内容时不会被当成空目录；并发创建的两个程序不再因为窗口很窄的交错而失败；拿锁超时的错误种类稳定，文件系统故障会写进消息里；占用者记录目录不再积累孤立的临时文件。
- **副作用**：`.format-<32 位十六进制>.tmp` 只在小写时被忽略。`NewFormatTempPath` 只生成小写，所以正常情况不受影响；别的程序留下的大写同形文件会被当成内容。
- **已知上限**：`Open` 的第一次检查本身没有测试覆盖，只测了“第一次检查之后”的那一段；这是为了不在生产代码里放测试钩子。占用者记录的临时文件清理失败时没有任何提示。

## Verification

- `tests/Haytham.Core.Store.Tests/StoreOpenTests.cs`：`.format-` 开头的条目哪些被拒绝、哪些被忽略；`CreateOrJoin` 对另一方写好的 `format.json`（格式 2、指定的 `storeId`）读出同一份内容且不改写文件，对损坏的 `format.json` 仍抛 `FormatUnreadable`，对没有 `format.json` 的非空目录仍抛 `NotAStore`。
- `tests/Haytham.Core.Store.Tests/FileLockTests.cs`：占用者目录被删后超时仍是 `LockTimeout`；列目录失败时 `Describe` 返回“未知占用者”加失败原因，迭代中途失败保留已读到的占用者；记录的目标名被目录占住时 `Register` 返回 `null`、没有遗留临时文件、拿锁照常成功。
- 运行 `dotnet test` 时看上面两个文件。
