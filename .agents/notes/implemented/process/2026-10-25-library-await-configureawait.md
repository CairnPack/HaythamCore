# Agent Note: 库代码的每个 await 都带 ConfigureAwait(false)

Status: implemented

## Problem

Haytham Core 是给其他启动器引用的库。调用方如果是 WPF、Avalonia 这类带 UI 线程的程序，没有加 `ConfigureAwait(false)` 的 `await` 在完成后会回到 UI 线程继续执行。这在 `Haytham.Core.Store` 里有两个后果：

- `ContentHasher.CopyAndHashAsync` 每读一块（80 KB）就 `await` 一次。哈希几百 MB 的文件时，每块的哈希计算都会排到 UI 线程上，界面会卡顿。
- 调用方如果在 UI 线程上用 `.Result` 或 `.Wait()` 等结果，库里的 `await` 要回到 UI 线程，而 UI 线程正被阻塞，于是死锁。

Haytham Launcher 自己通过原生库调用 Haytham Core，调用线程上没有同步上下文，所以我们自己用的时候碰不到这两个问题，只有别的启动器引用时才会出现。

## Decision

- `src/` 下的库项目里，每一个 `await` 都带 `.ConfigureAwait(false)`，包括 `Task.Delay(…, timeProvider, …)`、`await foreach`（用 `ConfigureAwait` 扩展）和 `await using`。
- `await using` 先把资源声明成一个变量，再对它调用 `ConfigureAwait(false)`：块形式写成 `await using (stream.ConfigureAwait(false)) { … }`，声明形式写成 `await using var _ = stream.ConfigureAwait(false);`。资源的释放时机和原来的 `await using` 相同，仍在所在块或方法结束时。
- `src/.editorconfig` 打开 `dotnet_diagnostic.CA2007.severity = warning`。`Directory.Build.props` 已经设了 `AnalysisLevel=latest` 和 `TreatWarningsAsErrors=true`，所以漏加的 `await` 会让构建失败。
- 测试项目不受这条规则影响：规则写在 `src/.editorconfig`，只作用于 `src/` 下的文件；`tests/` 里的 `FakeHttpHandler` 等辅助类自己已经按习惯加了 `ConfigureAwait(false)`，但测试方法本身没有同步上下文的问题，不强制。

## Alternatives considered

- **只在代码审阅时靠人检查** — 最有力的理由：不用改构建配置。不采用：每个 `await` 都要加，漏一个就会在特定调用方的 UI 线程上出问题，靠人眼盯不住。
- **在 `Directory.Build.props` 里对所有项目打开 CA2007** — 最有力的理由：一处配置、全仓库一致。不采用：测试代码跑在没有同步上下文的测试运行器里，加了只是噪音。
- **用 Fody 一类的织入工具自动补 `ConfigureAwait`** — 最有力的理由：源码里不用写。不采用：多一个构建期依赖，而且 `IsAotCompatible` 之外的织入行为要额外验证；显式写出来更直接。

## Consequences

- 新增的库代码如果漏了 `ConfigureAwait(false)`，构建会报 CA2007。
- 库里的 `await` 之后的代码不能假定自己还在调用方的线程上；需要回到调用方线程的操作（例如更新界面）由调用方自己负责。
- `await using var _ = …` 在同一个方法里每个作用域只能出现一次 `_` 这个名字；同一作用域需要两个资源时改用不同变量名。

## Verification

- 临时去掉 `IdleTimeoutStream.DisposeAsync` 里的一个 `ConfigureAwait(false)`，`dotnet build` 报 `error CA2007`；恢复后构建无警告。
- `dotnet test` 全部通过，`tests/` 构建无 CA2007。
