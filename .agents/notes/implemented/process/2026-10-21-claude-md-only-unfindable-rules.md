# Agent Note: CLAUDE.md 只写本 repo 里查不到的规则

Status: implemented

## Problem

CLAUDE.md 每个任务都会整份加载，agent 照着它做事，它过期了也没有东西会提醒。如果它抄写本 repo 已有的文件，或者写“只有项目骨架”“尚未决定”这类状态，就会出现两种错：

- 副本和原文走偏。例如构建一节写“测试框架尚未决定，决定后再加入 `tests/`”，测试框架定下之后 agent 仍会以为不该加测试。
- 规则写得不准，agent 照着写错。例如“临时目录持有锁并记录游戏 PID（`.haytham-lock`）”会让 agent 把 PID 写进锁文件；“不加入 GPL-only 的依赖”会让 agent 以为 GPL-3.0-or-later 的依赖可以用，而它同样会挡住日后放宽授权；“旧版在 `legacy` 分支”会让 agent 在本 repo 找一个不存在的分支。

## Decision

判断一行该不该写进 CLAUDE.md，问两件事：

1. 在本 repo 里能不能查到（代码、`.csproj`、`Directory.Build.props`、`global.json`、`.gitignore`、`package.json`、CI、README、笔记与笔记 skill）？
2. 没有这一行，agent 会不会做错？

查得到的只留一句指针，或者不写；查不到、又会让 agent 做错的才写正文。

- **“查得到”只算本 repo 的文件。** 本 repo 公开、会被单独 clone；跨产品的设计文档、ADR 与 Haytham Launcher 的开发者指南都在 Haytham Launcher repo，它在正式版发布前不公开。所以只写在那边的规则（互不引用、CAS 与临时目录、不写 `launcher_profiles.json`、client ID、机密、授权、只用新名称、旧版只当参考、commit 格式、笔记规则）在这里写正文，不改成指向那边的指针。
- **不写状态。** 不写“只有骨架”、进度、“尚未决定”或“等设计时再定”。已经决定的写成规则，还没决定的不写。
- **架构表是代码地图，保留“可依赖”一栏。** `Haytham.Core.Store` 与 `Haytham.Core.Minecraft` 都不引用任何项目；两者互相引用不会形成循环，MSBuild 不会拒绝，只有这一栏写明不允许。Store 单独发布为 NuGet 包，引用了 Minecraft 就会把它一起带给 Gebinde 的转接项目。

### 删掉的内容在哪里

| 删掉的内容 | 在本 repo 的哪里 |
|---|---|
| 产品介绍（CAS 与 GC、版本元数据、Java、加载器……） | `README.md`；“不认识 Manifest、Lockfile 或实例”并进互不引用那一条 |
| 现状：只有项目骨架；启动核心由维护者重新设计 | 删除，不写状态；“不翻译旧版或 ProjBobcat”由惯例的“可参考但不复制……代码要重新写”覆盖 |
| `Minecraft` 与 `Launch` 的切分等设计时再定 | 删除，不写状态；保留“新项目也加 `Haytham.Core.` 前缀” |
| Gebinde CLI 以后才依赖提供服务器 runtime 的包 | 删除：是 Gebinde 那边的计划，不影响本 repo 怎么写 |
| NuGet 包 ID 由项目名生成 | `Directory.Build.props` 的 `PackageId` |
| 授权是 GPL-3.0-or-later | `LICENSE`、`Directory.Build.props` 的 `PackageLicenseExpression` |
| `.claude/`、`.codex/`、`.codei18n/` 与构建产物不要提交 | `.gitignore` |
| 笔记规则的细节（什么算非平凡、`proposed/` 与 `implemented/`、先写被否方案最有力的理由） | `.agents/skills/write-notes-like-deepseek/SKILL.md` |
| Node 22.18 以上 | `package.json` 的 `description` |
| .NET 10 SDK 与版本锁定 | `global.json`、`README.md` |
| `dotnet build HaythamCore.slnx` | `README.md`、`.github/workflows/build.yml` |
| nullable、警告当错误、`IsAotCompatible` | `Directory.Build.props` |
| 测试框架尚未决定 | 删除，不写状态 |

## Alternatives considered

- **只删“现状”一句，其余不动** — 最有力的理由：改动最小，审阅最省事，和 Gebinde 的 CLAUDE.md 也容易保持一致。不采用：构建一节与 `.gitignore` 那一行仍是副本，“等设计时再定”“以后才依赖”同样是会过期的状态，锁文件与授权那两行也会让 agent 写错。
- **只写在开发者指南里的规则也改成指针** — 最有力的理由：规则正文只在开发者指南里有一份，改规则只改一处。不采用：外部贡献者与单独 clone 的 agent 打不开 Haytham Launcher repo，指针对他们等于没写。
- **删掉“可依赖”一栏，依赖方向只看 `.csproj`** — 最有力的理由：Gebinde 的 CLAUDE.md 就这样做，两份文件的结构一致。不采用：Gebinde 的项目之间任何反向引用都会形成循环，MSBuild 会拒绝；本 repo 的 Store 与 Minecraft 之间不会，删掉这一栏就没有任何地方写明它们不能互相引用。

## Consequences

- **收益**：CLAUDE.md 从 4,219 字节降到 3,367 字节，删掉的每一条都能在本 repo 的文件里找到；文件里没有会过期的状态；锁文件、授权与旧版位置三行改成和实际规则一致。
- **代价与已知上限**：与 Haytham Launcher repo 重复的规则有两份，那边改规则时这边要跟着改，没有脚本检查。这里的不变式是压缩版，完整的理由与 C# 注意事项（例如删除只读 hardlink 时不要先清只读属性）在 Haytham Launcher 的开发者指南里，外部贡献者看不到；如果发现 agent 反复踩到某个只写在那边的坑，就把那一条补进这里。

## Verification

- 在 repo 根目录运行 `rg -n "现状|尚未|再定|以后|骨架" CLAUDE.md`，没有命中。
- 删掉的内容逐项能找到：`rg -n "\.claude/" .gitignore`、`rg -n "dotnet build" README.md .github/workflows/build.yml`、`rg -n "PackageId|PackageLicenseExpression|IsAotCompatible" Directory.Build.props`、`rg -n "22.18" package.json`，每条都有命中。
- `npm run verify-notes` 通过。
