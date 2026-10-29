# Agent Note: 修复坏对象时取按 sha1 分片的修复锁

Status: implemented

## Problem

两个调用都把同一个对象判为损坏后，后到者会按过期判断，把先到者刚发布的好对象移到 `tmp/corrupt-*`。先到者随后读取 `FileStamp` 时可能失败；后到者若在隔离后中断，对象路径会暂时缺失。下载之间有下载锁，但直接纳入与下载仍可交错。

[最初的纳入实现](../../implemented/feature/2026-10-25-store-open-import-verify.md)用完整校验加非覆盖移动处理新增对象的竞争，却没有把“校验损坏、隔离、发布”放在同一把锁里。把校验移近改名仍然留下竞争窗口。

## Decision

本篇的修复互斥、分片和锁序仍有效。第二次 Full 校验及按旧写入者隔离提升 v2 的取舍由[专属写入者与显式失败](../simplification/2026-10-29-store-exclusive-writers-and-explicit-failures.md)部分取代，以下说明当前实现。

### 修复路径

- 修复锁是独立的跨进程 `FileLock`，路径为 `locks/repair/<sha1 前 3 位>`，独占。最多 4096 个锁文件，用完 never 删除，避免 POSIX 的路径与 inode 竞争。
- 内容先在锁外写入 `.part`、完整校验并设为只读。仅当发布遇到现存坏对象时，才获取修复锁；正常新增与复用好对象不取这把锁。
- 获取锁后 must 重新做 `VerifyMode.Full`：`Valid` 直接复用，`Corrupt` 才隔离，`Missing` 直接尝试非覆盖发布。所有 `MoveAside` 调用都在这把共同修复锁中。
- 锁覆盖重新哈希、隔离、非覆盖发布、`FileStamp` 读取和验证记录更新。发布阶段是 `ObjectImporter` 的私有实现。
- 隔离到发布之间允许对象缺失。普通纳入不取修复锁，可能在这个窗口抢先发布；项目写入者先校验再发布，修复者直接接受非覆盖碰撞的结果，不再做第二次 Full，也 never 再挪走它。已经隔离过坏对象的一方仍返回 `ReplacedCorrupt`，没有隔离的一方返回 `AlreadyPresent`，锁内发现缺失并独自发布时返回 `Added`。
- 两个修复者基于同一份坏对象进入等待时，只有先到者隔离；后到者拿锁后发现对象已完整，返回 `AlreadyPresent`。自己的 `.part` 由纳入外层 `finally` 清理。

### 锁序与等待

下载先获取下载锁，在修复锁外完成下载、写盘和校验，最后才获取修复锁。修复锁是叶子锁：持有期间 never 联网，也 never 获取下载锁或仓库锁；未来仓库写入会话须在外层建立。占用者记录写 `repair <sha1> (import)` 或 `repair <sha1> (download)`，来源只用于诊断，不改变修复流程。

等待取修复锁的默认上限是 2 分钟，使用现有 `FileLock` 的可取消退避与 `TimeProvider`。超时抛 `LockTimeout` 并描述占用者；取消显式传播，不得无锁降级。`ContentStore.Open` 使用系统时钟，内部组合构造函数可以注入测试时钟；不新增公开配置。

2 分钟是保守的等待策略，不是基准测量结果，也不是持锁执行期限。锁内完整哈希的耗时随文件大小和磁盘 I/O 变化，前缀相同的不同坏对象也会互相等待；不承诺修复或哈希能在 2 分钟内结束。

### 格式

当前无发布版，仓库仍使用 `format=1`、`minWriterFormat=1`，既有 v1 可写。先前考虑用 v2 隔离不认识修复锁的旧写入者，其理由是锁路径属于协议；当前范围只考虑项目协议内写入者，不据此自行推进格式版本。格式步进必须来自用户明确的前代不兼容需求并二次确认，规则见[后续决定](../simplification/2026-10-29-store-exclusive-writers-and-explicit-failures.md)。

`format.json` 只在首次创建时写入，不迁移、降版或改写实际仓库。`minWriterFormat>1` 与文件锁禁用等原有只读条件保留。

### 文件系统错误

非覆盖发布仅把目标已经存在的移动错误当竞争：Windows `ERROR_FILE_EXISTS`（80）或 `ERROR_ALREADY_EXISTS`（183），POSIX `EEXIST`（17）。不再检查目标当下是否存在：移动返回碰撞后，另一修复者可能刚隔离坏目标，后续完整校验的 `Missing` 分支会有限重试发布。捕获范围仅包住 `File.Move`；源文件丢失、目录故障和发布后的 `FileStamp`／records 失败都显式传播。

`MoveAside` 仅把 Windows 的共享／锁冲突（32、33）转换为 `ObjectInUse`。持共同修复锁后，其他正常修复者不可能抢先移走这个目标，所以不再吞掉 `FileNotFoundException`；目录、权限等其他故障也不伪装成占用。

隔离后的发布只考虑正常纳入抢先发布，直接接受该已校验对象；不为协议外写入者或恶意改动增加二次校验和异常分支。

Windows 错误码在本机 .NET 10 实际调用 `File.Move` 核对：目标存在得到 `IOException`／183，源不存在且目标存在得到 `FileNotFoundException`／2。POSIX 的 17 依据 [.NET 10 的错误映射实现](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Common/src/Interop/Unix/Interop.IOErrors.cs)：`EEXIST` 的 `IOException` 保留 raw errno。未在本次本机运行 Linux／macOS 测试。

## Alternatives considered

- **修复时复用下载锁**：少一把锁，下载原本已经持有它。不采用：直接纳入不联网，且下载跨重试与 `Retry-After` 可能长时间占锁，会无端阻塞同前缀的本地修复。
- **把校验移近改名，或再比较文件身份**：改动小。不采用：检查与按路径改名之间始终能被另一方替换，不能消除过期判断。
- **由宿主队列或进程内信息池串行化**：能直接避免同一个任务池中的交错。不采用：其他进程和直接使用 Store 的调用方不经过该队列；冲突键是对象而非宿主会话，共享信息池最终仍须跨进程锁。
- **直接原子覆盖，不保留坏对象**：相同内容的发布互相覆盖无需修复锁。不采用：会丢失坏对象证据，且 Windows 上只读目标或占用句柄会阻碍覆盖。
- **按下载与直接纳入分两种修复状态**：来源清楚。不采用：进入修复锁时内容已经准备好，两种来源执行完全相同的本地操作；来源仅保留在占用者记录中。
- **按旧写入者隔离升级格式**：原先采用 v2 的理由是新锁不约束不认识它的程序，而在线改写格式也无法让正在运行的旧程序停止。该取舍由[专属写入者决定](../simplification/2026-10-29-store-exclusive-writers-and-explicit-failures.md)取代：当前没有发布前代，不为这个假设升级格式。

## Consequences

- 收益：旧的损坏判断不能再隔离别人已经修好的对象，直接纳入与下载共用一个修复协议；发布竞争与真正的文件系统故障分开。
- 代价：坏对象修复在拿锁后完整哈希；分片冲突会等待，慢磁盘或大文件可能导致其他调用等锁超时。
- 中断后允许对象路径缺失；原坏内容保留在 `tmp/`，锁由系统释放，下次纳入或下载重新获取即可恢复。不承诺修复过程中路径一直存在。

### 验证

- 关键回归先红后绿：`dotnet test tests/Haytham.Core.Store.Tests --filter FullyQualifiedName~ObjectRepairTests --nologo`。保持旧发布算法时，外部持有修复锁而纳入已完成，`WaitingAsync` 的 `Assert.Same` 失败；修复后等待能被精确观察并取消。
- `ObjectRepairTests` 使用真实文件锁和独立假时钟，令两个直接纳入或纳入与下载都先判损坏、准备好只读 `.part` 并等待；精确释放指定一方，断言仅隔离一份原坏内容，另一方复用好对象。没有随机压力或 `sleep`。
- 等待期间模拟隔离后中断，分别验证锁内重检 `Missing` 与普通纳入先发布；取消、2 分钟超时、来源诊断、正常纳入不取锁均有覆盖。
- 子进程重新运行单个锁测试，确认跨进程共享同一修复锁；已有 Windows 占用测试继续验证 `ObjectInUse`。此前用于组合发布阶段的测试随着私有阶段接缝收回而删除，范围与理由见[后续决定](../simplification/2026-10-29-store-exclusive-writers-and-explicit-failures.md)。
- 完成检查：Store 测试项目、受影响 C# 文件的格式校验、子模块 `npm run verify-notes`；结果以此次交付报告为准。
