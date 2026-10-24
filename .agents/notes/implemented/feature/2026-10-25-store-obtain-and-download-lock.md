# Agent Note: Store 第二批：下载 obtain 与按 sha1 分片的下载锁

Status: implemented

## Problem

[第一批](2026-10-25-store-open-import-verify.md)只能纳入调用方手里已有的内容。安装与启动要的是“给我这个 sha1 的对象”：仓库里没有就下载。规则来自 Haytham Launcher repo：业务语义工作稿 E2（有 sha1 先查仓库；按对象加跨进程下载锁；同一个文件同时只下载一次，不论来自哪个地址；多个候选 URL 依次尝试；有限次重试，429 遵守 `Retry-After`），开发者指南 §4.11 的 HTTP 规则（User-Agent 由调用方传入；只重试 I/O 与 408／429／5xx，下载对象最多 3 次；限制并发；能换成假的响应），以及锁与并发笔记（下载锁 `store/locks/download/<键的前 3 位>`、独占、排队 15 分钟、退避 50 ms 起翻倍到 1 s、占用者记录 `<锁文件>.holders/`）。

## Decision

### 范围

- `ObjectDownloader.ObtainAsync`：只收有 sha1 的对象。没有 sha1 的下载（按 URL 的 SHA-256 加锁、`references/`）和 E4 一起做；没有 `references/` 的话，每次都得重新下载。
- `FileLock`：独占的跨进程文件锁，带占用者记录。之后的仓库锁（共享／独占）与临时目录锁复用同一套打开方式与占用者记录。
- 打开仓库时检查文件锁是否被关掉（见下）。

### obtain 的流程

1. 仓库只读时抛 `StoreError.ReadOnly`。URL 至少一个，而且都是绝对的 HTTP(S) 地址。
2. 快速验证对象：完好就返回 `AlreadyPresent`，不加锁、不联网。
3. 拿下载锁 `locks/download/<sha1 前 3 位>`，最多排队 `LockTimeout`（默认 15 分钟），可以取消；超时抛 `StoreError.LockTimeout`，消息里列出占用者。
4. 拿到锁后再验证一次：等锁期间，前一个持有者可能刚把它下载好。这一步让“同一个文件只下载一次”在进程内与进程间都成立。
5. 按顺序试每个候选 URL，每个最多 `MaxAttemptsPerUrl` 次（默认 3）。响应体直接流进第一批的纳入流程：先写 `.part`，核对 sha1、大小、sha512，再只读地移入。
6. 全部失败时抛 `StoreError.DownloadFailed`，消息按 URL 列出每一个失败原因。

下载锁在重试与 `Retry-After` 的等待期间一直持有。这是开发者指南 §4.9 规则 2 的例外①：按对象的锁，等它的人本来就要这个对象。

### 什么重试、什么换下一个 URL

| 情况 | 处理 |
|---|---|
| 2xx | 纳入；内容不符就换下一个 URL，不重试（同一个地址的错文件，再下一次还是错的） |
| 408、429、5xx | 重试；有 `Retry-After` 就等它给的时间，没有就从 `RetryDelay`（默认 1 秒）起翻倍 |
| `Retry-After` 超过 `MaxRetryAfter`（默认 60 秒） | 不等，换下一个 URL（例如 Modrinth 限流时改从 CurseForge 下载） |
| 其他状态码（404、403 等） | 换下一个 URL，不重试 |
| `HttpRequestException`、`HttpIOException`、`HttpClient` 自己的超时 | 重试 |
| 写 `.part` 时的普通 `IOException`（例如磁盘满） | 不重试，直接抛出：磁盘满了，重试也还是满的 |
| 调用方取消 | 直接抛出 |

- `UserAgent` 是 `DownloadOptions` 的 `required` 属性，每个请求都带上；库里不写默认值。
- 同一个 `ObjectDownloader` 同时在途的请求不超过 `MaxConcurrentRequests`（默认 8）。重试前的等待不占名额。
- 所有等待（重试、锁的退避）都经构造函数注入的 `delay` 回调，测试不必真的睡。

### 跨进程锁

- 打开方式按锁与并发笔记：独占用 `FileMode.OpenOrCreate`、`FileAccess.ReadWrite`、`FileShare.None`；打开成功就是拿到锁，`Dispose` 就是释放。句柄不可继承，游戏等子进程不会把锁留住。
- 只有“被别人占着”才退避重试：Windows 上是共享冲突（`ERROR_SHARING_VIOLATION`、`ERROR_LOCK_VIOLATION`），POSIX 上是非阻塞 `flock` 返回的 `EWOULDBLOCK`（.NET 把原始 errno 放进 `HResult`：Linux 11、macOS 35）。其他 I/O 错误立刻失败，不会白等到超时。
- 锁文件 never 删除：用完就删，会和已经打开同一路径的等待者竞争（锁与并发笔记的被否方案）。分片让文件数最多 4096 个。
- 同一进程的两个句柄互相冲突，所以进程内与进程间用同一套机制，没有另外的进程内锁。

### 占用者记录

- 拿到锁后，在 `<锁文件>.holders/<pid>-<启动时间的 Unix 毫秒>.json` 原子写入 `{format, program, version, pid, startTime, operation, since}`；释放时先删记录、再放锁，免得有人在锁空出来以后读到它。
- `program` 与 `version` 来自 `ContentStore.Open` 的 `ProgramInfo` 参数，不传时用进程名和 `unknown`。
- 写入时顺手删掉已结束进程的记录。“已结束”按 PID 加启动时间判断（允许 ±2 秒误差）；系统不肯回答（拒绝访问）时当作还活着。
- 记录只用来显示，never 用来判断锁是否被持有。写不进去时不影响拿锁，读不懂的记录跳过。
- 现在的文件名一个进程只有一个；独占锁不会出现同一进程的两条记录。仓库的共享锁落地时，同一进程可能同时有多个会话，文件名要另加区分。

### 文件锁被关掉时仓库只读

POSIX 上，`FileShare` 背后的 `flock` 可以被 AppContext 开关 `System.IO.DisableFileLocking` 或环境变量 `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` 关掉，而且不报错，锁就悄悄失效了。打开仓库时发现这个开关打开，仓库按只读打开，写入抛 `StoreError.ReadOnly` 并说明原因（锁与并发笔记的“打开仓库时的检查”）。Windows 的共享模式由系统强制执行，这个开关不影响它，所以只在 POSIX 上检查。网络文件系统的警告还没做。

### 文件

| 文件 | 职责 |
|---|---|
| `ObjectDownloader.cs` | obtain：查仓库、拿锁、按 URL 重试与换地址 |
| `ObtainTypes.cs` | `DownloadOptions`、`ObtainRequest`、`ObtainResult` |
| `FileLock.cs` | 独占文件锁：打开即持有、退避、超时 |
| `LockHolders.cs` | 占用者记录的写入、删除、清理与描述 |
| `ProgramInfo.cs` | 显示给别人看的程序名称与版本；进程身份（PID＋启动时间）与存活判断 |

## Alternatives considered

- **`Retry-After` 一律照等** — 最有力的理由：完全遵守服务器的要求，对来源最友好。不采用：服务器可能给出一小时，持有下载锁的一方会让同一片的所有下载跟着等；有别的候选 URL 时，换地址比干等好。上限可以由调用方调大。
- **内容不符时也在同一个 URL 重试** — 最有力的理由：可能只是传输中途被截断、CDN 偶尔返回旧文件。不采用：截断表现为连接错误，已经会重试；返回完整但错误的文件，多半是那个地址本身错了，换下一个候选更快。
- **任何 `IOException` 都重试** — 最有力的理由：网络读取的错误有时只是普通的 `IOException`，一律重试最不容易漏。不采用：写 `.part` 的磁盘错误也是 `IOException`，重试只会多等几秒再失败；.NET 8 起网络读取的错误是 `HttpIOException`，可以分开。
- **不做占用者记录，超时只说“等不到锁”** — 最有力的理由：少一套落盘格式，这一批小一些。不采用（2026-10-25 用户确认）：锁与并发笔记要求超时时说明是谁占着；这套记录之后的仓库锁、临时目录锁都要用，现在做好可以直接复用。
- **把没有 sha1 的下载也一起做** — 最有力的理由：Fabric、Quilt 的 Maven 库没有 sha1，启动核心迟早要用。不采用（2026-10-25 用户确认）：要先定 `references/` 的格式、命中后的验证与 GC 时的清理，属于 E4；没有它，每次都得重新下载。

## Consequences

- 收益：安装与启动可以只给 sha1 和候选地址，拿到一个完好的对象；同一个文件在两个程序之间只下载一次。
- 收益：`FileLock` 与占用者记录是之后仓库锁、临时目录锁的现成积木。
- 代价：没有 sha1 的下载还不支持，要等 E4。
- 代价：仍然没有仓库锁，obtain 与 GC 还不能安全并存；GC 落地时要补上写入会话（锁与并发笔记的“仓库锁”）。
- 代价：同一片里不相干的两个下载要排队；同时下载 8 个对象时约 0.7% 的概率（锁与并发笔记的估算）。
- 代价：POSIX 上“被占用”的判断依赖 .NET 把 errno 放进 `HResult`，这是 runtime 源码里的行为，没有正式文档；本机只在 Windows 上跑过测试，Haytham Core 的 CI 目前只在三平台 `dotnet build`，不跑测试。
- 测试：`FileLockTests` 覆盖同一进程的第二个句柄排队、超时并列出占用者，记录只在持有期间存在，已结束进程的记录被清掉、不显示，等待可取消。`ObjectDownloaderTests` 覆盖下载与 User-Agent、已存在时不联网、5xx 翻倍重试、429 遵守 `Retry-After`、超过上限换地址、404 不重试、内容不符换地址、网络错误重试到上限并列出每个原因、等锁超时列出占用者、等锁之后发现对象已在而不再下载、并发 obtain 只下载一次、只读仓库拒绝下载。
- 验证缺口：锁与并发笔记要求的“子进程持锁”契约测试还没有，现在只用同一进程的两个句柄验证；POSIX 上的行为没有实测；`DisableFileLocking` 的只读检查没有测试（Windows 上不生效，在测试进程里打开开关会影响其他测试）。
