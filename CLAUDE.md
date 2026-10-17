# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目

**Haytham Core** 是团队 CairnPack 的可复用 Minecraft 启动器核心（C#，GPL-3.0-or-later）：CAS 仓库与 GC、版本元数据、Java、加载器安装、服务器 runtime、账号、游戏进程。它不认识 Manifest、Lockfile 或实例，启动入口只收“游戏目录＋版本＋加载器”。

另外两个产品：整合包工具链 **Gebinde**（`CairnPack/Gebinde`）与桌面启动器 **Haytham Launcher**（以子模块引用本 repo）。跨产品的设计文档、ADR 与名词表目前放在 Haytham Launcher repo。

**现状**：只有项目骨架，还没有代码。启动核心由维护者重新设计，不翻译旧版或 ProjBobcat。

## 架构

| 项目 | 内容 | 可依赖 |
|---|---|---|
| `Haytham.Store` | CAS、下载、文件链接、临时目录；GC（根集合由外部提供） | 无 |
| `Haytham.Minecraft` | 启动核心的纯领域：版本解析、规则、参数、natives、崩溃诊断；不碰磁盘与网络 | 无 |
| `Haytham.Launch` | 启动核心的 I/O：版本元数据、Java、加载器安装、服务器 runtime、账号、游戏进程 | Minecraft、Store |

- **Haytham Core 与 Gebinde 互不引用。** `Haytham.*` never 引用 `Gebinde.*`。
- **全局只有一个 CAS，以 sha1 为键**（`objects/<前两位>/<sha1>`）。对象一律只读；下载先原子写入 `object-*.part`，验证大小、sha1 与可选的 sha512 后才移入。
- **临时目录持有锁并记录游戏 PID**（`.haytham-lock`）；具名 Maven 路径只在临时副本中出现。
- **不在任何游戏目录写 `launcher_profiles.json` 或创建 `versions\`。**
- **Microsoft 登录的 client ID 由调用方传入，不设默认值**；源代码里不放任何 client ID。
- **机密**：含凭据的类型 `ToString()` 一律脱敏（不要用 `record`），命令行与 token 不写进 log。
- **授权**：GPL-3.0-or-later。为了保留日后放宽授权（例如 LGPL）的可能，不收进第三方 GPL 代码，也不加入 GPL-only 的依赖。

## 惯例

- 文档与领域名词用简体中文（中国大陆用语）；代码、标识符、注释与 API 文档用英文。
- Commit 信息为双语：`type(scope): 中文 / English`。作者 email 用 GitHub 的 noreply 地址，不用个人邮箱。
- 新代码只用新名称：`gebinde.toml`、`gebinde.lock`、`.haytham-lock`、`GEBINDE_`／`HAYTHAM_` 环境变量；代码中不出现 `cairn`。
- 参考 ProjBobcat、HMCL、Prism 时可参考但不复制；旧版 Cairn（Kotlin）只当行为规格，代码要重新写。
- `.claude/`、`.codex/`、`.codei18n/` 与构建产物是本地内容，不要提交。

## 重要变更必须留下笔记

非平凡变更（行为、架构、跨文件契约、流程与工具链、测试策略、落盘／网络／配置格式）前，依 `.agents/skills/write-notes-like-deepseek/SKILL.md` 在 `.agents/notes/` 撰写或更新笔记；新构想放 `proposed/`，实现时随代码移到 `implemented/`；放弃某个方案前先写出它最有说服力的理由。提交前运行 `npm run verify-notes`（Node 22.18 以上）。

## 构建

.NET 10 SDK（版本由 `global.json` 锁定）。

```powershell
dotnet build HaythamCore.slnx
```

`Directory.Build.props` 打开 nullable、警告当错误与 `IsAotCompatible`；每个项目一个程序集，依赖方向由项目引用强制。测试框架尚未决定，决定后再加入 `tests/`。
