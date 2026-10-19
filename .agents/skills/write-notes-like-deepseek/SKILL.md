---
name: write-notes-like-deepseek
description: Use when a change is non-trivial by DSH standards (behavior, architecture, cross-file contracts, process/tooling, testing strategy, or on-disk/wire/config formats), when choosing between technical alternatives, superseding a decision, or writing a postmortem. Records the why and rejected options in .agents/notes/ with script-enforced gates; skips purely mechanical edits (CRUD, styling, patches, tagging, formatting).
---

# 像 DeepSeek 那样写笔记

> 方法来自 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（下称 DSH）的工程实践。本 skill 只做一件事：把“为什么这样改、放弃了哪些做法、怎么确认改对了”写在 `.agents/notes/` 的一个地方，给以后改这段代码的人看。拆任务、排计划不归它管。

## 先判断要不要写

**不写笔记、直接提交的改动**（DSH 所说的纯机械或局部改动）：

- 排版、格式化、错别字、没有歧义的改名
- 不改变行为的样式调整
- 不改变行为的依赖补丁；发布打 tag（RC、Release）
- 普通的增删改查；只在一个模块里、看 diff 就懂的逻辑修复（不影响其他文件）

**必须写的改动**（非平凡）：改了以下任何一项——行为、架构、跨文件的契约、流程与工具链、测试策略、落盘／网络／配置格式——或者其他维护者以后可能回头查的决定。

判断方法：代码和单元测试表达不出来的意图、边界和取舍，就要写。拿不准时按“要写”处理。

写之前，想清楚这篇笔记属于哪一种：

1. **定新规则**：新的跨模块通信约定、状态转换规则、访问边界、运行时不变式。不写下来，后来的人会各写一套，绕过模块边界。
2. **记录妥协**：因为某个不明显的约束，放弃了常见或直觉上的做法。不写下来，被否掉的做法会被再提一次。
3. **收窄**：破坏性重构、删代码、缩小 API。不写下来，没人知道要删到哪里为止、迁移的边界在哪里，删不下去或删过头。

> 能用脚本检查的规则都由脚本检查（§6）。脚本查不了的——决定变了要新开一篇、写新篇时当场审计、正文用现在时——靠下面的操作步骤，不靠扫描用词。
> 优先级：宿主项目的 `AGENTS.md`／`CLAUDE.md` 与用户的直接指令高于本 skill；本 skill 只是默认规则。

常见的“不写”理由，以及为什么不成立：

| 理由 | 为什么不成立 |
|---|---|
| “只是改个默认值或改个名” | 默认值和名称也是决定；原地更新已有的笔记只要几十秒 |
| “先合并，以后再补” | 以后通常不会补，笔记就是这样一篇篇过时的 |
| “代码就是文档” | 代码只说明做了什么，说明不了为什么、放弃了什么 |
| “改动很小” | 改动小不等于影响小，一个重试参数也可能引发大范围故障 |
| “不确定要不要写” | 对照上面的清单：命中就写，没命中就不写，不要拿“不确定”当理由 |

基本规则：

- **优先更新已有的笔记，没必要就不新建。** DSH 的公开历史里，笔记的改动大多是原地修正已有笔记里的事实（路径、类名、默认参数），新建的很少。改动已经有对应笔记的，直接更新那一篇。
- **不要把一篇笔记改成另一个决定。** 事实（路径、符号、默认值）原地改；决定或理由变了，新开一篇，两篇互相链接。不要把 `## Decision` 改成相反的结论，也不要让 git 历史成为旧理由的唯一副本。
- **写新篇时当场审计，不要推迟。** 按模块名、关键词搜 proposed、implemented、rejected；每篇命中的笔记当场归类：无关／部分重叠（互链）／被完全吸收（删除或归档）／过时的提案（转 rejected 或删除）。归类结果和新笔记一起提交。
- **没有参考价值的不归档。** 过时的提案改成 `Status: rejected — <原因>`，不要归档；不能防止以后重犯的被否记录直接删除。

> “决定”不一定很大。个人项目里，“为什么选 A 不选 B”就是决定；选型、取舍、踩过的坑都值得写。

## 0. 先看仓库现状，再建目录

不要一开始就建全套目录，按仓库现在的情况选：

1. 有 `AGENTS.md`／`CLAUDE.md`／贡献指南 → 先读。已经有 `docs/adr/`、`docs/decisions/`、Issue 模板之类的决定记录 → 继续用，状态与分类按本 skill 的目录来。
2. 没有现成的决定记录 → 按标准结构建：`.agents/notes/{proposed,implemented,rejected,archived}` × 6 个分类，用到哪个目录才建哪个，不必预先建空目录。个人项目、不用 git 也按这个结构，不要简化成一个目录。
3. 团队长期维护的仓库 → 结构相同，再加上流程：`CONTRIBUTING.md`／PR 模板写一句“重要改动必须附一篇笔记”，并把校验脚本接进 CI。

## 1. 路径就是分类

每篇笔记的路径说明它是什么：`{lifecycle}/{class}/yyyy-mm-dd-topic.md`

**Lifecycle（第一层目录：这篇走到了哪一步）：**

- `proposed`：有了方案，还没实现
- `implemented`：已经实现，与代码在同一批改动里保持一致
- `rejected`：慎重考虑后否掉的提案；只有能防止以后重犯时才保留，否则删除
- `archived`：已经完成、以后很少需要参考的 implemented 记录；归档后不能再改

**Class（第二层目录，固定 6 个；要加新的得改检查脚本）：**

- `feature`：新能力，用户或模型看得到的行为（不明显的行为选择也算）
- `bug-fix`：修好了什么缺陷，或补上复盘发现的漏洞
- `simplification`：只删不加，不增加能力，只删代码、行为或对外暴露的部分
- `architecture`：发布出去的源码怎么组织、包之间怎么依赖
- `process`：围绕代码的工具和流程，例如检查、发布、协作方式（不是运行时行为）
- `testing`：测试策略与测试基础设施

> 不单列 `refactor`：看得到的行为变了，归到对应的分类；没变就是 `simplification`。不建 `INDEX.md`。细则见 `references/classification.md`。

## 2. 文件格式（脚本会检查）

正文一律用简体中文写（代码、命令、专有名词除外），不管触发任务时用的是什么语言。

前三行固定：

```markdown
# Agent Note: <标题>

Status: <状态>
```

状态必须和所在的 lifecycle 目录一致（`rejected` 要附一句原因）；文件名里的日期是**第一次提出的日期**。正文结构：

- `proposed`：`## Problem` → `## Proposal` → 自由章节 → `## Alternatives considered` → `## Acceptance criteria` → `## Risks`
- `implemented`：`## Problem` → `## Decision`（现在时）→ 自由章节 → `## Alternatives considered` → `## Consequences`
- `rejected`：保持提案当时的样子，结论写在 `Status:` 行

> 备选方案必须写：只记录真正考虑过的方案，先写它最有力的理由，再写为什么不采用。没考虑过的选项不要编。“不做／维持现状”只有当时真的考虑过才写。脚本只检查有没有 `## Alternatives considered` 这一节。确实没有真正考虑过的备选方案时，保留这一节的标题，正文写“无”就能通过检查；不要为了凑数编造。
> `implemented` 的 `## Decision` 用现在时；脚本只拒绝提案式的标题（`## Proposal`／`## Plan`／`## Migration plan`／`## Acceptance criteria` 及其中文别名）。细则见 `references/note-format.md`。

模板在 `templates/`。

## 3. 动手前先查已有的决定

重构或选型之前，先查以前的约束，避免重复踩坑或推翻前人的妥协。按下面四种方法查，不需要一个集中的索引：

1. **入口注释（如果有）**：代码入口已经有 `// Note: ... 见 .agents/notes/...`，就顺着读。没有就用后面三种方法，不要为了查而去补注释。
2. **按目录查**：按目的直接进对应目录，不要全库扫（查架构看 `implemented/architecture/`，查踩过的坑看 `rejected/`）。
3. **全文搜索**：用 ripgrep 搜关键词或机制名，**要带 `--hidden`，并排除 `archived/`**：
   ```bash
   rg --hidden --glob '!.agents/notes/archived/**' "<机制名或关键词>" .agents/notes/
   ```
4. **模块文档**：子模块 README 提到设计依据时，顺着相对链接找到对应的笔记。

**能自己查到的事实不要问用户。** 用以上方法能回答的问题自己查；只有需要拍板的取舍才问用户。

## 4. 什么时候写、什么时候改

**对话里出现这些话时，就该写笔记**（开头列出的机械改动除外，不要拿这些话当多写的理由）：

- 定下路线：“就选 X”“决定用 X”“先用 X 撑着” → 写笔记
- 在比较：“X 和 Y 怎么选”“为什么倾向 X” → 记下备选方案
- 同一段理由解释了第二遍 → 该写下来了

判断与操作：

- **重构已有架构、迁移路径、改参数** → **首选原地更新**：直接在负责这个决定的旧笔记里改事实（代码路径、方法签名、默认值），不另开新篇，也不要在正文末尾追加修改记录。`## Decision` 的核心理由不变；理由变了就新开一篇。
- **写新笔记之前** → 按模块名、关键词搜活跃笔记，当场归类（见开头的“当场审计”），不要留到以后集中整理。
- **有了新想法、还没动手** → 先写 `proposed`（为什么想这样做、考虑过哪几种做法），评审通过再动手。提问方式见下。
- **实现完成（proposed → implemented）**，在同一次改动里做完：① 移到 `implemented/<class>/`，文件名日期不变；② `Status: proposed` → `Status: implemented`；③ `## Proposal` → 现在时的 `## Decision`；④ `## Acceptance criteria`／`## Risks` 并入 `## Consequences`（或现在时的 `## Testing`）；⑤ 删掉计划性的段落。笔记和代码一起提交——用 git 时就是同一个 commit 或 PR。
- **方案被新决定部分取代** → 两篇都保留，互相加相对链接；只更新仍然成立的事实。不要归档。
- **方案被新决定完全取代** → 新笔记接手旧篇所有独有的理由、备选方案、后果与验证缺口；改完指向旧篇的链接后，能删就删；旧篇还有单独的参考价值时，用 `archive-agent-note.ts` 归档。指向关系写在新笔记里，不写进归档的那篇。
- **不用写的情况**：见开头的“不写笔记、直接提交的改动”。

**向用户提问时：**

1. **先分清事实和决定**：环境里查得到的事实（代码、笔记目录、rg）先自己查；只有真正要拍板的取舍才占用用户的时间。
2. **一次列出全部问题**：把所有要拍板的问题编号列出，每题单独一行给出推荐答案 `➡️ <推荐>`；用户按编号一次回答（“1 同意，2 选第二个”），不要一题一题地问、来回试探。
3. **动手前复述并确认，不靠限制题数**：动笔或动手之前，复述全部决定，用户确认后再执行；复述中按推荐默认的地方，用户可以在确认时更正。没有确认就不动手。
4. **要拍板的问题超过五个，说明这次改动太大**：拆成几篇笔记，或者先交一篇 proposed 草稿评审，不要在一次对话里全部塞进去。

> 判断细则见 `references/when-to-write.md`，归档与删除见 `references/archiving.md`。

## 5. 怎么写好

- `## Consequences` 同时写**代价和收益**，不要只写“放弃了什么”。
- 自由章节（包的依赖关系、接口约定、数据结构等）放在 `Decision` 与 `Alternatives` 之间；保留可以搜索的机制名，用 `must`／`may`／`never` 标明强制程度。
- 引用其他笔记用相对 Markdown 链接 `[topic](../../implemented/architecture/2026-…-….md)`，不要只写编号，这样脚本才能检查链接。
- 宿主项目如果有这个习惯，可以在核心入口加一行 `// Note: ... 见 .agents/notes/...`；这不是必需的。决定被取代时，这些注释也要一起改。
- 文风与去掉过程痕迹见 `references/prose-checklist.md`；简化的机会见 `references/simplification-checklist.md`。
- 写完后按 `references/quality-gate.md` 自查一遍意思是否完整，只向用户报告**缺口和写得好的地方**（不超过 5 行），每个缺口附上具体的改法。结构交给脚本检查，意思由人确认。

## 6. 校验与维护命令

在仓库根目录运行（宿主已配置 npm script 时）：

```sh
npm run verify-agent-note-tree     # 目录、分类、文件名、笔记之间的相对链接
npm run verify-agent-note-format   # 开头三行、状态、必需的章节、备选方案、implemented 不能有提案式标题
npm run verify-archived            # 归档：开头格式、manifest 哈希、只能追加不能修改（没有 git 时自动降级）
npm run verify-notes               # 依次运行以上三项（CI 用这个）
npm run archive-agent-note -- <path> [--superseded-by <新笔记>]  # 归档；可选在新笔记里加上互链，并列出失效的入站链接
npm run check-anchors              # 只报告不拦截：代码里有 // Note: 注释时，检查注释与笔记是否对得上；不放进 CI
npm run init-board                 # 生成约 69 KB 的轻量看板 board.html（日常推荐）
npm run bundle-board               # 生成内嵌全部数据的独立 demo.html
```

脚本在本 skill 的 `scripts/` 目录，从**宿主项目根目录**调用，默认检查当前目录下的 `.agents/notes/`。先根据已加载的 `SKILL.md` 路径确定 skill 的位置；不要假定宿主根目录有 `scripts/`，也不要切换到 skill 目录里执行。安装 skill 不会自动配置宿主的 npm scripts。

写完或改完任何笔记后都必须运行检查：有 npm script 就用 `npm run verify-notes`，没有就用下面的 `npx tsx` 命令；不能以“没有配置检查命令”为理由跳过。

安装在项目的 `.agents/skills/` 时，可以直接执行：

```sh
npx tsx .agents/skills/write-notes-like-deepseek/scripts/verify-agent-note-tree.ts
npx tsx .agents/skills/write-notes-like-deepseek/scripts/verify-agent-note-format.ts
npx tsx .agents/skills/write-notes-like-deepseek/scripts/verify-archived-agent-notes.ts
npx tsx .agents/skills/write-notes-like-deepseek/scripts/build-board.ts --init board.html "项目决策看板"
```

安装在 `.claude/skills/` 或全局目录时，把上面的路径前缀换成实际的 skill 路径。看板打包的参数是 `--bundle <notes目录> <输出.html> "名字"`；参数说明与例外规则见 `references/verification.md`。

团队可以把本 skill 的 `templates/verify-notes.yml` 复制到宿主仓库的 `.github/workflows/verify-notes.yml`，改成实际的安装路径，并把项目级的 skill 文件一起提交；模板放在 skill 目录里不会执行。可以在 `CONTRIBUTING.md`／PR 模板里写一句“重要改动必须附一篇笔记”。

## References

需要时再读：

- `references/note-format.md`：开头三行与正文结构的细则
- `references/classification.md`：6 个分类的判断方法与边界
- `references/when-to-write.md`：什么时候新建、更新、改变状态（含与 DSH 判断标准的对应）
- `references/archiving.md`：归档与合并删除（含怎么判断“以后还有没有参考价值”）
- `references/prose-checklist.md`：行文与去掉过程痕迹的自查
- `references/simplification-checklist.md`：简化机会的自查
- `references/quality-gate.md`：写完后的意思自查：Problem／Alternatives／Consequences／Verification 的判断与报告格式
- `references/verification.md`：校验脚本说明
