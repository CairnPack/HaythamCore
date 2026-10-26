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

1. 仓库只读时抛 `StoreError.ReadOnly`。然后校验请求本身：URL 至少一个，而且都是绝对的 HTTP(S) 地址；`Sha512` 如果给了，必须是 128 个十六进制字符；`Size` 如果给了，不能是负数。不合格就抛 `ArgumentException`（见下面的“参数提前校验”）。
2. 检查仓库里是否已有这个对象，规则与纳入时的快速路径相同，由同一个内部方法 `ObjectImporter.FindExistingAsync` 回答：对象完好、而且大小与 sha512（请求给了才核对）都符合期望时，返回 `AlreadyPresent`；对象完好但不符合时，抛 `StoreError.ContentMismatch`，消息与对同样的输入调用纳入时完全相同；两种情况都不加锁、不联网。对象不在或已损坏时继续下一步。
3. 拿下载锁 `locks/download/<sha1 前 3 位>`，最多排队 `LockTimeout`（默认 15 分钟），可以取消；超时抛 `StoreError.LockTimeout`，消息里列出占用者。
4. 拿到锁后按第 2 步再检查一次：等锁期间，前一个持有者可能刚把它下载好。这一步让“同一个文件只下载一次”在进程内与进程间都成立；前一个持有者放进来的对象不符合这次的期望时，同样抛 `ContentMismatch`。
5. 按顺序试每个候选 URL，每个最多 `MaxAttemptsPerUrl` 次（默认 3）。收到 2xx 的响应头后，如果请求给了 `Size`、响应也给了 `Content-Length`，两者不同就不读响应体，直接换下一个 URL。否则响应体包上空闲超时（见下），流进第一批的纳入流程：先写 `.part`，核对 sha1、大小、sha512，再只读地移入。
6. 全部失败时抛 `StoreError.DownloadFailed`，消息按 URL 列出每一个失败原因。

下载锁在重试与 `Retry-After` 的等待期间一直持有。这是开发者指南 §4.9 规则 2 的例外①：按对象的锁，等它的人本来就要这个对象。

### 什么重试、什么换下一个 URL

| 情况 | 处理 |
|---|---|
| 2xx | 纳入；内容不符就换下一个 URL，不重试（同一个地址的错文件，再下一次还是错的） |
| 2xx，但 `Content-Length` 与请求的 `Size` 不同 | 不读响应体，换下一个 URL，不重试（理由同上） |
| 408、429、5xx | 重试；有 `Retry-After` 就等它给的时间，没有就从 `RetryDelay`（默认 1 秒）起翻倍 |
| 下一次重试前要等的时间超过 `MaxRetryAfter`（默认 60 秒），不论来自 `Retry-After` 还是翻倍的退避 | 不等，换下一个 URL（例如 Modrinth 限流时改从 CurseForge 下载）；失败原因写明是哪一种 |
| 其他状态码（404、403 等） | 换下一个 URL，不重试 |
| `HttpRequestException`、`HttpIOException`（包括读响应体中途出错）、`HttpClient` 自己的超时 | 重试 |
| 响应体超过 `ReadIdleTimeout`（默认 30 秒）没有新数据 | 按网络错误重试，计入这个 URL 的尝试次数 |
| 写 `.part` 时的普通 `IOException`（例如磁盘满） | 不重试，直接抛出：磁盘满了，重试也还是满的 |
| 调用方取消 | 直接抛出 |

- `UserAgent` 是 `DownloadOptions` 的 `required` 属性，每个请求都带上；库里不写默认值。
- 同一个 `ObjectDownloader` 同时在途的请求不超过 `MaxConcurrentRequests`（默认 8）。重试前的等待不占名额。
- 所有等待与计时（重试的退避、`Retry-After`、排队等锁、空闲超时）都用构造函数注入的 `TimeProvider`，见下面的“时钟”。

### 响应体的空闲超时

请求用 `HttpCompletionOption.ResponseHeadersRead`，响应体边读边写进 `.part`，不先缓冲在内存里。代价是 `HttpClient.Timeout` 只管到收到响应头为止：服务器发完响应头就不再发数据、连接又没断时，读响应体会一直等下去，并且一直占着这一片的下载锁和一个请求名额。

- 下载层用 `IdleTimeoutStream` 包住响应流。它每次 `ReadAsync` 都在注入的 `TimeProvider` 上新建一个 `ReadIdleTimeout` 后触发的计时器（`new CancellationTokenSource(ReadIdleTimeout, timeProvider)`），再和调用方的令牌联动成这一次读用的令牌；读到数据就作废，下一次读再重新计时。所以“空闲”指的是一次读等了多久，慢但一直有数据的下载 never 被掐断，两次读之间算哈希、写磁盘的时间也不算进去。
- 计时器触发、调用方没有取消时，抛 `TimeoutException`（消息是 “No data received for …s.”），`IsTransient` 把它当网络错误重试。判断看的是计时器和调用方令牌的状态，不看异常类型：取消一次读，底层可能抛 `OperationCanceledException`，也可能因为连接被中止而抛 `IOException`。
- 调用方自己取消时，即使计时器也同时触发，也照常抛 `OperationCanceledException`，不重试。
- 只有异步读计时；同步的 `Read` 直接转给内层流。`ContentHasher` 只用异步读。
- 默认 30 秒。正常的连接在 30 秒里总会送来数据，连 TCP 重传也会在这段时间里发生几次；而一次停住最多白占锁约 30 秒乘以尝试次数（默认 3 次，再加上 1 秒、2 秒的退避），比别人等锁的上限 `LockTimeout`（15 分钟）小得多。`Timeout.InfiniteTimeSpan` 表示不限，这时不建计时器，读直接转给内层流。
- 超时放在下载层，不放进 `ContentHasher` 的读循环：那个循环也用于从本地文件纳入，那里没有“空闲”可言；读循环的大小上限（复制时超过期望大小就中止）也在那里，由纳入那边负责。

### 等待的上限与退避

- 状态码分支和网络错误分支用同一条规则：先看是否已经用完 `MaxAttemptsPerUrl`；没用完时，下一次要等的时间超过 `MaxRetryAfter` 就放弃这个 URL，失败原因写成 “HTTP 503, backoff 80s is above the 60s limit” 或 “HTTP 429, Retry-After 3600s is above the 60s limit”，只有服务器真的给了 `Retry-After` 时才出现这个词。
- 退避从 `RetryDelay` 起逐次加倍，加到 `TimeSpan.MaxValue` 就停住，不会溢出。只靠上面的上限不够：服务器连续回 `Retry-After: 0` 时尝试次数可以涨到很大，下一次没有 `Retry-After` 的退避仍要按这个次数算。

### 时钟：注入 `TimeProvider`

Store 里凡是“现在几点”和“过了多久”，都问同一个 .NET 自带的 `TimeProvider`，它由 `ObjectDownloader` 的构造函数接收，再往下传：

| 用到的地方 | 怎么用 |
|---|---|
| 重试前的等待（退避与 `Retry-After`） | `Task.Delay(wait, timeProvider, cancellationToken)` |
| `Retry-After` 给的是日期 | 用 `GetUtcNow()` 算还要等多久 |
| `FileLock.AcquireExclusiveAsync`（静态方法，`TimeProvider` 作为参数传入） | 用 `GetTimestamp`／`GetElapsedTime` 算已经排队多久，退避同样用 `Task.Delay(…, timeProvider, …)` |
| 占用者记录的 `since` | `FileLock` 用 `GetUtcNow()` 取值，作为参数传给 `LockHolders.Register` |
| `IdleTimeoutStream` | 见上面的“响应体的空闲超时” |

- 公开构造函数有两个：原有的三参数版本 `ObjectDownloader(ContentStore, HttpClient, DownloadOptions)` 用 `TimeProvider.System`；新增的重载 `ObjectDownloader(ContentStore, HttpClient, DownloadOptions, TimeProvider)` 接收调用方的时钟，传 `null` 抛 `ArgumentNullException`。原来 internal 的、收 `delay` 回调的构造函数删掉了。
- 用重载而不是给原有签名加一个可选参数：Store 作为独立的 NuGet 包发布，加可选参数等于换掉原来的方法签名，按旧版本编译的调用方升级后会在运行时找不到构造函数；重载让源代码和已编译的调用都不受影响。
- 不经过 `TimeProvider` 的时间：`ProcessIdentity` 读的是进程的启动时间，验证记录的 `FileStamp` 读的是文件的修改时间。它们是系统记下的时间点，要和进程表、文件系统里的值比较，不是“现在”，所以 never 换成注入的时钟。`ContentStore`、`ObjectImporter`、`ObjectVerifier` 不读时钟。`HttpClient.Timeout` 由调用方传入的 `HttpClient` 自己计时，不归 Store 管。
- 构造函数注入的其余结构不变：`ContentStore` 仍然自己创建 `ObjectVerifier` 与 `ObjectImporter`。它们只在仓库内部组合，调用方没有替换它们的需要。

为什么要注入时钟，而不是只注入“等待”：原来的 `delay` 回调只能替换“等”这个动作，`FileLock` 用 `Stopwatch` 算“已经等了多久”、`Retry-After` 的日期用 `DateTimeOffset.UtcNow`、空闲超时用 `CancelAfter` 的系统计时器，这三处测试都控制不了，等锁超时与空闲超时的测试只能真的等。`TimeProvider` 同时提供计时器、时间戳和当前时间，测试换成假时钟后，“等了多久”和“等多久”由同一个时钟决定，测试不再依赖真实时间。

测试用 `Microsoft.Extensions.TimeProvider.Testing` 的 `FakeTimeProvider`（MIT，只在测试项目引用，登记在仓库根目录的 `THIRD_PARTY_NOTICES.md`）。测试里的 `TestClock` 继承它，每启动一个计时器就报告它的时长；测试按顺序等下一个计时器、核对时长、把时钟推过去，所以每次等待的时长都被断言，而且不推进就不会触发。等整个操作结束时，测试经过 `WithWatchdogAsync`，用真实时钟给它一个上限。`ObjectDownloaderTests` 默认关掉空闲超时，这样下载器启动的计时器都是等待；测空闲超时的几个测试再单独打开。“并发 obtain 只下载一次”这一个测试仍用真实时钟：三个等锁的调用在持有者写盘期间要重试几次取决于机器快慢，没有固定的计时器顺序可以一步步推进。

### 参数提前校验

参数错误要在拿锁、联网之前报出来，否则要等某个 URL 回 200 才暴露；所有 URL 都 404 时会被 `DownloadFailed` 掩盖，对象已经在仓库里时完全不报。

- 构造函数：`UserAgent` 用 `request.Headers.UserAgent.TryParseAdd`（`HttpHeaderValueCollection<T>.TryParseAdd`） 试解析一次（例如 `name/1.0 contact@x.com` 不合法，联系方式要放进括号）；`RetryDelay`、`MaxRetryAfter` 不能是负数；`MaxRetryAfter` 与 `ReadIdleTimeout` 不能超过 `int.MaxValue` 毫秒（约 24.8 天），这个值在 `Task.Delay(TimeSpan, TimeProvider)` 与带 `TimeProvider` 的 `CancellationTokenSource` 能接受的计时长度以内；`ReadIdleTimeout` 还必须大于零，或者是 `Timeout.InfiniteTimeSpan`。
- `ObtainAsync`：见流程第 1 步。`Sha512` 与 `Size` 的检查和纳入时用的是同一个函数 `ObjectImporter.Validate`（SHA-512 长度 128，大小写都接受；大小不能是负数），所以两边的规则和错误消息不会分开演变。

### 已有对象不符合期望时为什么报错，而不是重新下载

仓库以 sha1 为键，`objects/` 下同一个 sha1 只能放一份内容。已有对象的 sha1 验证通过，却和请求的大小或 sha512 对不上，说明调用方给的元数据和这个 sha1 互相矛盾（或者真的遇到了 SHA-1 碰撞）。重新下载帮不上忙：下载回来的内容进入纳入流程时，会在同一个快速路径上遇到同一个已有对象，再抛一次 `ContentMismatch`；而 `TryUrlAsync` 会把它当成“这个地址的文件不对”换下一个 URL，最后变成一条误导人的 `DownloadFailed`，还白发了请求。所以在拿锁、联网之前就报出和纳入一致的错误。

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
| `IdleTimeoutStream.cs` | 包住响应流：每次读超过 `ReadIdleTimeout` 没有数据就抛 `TimeoutException` |
| `FileLock.cs` | 独占文件锁：打开即持有、退避、超时 |
| `LockHolders.cs` | 占用者记录的写入、删除、清理与描述 |
| `ProgramInfo.cs` | 显示给别人看的程序名称与版本；进程身份（PID＋启动时间）与存活判断 |

## Alternatives considered

- **`Retry-After` 一律照等** — 最有力的理由：完全遵守服务器的要求，对来源最友好。不采用：服务器可能给出一小时，持有下载锁的一方会让同一片的所有下载跟着等；有别的候选 URL 时，换地址比干等好。上限可以由调用方调大。
- **内容不符时也在同一个 URL 重试** — 最有力的理由：可能只是传输中途被截断、CDN 偶尔返回旧文件。不采用：截断表现为连接错误，已经会重试；返回完整但错误的文件，多半是那个地址本身错了，换下一个候选更快。
- **任何 `IOException` 都重试** — 最有力的理由：网络读取的错误有时只是普通的 `IOException`，一律重试最不容易漏。不采用：写 `.part` 的磁盘错误也是 `IOException`，重试只会多等几秒再失败；.NET 8 起网络读取的错误是 `HttpIOException`，可以分开。
- **不做占用者记录，超时只说“等不到锁”** — 最有力的理由：少一套落盘格式，这一批小一些。不采用（2026-10-25 用户确认）：锁与并发笔记要求超时时说明是谁占着；这套记录之后的仓库锁、临时目录锁都要用，现在做好可以直接复用。
- **用 `HttpClient.Timeout` 管住整个下载（`ResponseContentRead`，或者给整个响应体设一个总时限）** — 最有力的理由：不用另写流包装，一个参数就能限制占锁的总时间。不采用：`ResponseContentRead` 会把整个文件先缓冲进内存；总时限会掐断又大又慢、但一直有数据的正常下载。空闲超时只抓“停住”这一种情况。
- **把空闲超时写进 `ContentHasher` 的读循环** — 最有力的理由：读循环本来就在逐块读，在那里重置计时器最直接，不用多一个类。不采用：那个循环也用于从本地文件纳入，没有网络就没有空闲超时可言；放在下载层，纳入那边的代码不必知道网络。
- **退避超过上限时截到 `MaxRetryAfter`，继续重试** — 最有力的理由：调用方调大 `MaxAttemptsPerUrl` 就是想多试几次，截断能让每一次都真的发生。不采用：等待期间一直占着下载锁，64 次尝试会让同一片的其他下载跟着等上一个小时；还有别的候选 URL 时，换地址更好。而且状态码分支原本就是“超过上限就换地址”，两个分支用同一条规则更容易讲清楚。
- **已有对象不符合期望时当作“没有”，照常加锁下载** — 最有力的理由：下载侧只需要一个“是否已经满足”的布尔判断（纳入侧原本为此提供了 `SatisfiesAsync`），流程最简单。不采用：同一个 sha1 下载不来别的内容，纳入时还会撞上同一个已有对象，结果是白发请求，最后报一条误导人的 `DownloadFailed`；而对同样的输入调用纳入得到的是 `ContentMismatch`，两个入口的回答不一致。所以把纳入的快速路径整个抽成 `FindExistingAsync`，返回“已有／没有”，不符时直接抛错，取代了 `SatisfiesAsync`。
- **保留 `delay` 回调，空闲超时另想办法** — 最有力的理由：改动小，不加任何依赖，已有的测试写法不用变。不采用：回调只能替换“等”，控制不了“已经过了多久”（`FileLock` 的 `Stopwatch`、`Retry-After` 日期的 `UtcNow`）和 `CancellationTokenSource` 的计时器，等锁超时与空闲超时的测试仍要真的等；每多一种计时就要多一个回调。`TimeProvider` 是 .NET 自带的类型，一个参数就把这几样都覆盖了。
- **给原有的公开构造函数加一个可选的 `TimeProvider? timeProvider = null` 参数** — 最有力的理由：只有一个构造函数，调用方的源代码不用改，看签名就知道可以传时钟。不采用：可选参数会换掉原来的方法签名，按旧版本编译的程序升级 Store 包后会找不到构造函数；`null` 表示“用系统时钟”也让参数多了一层含义。改用重载，旧签名原样保留。
- **把没有 sha1 的下载也一起做** — 最有力的理由：Fabric、Quilt 的 Maven 库没有 sha1，启动核心迟早要用。不采用（2026-10-25 用户确认）：要先定 `references/` 的格式、命中后的验证与 GC 时的清理，属于 E4；没有它，每次都得重新下载。

## Consequences

- 收益：安装与启动可以只给 sha1 和候选地址，拿到一个完好的对象；同一个文件在两个程序之间只下载一次。
- 收益：`FileLock` 与占用者记录是之后仓库锁、临时目录锁的现成积木。
- 代价：没有 sha1 的下载还不支持，要等 E4。
- 代价：仍然没有仓库锁，obtain 与 GC 还不能安全并存；GC 落地时要补上写入会话（锁与并发笔记的“仓库锁”）。
- 代价：同一片里不相干的两个下载要排队；同时下载 8 个对象时约 0.7% 的概率（锁与并发笔记的估算）。
- 收益：除了并发下载的那一个测试，计时相关的测试都推进假时钟，不再真的等待；等锁的退避序列（50、100、50 ms）、空闲超时、`Retry-After` 日期这些以前没法精确断言的时长，现在都直接断言。
- 代价：测试项目多了一个包 `Microsoft.Extensions.TimeProvider.Testing`（只用于测试，不随 Store 包分发）。用假时钟的测试要按顺序写出每一次等待；代码多等了一次而测试没有推进时钟，操作会停在没人推进的假时钟上；测试经过 `TestClock.WithWatchdogAsync` 等整个操作，30 秒真实时间后失败，并在信息里列出观察到的计时器序列（少等了一次时，`NextTimerAsync` 会在 10 秒后报错，信息同样列出计时器序列）。见[假时钟的看门狗](../testing/2026-10-27-fake-clock-watchdog.md)。
- 代价：POSIX 上“被占用”的判断依赖 .NET 把 errno 放进 `HResult`，这是 runtime 源码里的行为，没有正式文档；本机只在 Windows 上跑过测试；Haytham Core 的 CI 在三平台 `dotnet build` 之后运行 `dotnet test`，见[子模块 CI 运行测试](../process/2026-10-27-ci-runs-tests.md)，但 Linux 与 macOS 的结果在写这条时还没有看到。
- 测试：`FileLockTests` 覆盖同一进程的第二个句柄排队、按 50、100、50 ms 退避后超时并列出占用者（记录的 `since` 来自注入的时钟），记录只在持有期间存在，已结束进程的记录被清掉、不显示，等待可取消。`ObjectDownloaderTests` 覆盖下载与 User-Agent、已存在时不联网、5xx 翻倍重试、429 遵守 `Retry-After`（秒数与日期两种，日期按注入时钟的“现在”计算）、超过上限换地址、404 不重试、内容不符换地址、网络错误重试到上限并列出每个原因、等锁超时列出占用者、等锁之后发现对象已在而不再下载、已有对象的大小或 sha512 不符时抛出与纳入相同的 `ContentMismatch` 且不发请求（加锁前与等锁之后两处）、已有对象全部相符时返回 `AlreadyPresent`、并发 obtain 只下载一次、只读仓库拒绝下载；还覆盖响应体停住时超时并重试、一直停住时失败原因写明空闲超时、停住时调用方取消照常抛出且不重试、读响应体中途出错时重试、不合法的 User-Agent 在构造时报错、不合法的 SHA-512 与负的大小在任何请求之前报错（对象已在仓库时也报）、退避超过上限时换地址且不说成 `Retry-After`（状态码与网络错误两个分支）、64 次尝试不溢出、`Content-Length` 与期望大小不同时不读响应体就换地址。
- 验证缺口：空闲超时只用测试里的假流验证过；真实的 `SocketsHttpHandler` 在读被取消时抛什么异常没有实测，代码按计时器状态判断，就是为了不依赖这一点。
- 验证缺口：锁与并发笔记要求的“子进程持锁”契约测试还没有，现在只用同一进程的两个句柄验证；POSIX 上的行为没有实测；`DisableFileLocking` 的只读检查没有测试（Windows 上不生效，在测试进程里打开开关会影响其他测试）。
