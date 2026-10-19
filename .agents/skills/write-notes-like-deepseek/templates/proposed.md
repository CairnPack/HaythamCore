# Agent Note: <标题>

Status: proposed

<!--
  【维护提醒】
  1. 实现后改成 implemented 时要改写：## Proposal 改为现在时的 ## Decision；
     ## Acceptance criteria / ## Risks 折进 ## Consequences（或现在时的 ## Testing / ## Verification）。
  2. 前提不存在了或不再推进 → 改成 Status: rejected — <原因>；proposed 不能归档。
  3. Alternatives 必须写：只记录真正考虑过的方案；“不做／维持现状”只有当时真的考虑过才写。
-->

## Problem

<动机，不看方案也成立；写清背景、约束，以及什么情况算失败>

## Proposal

<打算怎么改，可以用将来时；包括关键设计、迁移步骤与还没定的问题>

## Alternatives considered

必须写。只写当时真正考虑过的选项（没考虑过的不要编）。“不做／维持现状”可以是其中一项，但不是必须有。

每个方案一段，或者用 `### Why not <X>?` 小节：

- **<备选 A>** — <为什么没选：先写它最有力的理由，再写不采用的原因，一个方案一段>
- **<备选 B>** — <同上>

要同时比较多个选项时，可以用“标准 × 选项”的表格（是否符合需求 / 运维复杂度 / 团队技能 / 成本 / 被绑定的风险 / 实施时间），每格填 ✓、⚠️ 或 ✗。被否的选项必须写明为什么不采用。

## Acceptance criteria

<看到什么状态就算完成；包括在哪里验证、要通过哪些检查>

## Risks

<可能出什么问题、已经接受的取舍，以及什么情况下回退>
