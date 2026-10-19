# 需要时读：校验脚本

> SKILL §6 的细则。接入 CI 时对照；本地简单使用时不用读。

## 检查脚本（都是 TypeScript，不增加依赖，都可以用 `npx tsx` 单独运行）

1. **`verify-agent-note-tree`**（`scripts/agent-note-tree.ts` + `scripts/verify-agent-note-tree.ts`）
   - 检查：lifecycle 只能是 `proposed`／`implemented`／`rejected` 与 `archived`；分类只能是固定的 6 个；路径层级是 `{lifecycle}/{class}/file.md`；文件名是 `yyyy-mm-dd-topic.md`；不允许 `INDEX.md`；活跃笔记之间的相对 Markdown 链接都有效。

2. **`verify-agent-note-format`**（`scripts/verify-agent-note-format.ts`）
   - 开头：第 1 行是 `# Agent Note:` 标题（半角、全角冒号都接受，中文输入法常打出全角）；第 2、4 行是空行；第 3 行是 `Status:`，与 lifecycle 一致，全文只有一行。
   - 结构：第一节必须是 `## Problem`／`## 问题`；每种 lifecycle 必需的章节按中英文别名匹配（`## Decision`／`## 决策`、`## Consequences`／`## 后果` 等；`## Decision（说明）` 这类括号后缀会先去掉再匹配）；`implemented` 不能有提案式的标题（`## Proposal`／`## Plan`／`## Migration plan`／`## Acceptance criteria` 及其中文别名）。时态靠写的人自己守，脚本不扫正文。
   - 备选方案：必须有 `## Alternatives considered`／`## 备选方案`／`## 已考虑的替代方案` 等别名之一。脚本不检查有没有“不做／维持现状”这一项，也不接受用注释占位来跳过。
   - 兼容：自动处理 CRLF 与 BOM。

3. **`verify-archived`**（`scripts/verify-archived-agent-notes.ts`，默认启用）
   - 每篇归档笔记逐行检查开头：第 3 行 `Status: implemented`、第 4 行紧接着 `Archived: YYYY-MM-DD`、第 5 行空行。不接受 `Superseded-by:` 字段。
   - `archived/manifest.json`：每个文件都有记录，每条记录都有对应的文件，sha256 与磁盘上的内容一致（这就是“归档后不能改”的具体含义）。
   - 只增不改：有 git 时，与基线 ref 的 manifest 逐条比较（环境变量 `AGENT_NOTE_ARCHIVE_BASE_REF`，本地默认 HEAD）。CI 里必须指向改动之前的 commit（本仓库的 workflow：PR 用 `pull_request.base.sha`，push 用 `github.event.before`），用 HEAD 等于没有比较。已有的记录被改动或删除就报错；**没有 git 时自动降级**为只核对哈希——不用 git 也能运行，只是少了与上一个版本的比较。
   - `--write`：先确认已有的记录没被改过，再给没有记录的文件补上记录（用来补录旧数据）。

4. **`check-note-anchors`**（`scripts/check-note-anchors.ts`，只报告不拦截，退出码总是 0，**不放进 CI**）
   - 扫描源码（环境变量 `AGENT_NOTE_CODE_ROOT` 指定根目录，默认当前目录；自动跳过 node_modules、.git、dist 等）里的 `// Note:`／`# Note:` 注释：报告指向不存在的笔记的注释、没有任何注释指向的 implemented 笔记、缺少路径的注释行。宿主项目没用这种注释就不必运行。

## 怎么接入

- 简单使用（个人、没有 git）：前两个脚本就够了；第一次归档之后，归档检查自然开始起作用。
- 完整使用（团队）：把 `npm run verify-notes`（依次运行三个脚本）接进 CI，任何一项失败就让 CI 失败（可以用本 skill 的 `templates/verify-notes.yml` 作模板，复制到宿主根目录的 `.github/workflows/`，核对脚本路径后启用）。

三个校验脚本的做法来自 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)，按只用中文的宿主项目做了裁剪：去掉了双语相关的检查，也去掉了 DSH 迁移仓库时的例外。注释检查只是可选的报告，DSH 并没有把它当成必须通过的检查。

## 脚本路径

本文的 `scripts/` 指 skill 安装目录下的 `scripts/`。运行时保持当前目录为宿主项目根目录，用实际的 skill 路径调用脚本；安装 skill 不会创建宿主的 npm scripts。
