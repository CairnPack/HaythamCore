# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目

Haytham Core 是 CairnPack 的可复用 Minecraft 启动器核心，介绍见 [README](README.md)。另外两个产品：整合包工具链 **Gebinde**（`CairnPack/Gebinde`）与桌面启动器 **Haytham Launcher**（以子模块引用本 repo）。跨产品的设计文档、ADR 与名词表在 Haytham Launcher repo，它在正式版发布前不公开。

## 架构

| 项目 | 内容 | 可依赖 |
|---|---|---|
| `Haytham.Core.Store` | CAS、下载、文件链接、临时目录；GC（根集合由外部提供） | 无 |
| `Haytham.Core.Minecraft` | 启动核心的纯领域：版本解析、规则、参数、natives、崩溃诊断；不碰磁盘与网络 | 无 |
| `Haytham.Core.Launch` | 启动核心的 I/O：版本元数据、Java、加载器安装、服务器 runtime、账号、游戏进程 | Minecraft、Store |

- 新项目也加 `Haytham.Core.` 前缀。
- **`Haytham.Core.Store` 作为独立的 NuGet 包发布**，单独定版本：Gebinde 的转接项目只依赖它，不依赖整个 Haytham Core。
- **Haytham Core 与 Gebinde 互不引用。** `Haytham.Core.*` never 引用 `Gebinde.*`，也不认识 Manifest、Lockfile 或实例：启动入口只收“游戏目录＋版本＋加载器”。
- **全局只有一个 CAS，以 sha1 为键**（`objects/<前两位>/<sha1>`）。对象一律只读；下载先原子写入 `object-*.part`，验证大小、sha1 与可选的 sha512 后才移入。
- **临时目录持有锁文件 `.haytham-lock`**，所有者记录 `.haytham-owner` 记下游戏的 PID 与启动时间（PID 会被重用，判断游戏是否还活着要比对启动时间）；具名 Maven 路径只在临时副本中出现。
- **不在任何游戏目录写 `launcher_profiles.json` 或创建 `versions\`。**
- **Microsoft 登录的 client ID 由调用方传入，不设默认值**；源代码里不放任何 client ID。
- **机密**：含凭据的类型 `ToString()` 一律脱敏（不要用 `record`），命令行与 token 不写进 log。
- **授权**：为了保留日后放宽授权（例如 LGPL）的可能，不收进第三方 GPL 代码，也不加入 GPL 授权的依赖（LGPL 可以）。

## 异常与资源边界

- 库的已知业务失败、网络/文件系统错误和用户取消须显式传播给调用方；内部缺陷不得吞掉后返回成功、默认值或无限等待。开发及非稳定版优先让问题暴露，不以维持调用方进程存活为理由掩盖错误。
- 保留有明确条件的文件竞争、网络重试、取消和资源清理；资源释放、原子写入与下次恢复要求不因允许显式失败而放松。不能按 catch 语法批量删除处理。
- 库负责错误和自身资源，不自行终止或重启宿主。稳定版的故障呈现与受控重新拉起由应用入口决定，不向库加入 Launcher 专用恢复机制。

## 惯例

- 文档与领域名词用简体中文（中国大陆用语）；代码、标识符、注释与 API 文档用英文。
- Commit 信息为双语：`type(scope): 中文 / English`。作者 email 用 GitHub 的 noreply 地址，不用个人邮箱。
- 新代码只用新名称：`gebinde.toml`、`gebinde.lock`、`.haytham-lock`、`GEBINDE_`／`HAYTHAM_` 环境变量；代码中不出现旧产品名 `cairn`（团队名 CairnPack 不受影响）。
- 参考 ProjBobcat、HMCL、Prism 时可参考但不复制。旧版 Cairn 在 Haytham Launcher repo 的 `legacy` 分支（本 repo 没有这个分支），只当参考，不是规格：行为以本文件、本 repo 的笔记，以及 Haytham Launcher repo 的 `docs/design.md` 与开发者指南为准，代码要重新写。

## 重要变更必须留下笔记

非平凡变更前，先按 `.agents/skills/write-notes-like-deepseek/SKILL.md` 在 `.agents/notes/` 撰写或更新笔记；什么算非平凡、放在哪个目录、何时归档都以那份 skill 为准。提交前运行 `npm run verify-notes`，失败先修正再提交。
