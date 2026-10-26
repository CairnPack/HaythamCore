# Agent Note: Store 第一批：打开仓库、纳入对象、验证对象

Status: implemented

## Problem

`Haytham.Core.Store` 是安装、启动、GC 共同的底层：它们都要先能打开仓库、把文件放进 `objects/`、判断一个对象好不好。规则散在 Haytham Launcher repo 的开发者指南（§4.1 寻址、§4.2 只读与 `.part`、§4.9 原子写入、§4.10 往安全的方向失败、§4.12 仓库目录）和业务语义工作稿（E1 寻址与自愈、E3 纳入、D6 完好性检查、E10 格式版本）里，还没有任何代码。

本批只做不联网、不加锁就能成立的部分，让后面的下载（E2）、锁、GC、放置有东西可接。

## Decision

### 范围

本批提供：

- `Sha1`：20 字节的值类型，仓库里所有对象的键。解析时大小写都接受，输出一律小写（E1）。
- `ContentStore.Open`：打开或创建仓库，读写 `format.json`（E10）。
- `ImportAsync`／`ImportFileAsync`／`ImportBytesAsync`：流、文件、字节三个入口（E3），可传期望的 sha1、大小、sha512，不符就失败。
- `VerifyAsync`：快速与完整两种模式（D6）。

本批不做：下载 `obtain` 与下载锁（E2，已由[第二批](2026-10-25-store-obtain-and-download-lock.md)接手）、仓库锁、根记录与 GC（E7）、`references/`（E4）、不可再获取标记（E5）、临时目录的锁与清理（E6）、纳入时的克隆与剪切（E3 的 ★ 项）、放置。

### `format.json`

```json
{
  "format": 1,
  "minWriterFormat": 1,
  "storeId": "<UUID>"
}
```

- `format` 是仓库的格式版本，`minWriterFormat` 是能安全写入这个仓库的最低程序格式版本，`storeId` 是仓库身份，第一次创建时生成，之后不变。
- 本程序支持格式 1。`minWriterFormat` 大于 1 时，仓库以只读方式打开：`IsReadOnly` 为真，纳入一律抛 `StoreError.ReadOnly`，也不创建 `objects/`、`tmp/`。这就是 E10 的“遇到比自己新的格式只读，绝不写入”。
- `format.json` 读不懂（不是 JSON、缺字段、`format` 小于 1、`storeId` 为空）时抛 `StoreError.FormatUnreadable`，never 重写它（§4.10）。
- `format.json` 只在创建时写一次，之后不改，所以不需要保留未知字段。

### 打开与创建

- `StoreOpenMode.OpenExisting`（默认）：没有 `format.json` 就抛 `StoreError.StoreMissing`，不创建任何东西。这对应“位置记录指向的目录不存在时 must 报错”；只有调用方确定要创建时才传 `CreateIfMissing`。
- `CreateIfMissing`：目录不存在或为空时创建仓库；目录里已经有别的东西、却没有 `format.json`，抛 `StoreError.NotAStore`，never 把一个普通目录当成仓库接管。
- 创建是原子的：先在仓库根目录写 `.format-<guid>.tmp`，再不覆盖地移成 `format.json`。两个进程同时创建时，后到的一方移动失败，改读先到者写下的文件，两边拿到同一个 `storeId`。判断“目录为空”时只忽略名字完全是 `.format-<32 位小写十六进制>.tmp` 的普通文件，免得后到的一方把先到者的临时文件当成“目录里有别的东西”；同前缀的目录和名字不完全匹配的文件都算已有内容（见[四处小缺陷](../bug-fix/2026-10-27-store-small-fixes.md)）。非空目录里若 `format.json` 已出现，按已有仓库打开。

### 纳入

1. 传了期望的 sha1、而且仓库里这个对象快速验证通过时，不读输入，但要先核对其余期望：大小与文件长度不同，或给了 sha512 而已有对象的 sha512 不同，抛 `StoreError.ContentMismatch`，和仓库为空时同样的输入得到的结果一致；全部相符才返回 `AlreadyPresent`。只给了 sha1 时仍然不读文件内容。只有给了 sha512 才会对已有对象多算一次 SHA-512，因为调用方给它就是为了得到比 SHA-1 更强的校验，已有对象恰好是从只带 sha1 的来源纳入的，也不能让它绕过这项核对。
2. 否则写到 `tmp/object-<guid>.part`，边写边算 sha1（有期望的 sha512 时也算），写完 `Flush(flushToDisk: true)`，免得断电后 `objects/` 里出现长度为 0 的对象。给了期望大小时，复制读到超过它就立即中止并抛 `ContentMismatch`，不把超长或不会结束的响应体写完。
3. 大小、sha1、sha512 任何一项与期望不符，抛 `StoreError.ContentMismatch`，删掉 `.part`。
4. 先把 `.part` 设成只读，再不覆盖地移进 `objects/<前两位>/<sha1>`。对象在出现于 `objects/` 的那一刻就已经是只读的，没有可写的窗口。移入成功后用移入后的文件属性记下验证记录，所以同一进程里对刚纳入的对象做第一次快速验证不必重新哈希（sha1 在写入时已经算过）。
5. 目标已经存在时完整验证它：好的就返回 `AlreadyPresent`；坏的就按下面的“坏对象”处理后重试，最多 3 次。

第 1 步由内部方法 `ObjectImporter.FindExistingAsync` 完成：已有对象全部相符时返回 `AlreadyPresent`，不符时抛 `ContentMismatch`，没有完好的对象时返回空。期望本身的格式（sha512 的长度与字符、大小不能是负数）由 `ObjectImporter.Validate` 检查。下载侧在加锁前后判断“是否已经有了”时复用这两个方法，所以下载与纳入对同样的输入给出同样的回答，规则只在一处（理由见[下载笔记](2026-10-25-store-obtain-and-download-lock.md)）。

`.part` 是本次纳入自己的文件，从没被链接过，所以失败时可以先清掉只读再删除；删不掉就留给以后的临时目录清理（§4.4 的“超过一天的 `object-*.part`”）。

### 坏对象

E1 要求坏对象由下一次纳入原子替换，而且 never 解除只读保护。Windows 上覆盖式移动遇到只读的目标会失败，所以替换分两步：

1. 把坏对象改名移到 `tmp/corrupt-<sha1>-<guid>`。改名不碰文件属性，实例里指向它的只读共享仍然指向同一份（坏的）数据，下一次 install 会按“过期”或“被改动”处理。
2. 再把新的 `.part` 移进 `objects/`。

Windows 上，坏对象若被别的句柄打开而没有共享删除（游戏的 JVM 打开 jar 大概就是这样；实例里的硬链接指向同一个文件，所以被打开也算），改名会失败。这时抛 `StoreError.ObjectInUse`，内部异常保留原始的 `IOException`，调用方（以及把错误翻译成稳定键的原生库）可以等占用方关闭后重试。目标已经不存在则仍视为别的纳入先移走了它，不算错误。

两步之间这个对象暂时不存在，仍满足“对象要么完整、要么不存在”。移走的坏对象本批不删除：删除只读文件的目录项要用 Windows 的 `FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE`（§4.2 的 C# 注意），和临时目录清理一起做。返回值是 `ReplacedCorrupt`。

### 验证

- 文件不存在是 `Missing`；重算的 sha1 与文件名一致是 `Valid`，否则是 `Corrupt`。CAS 对象是自验证的，不另存“对象 → 哈希”的记录（D6）。
- 完整模式一律重算。快速模式对照上次验证通过时记下的大小与 mtime，一致就放行，否则重算。
- 验证记录本批只放在内存里（`VerificationRecords`），进程结束就丢；下一个进程第一次验证会重算。D6 定了“验证记录丢了只是多算一次哈希”，所以这不影响正确性。大小与 mtime 在读文件之前取，读的过程中文件被改，记下的是旧的时间戳，下一次快速验证会发现不一致并重算。
- 快速模式只是性能优化，不是安全保证：大小不变、mtime 被改回原值的篡改，快速模式发现不了。测试明确覆盖了这一点。

### 文件

| 文件 | 职责 |
|---|---|
| `Sha1.cs` | 键的值类型：解析、格式化、相等 |
| `ContentStore.cs` | 公开入口：打开仓库，转交纳入与验证 |
| `ImportTypes.cs` | 纳入的输入与结果：`ExpectedContent`、`ImportOutcome`、`ImportResult` |
| `StoreFormat.cs`、`StoreJsonContext.cs` | `format.json` 的读写；JSON 用 source generator，NativeAOT 下不靠反射 |
| `StoreLayout.cs` | 仓库里各个路径 |
| `ObjectImporter.cs` | 纳入：写 `.part`、核对、发布、替换坏对象 |
| `ObjectVerifier.cs`、`VerificationRecords.cs` | 验证与快速模式的记录 |
| `ContentHasher.cs` | 边复制边算 sha1／sha512 |
| `StoreException.cs` | 带 `StoreError` 的异常，给原生库的错误翻译用 |

## Alternatives considered

- **验证记录落盘（例如新增 `store/verified/`）** — 最有力的理由：进程重开以后也能走快速路径，启动前检查不必每次重算几百 MB 的 classpath。不采用（这一批）：§4.12 的格式 v1 目录里没有这个位置，加目录就是改格式 v1 的定义，要和 Haytham Launcher repo 的开发者指南一起改；而记录丢了只是多算一次哈希。启动核心（T6）决定启动前怎么检查时，再定要不要落盘、放在哪里。
- **替换坏对象时直接用 Windows 的忽略只读删除** — 最有力的理由：一步到位，`tmp/` 里不会留下坏对象。不采用（这一批）：要写 P/Invoke，POSIX 那边也要另外处理，而这些代码和临时目录清理用的是同一套；先把坏对象改名移走，已经满足 E1 的全部要求。
- **纳入时覆盖式移动（`File.Move(..., overwrite: true)`）** — 最有力的理由：一行代码，POSIX 上的 `rename` 本来就是原子覆盖。不采用：Windows 上目标是只读文件时会失败；而且盲目覆盖会把别的进程刚放好的好对象换掉，没有先验证。

## Consequences

- 收益：仓库目录、`format.json`、`.part` 规则、只读与自愈都有了代码和测试，下载、GC、放置可以直接建在 `ContentStore` 上。
- 收益：`StoreError` 让原生库能把错误翻译成稳定的错误键，不必解析异常消息。
- 代价：纳入本身不加锁。两个进程同时纳入同一个对象仍然正确（不覆盖的移动加上完整验证），但会各写一份 `.part`、各算一次哈希；“同一个文件只下载一次”由[第二批](2026-10-25-store-obtain-and-download-lock.md)的下载锁保证，只管下载，不管直接纳入。
- 代价：没有仓库锁，所以本批的纳入还不能和 GC 安全并存；GC 落地时纳入要补上共享锁。
- 代价：`tmp/corrupt-*` 与残留的 `.part` 只增不减，直到临时目录清理落地。
- 代价：纳入文件一律复制，不试克隆，大文件同卷纳入也会多占一份写入时间。
- 测试：`Haytham.Core.Store.Tests` 覆盖 `Sha1` 的解析与格式化；打开时的 `StoreMissing`、`NotAStore`、`FormatUnreadable`、只读格式，以及并发创建拿到同一个 `storeId`；纳入的三个入口、期望不符、已存在时不读输入、对象只读、`tmp/` 不留 `.part`、并发纳入只发布一次；坏对象的替换与移走；快速模式对时间戳的依赖；已有对象要核对期望的大小与 sha512；读到超过期望大小就中止；纳入后第一次快速验证不重算；坏对象被占用时抛 `ObjectInUse`（只在 Windows 上运行）。
