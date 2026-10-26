# Agent Note: 假时钟测试用真实时钟的看门狗，在有限时间内失败

Status: implemented

## Problem

`Haytham.Core.Store.Tests` 里的计时测试用 `TestClock`（继承 `FakeTimeProvider`），按顺序等下一个计时器、断言它的时长、再把时钟推过去。`TestClock.NextTimerAsync` 对“代码没有启动测试预期的计时器”已经有 10 秒的真实时间保护。

但测试推进完预期的计时器之后，会直接 `await` 整个操作。如果实现比测试预期多等了一轮，操作就停在一个没人推进的假时钟上。这时没有任何东西会让它结束，测试一直挂着，直到外层的测试运行器终止整个进程。挂起时没有任何信息说明实现启动了哪些计时器，定位要靠自己加日志。

## Decision

- `TestClock.WithWatchdogAsync` 有 `Task<T>` 与 `Task` 两个重载，等一个操作结束。它用 `TimeProvider.System` 计时，超过 `OperationGuard`（30 秒真实时间）仍未结束就抛 `InvalidOperationException`，测试因此失败。
- 失败信息写出观察到的情况：实现启动过的全部计时器（按顺序的时长），以及测试把假时钟总共推进了多久。`NextTimerAsync` 的 10 秒超时信息也带上同样的内容。
- 操作自己的结果或异常原样传出，所以 `Assert.ThrowsAsync<...>(() => _clock.WithWatchdogAsync(obtain))` 与直接 `Assert.ThrowsAsync<...>(() => obtain)` 断言的内容相同。只有操作本身还没结束时，超时才算看门狗触发。
- 业务逻辑的时间仍然只来自 `FakeTimeProvider`；看门狗是测试代码自己的真实时间，不进入被测代码。
- `ObjectDownloaderTests` 和 `FileLockTests` 里，所有 `await` 一个由 `TestClock` 驱动的操作的地方都经过 `WithWatchdogAsync`，包括预期不会启动任何计时器的测试（如果实现意外启动了计时器，它们同样会挂住）。`Concurrent_obtains_of_one_object_download_it_once` 用真实时钟，不经过它。
- 30 秒只是防挂起用的上限，不是性能断言：推进完预期的计时器之后只剩磁盘与 CPU 工作，正常远小于这个值。

## Alternatives considered

- **用 xUnit 的 `[Fact(Timeout = …)]`** — 最有力的理由：不用改测试方法体，一个特性就能让挂起的测试失败。不采用：它只说“超时了”，不知道实现启动了哪些计时器，定位仍要自己加日志；每个计时测试都要记得加，漏一个就会挂住；超时后被放弃的任务还在后台跑。
- **让 `FakeTimeProvider` 自动推进（`AutoAdvanceAmount`）** — 最有力的理由：计时器自己会触发，永远不会停在没人推进的时钟上。不采用：测试要逐个断言每次等待的时长（1、2、4 秒的退避，50、100、50 毫秒的等锁），自动推进后这些断言就看不到了，多等一轮也不会被发现。
- **只依赖 CI 的整体超时** — 最有力的理由：不用写任何代码。不采用：GitHub Actions 的默认作业超时是 6 小时，而且超时后只留下“被取消”，没有哪个测试、哪些计时器的信息。

## Consequences

- **好处**：多等一轮的实现会让对应测试在 30 秒内失败，失败信息直接列出计时器序列与已推进的时间。
- **副作用**：看门狗触发后，被放弃的操作仍在后台运行，直到它的取消令牌（`TestContext.Current.CancellationToken`）在测试结束时取消。
- **上限**：30 秒是真实时间。在非常慢的 CI 机器上，如果推进完计时器之后的磁盘工作也超过 30 秒，会被误判为挂起；出现这种情况应先查是不是机器的问题，再决定要不要调大 `OperationGuard`，不要为了通过而去掉看门狗。
- **没有验证的部分**：看门狗的失败路径（真的卡住时的失败信息）没有单独的测试，因为要故意制造一个 30 秒才失败的测试；目前只验证了正常路径编译、调用方式不改变原有断言。
