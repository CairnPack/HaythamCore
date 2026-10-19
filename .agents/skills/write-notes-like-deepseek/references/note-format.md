# 需要时读：笔记的文件格式

> SKILL §2 的细则。写或改笔记时对照；只改代码、不动笔记时不用读。

## 开头三行（脚本会检查）

```markdown
# Agent Note: <标题>

Status: <状态>
```

- `proposed` → `Status: proposed`
- `implemented` → `Status: implemented`
- `rejected` → `Status: rejected — <一句话原因>`

标题前面必须有 `Agent Note: `；状态里不写日期和括号；状态必须和所在的 lifecycle 目录一致（脚本会核对）。文件名里的日期是第一次提出的日期，其他时间信息看 git。

## 正文结构

每篇笔记都从 `## Problem` 开始，写动机；动机要在不看方案的情况下也成立。之后按 lifecycle 区分：

**`proposed/`**

```markdown
## Problem
## Proposal
…自由章节…
## Alternatives considered
## Acceptance criteria
## Risks
```

`Proposal` 可以用将来时；`Acceptance criteria` 写清楚看到什么状态就算完成；`Risks` 同时写风险和已经接受的取舍。

**`implemented/`**

```markdown
## Problem
## Decision
…自由章节…
## Alternatives considered
## Consequences
```

`Decision` 用现在时描述已经实现的事实；脚本只拒绝提案式的标题（`## Proposal`／`## Plan`／`## Migration plan`／`## Acceptance criteria` 及其中文别名）；`## Consequences` 同时写代价和收益；需要时可以加 `## Testing`／`## Verification` 这类现在时的章节。时态靠写的人自己守，脚本不扫正文。

**`rejected/`**

保持提案当时的样子，结论写在 `Status:` 行；保留提案时的 `Alternatives` 等章节，不改成相反的内容。

## Alternatives considered（必须写）

每篇笔记都必须有 `## Alternatives considered`：每个**真正考虑过**的备选方案为什么没有采用，一个方案一段（也可以用 `### Why not <X>?` 小节）。没考虑过的选项不要编。“不做／维持现状”只有当时真的考虑过才写。脚本只检查这一节在不在。

## 时态，以及不能改写成另一个决定

- `proposed` 可以用将来时；`implemented` 一律用现在时，描述已经实现的事实。
- 一篇笔记不能被改成另一个决定；被取代时由新笔记接手，旧笔记按归档或合并的规则处理。

标题行也接受全角冒号（`# Agent Note：标题`，中文输入法常会打出全角）；新笔记建议用半角。

## 自由章节与文风

- 自由章节（包的依赖关系、接口约定、数据结构等）放在 `Decision` 与 `Alternatives` 之间。
- 保留可以搜索的机制名，用 `must`／`may`／`never` 标明强制程度与先后顺序；一件事只在一处讲清楚，其他地方链接过去。
- 引用其他笔记用相对 Markdown 链接 `[topic](../../implemented/architecture/2026-…-….md)`，不要只写编号。

格式与标题别名表沿用 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的笔记规范；本项目的正文用中文，开头的 `Agent Note:` 与 `Status:` 保持英文，方便脚本检查。
