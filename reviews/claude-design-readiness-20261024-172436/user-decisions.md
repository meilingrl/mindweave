# 审查后用户决定

记录用户对本次审查结论的明确答复。v1／v2 正文不回写；本记录与 v2 一起构成当前口径。仍未授权修改项目根目录现行文档或启动开发。

## 2026-10-24：接受 A 类补全

用户原话：

> I accept MW-C01 to MW-C04 as written in v2. Next I'll answer U1 to U3.

| 编号 | 已接受内容（以 v2 原文为准） |
| --- | --- |
| MW-C01 | 用户消息与已结束回答不可改写；重试另存为新回答并记录取代关系，旧尝试保留可读；续聊默认取每个提问的最新尝试 |
| MW-C02 | 补全逻辑实体与引用清单；跨会话引用只指向祖先链；上下文摘要归属被覆盖消息所在的会话 |
| MW-C03 | 四工程依赖方向与运行期状态归属表（架构 §2.2） |
| MW-C04 | 首版不从总结正文直接创建分支，通过回到来源会话继续 |

因此 v2 中这四项的〔审查补全〕标记应理解为**用户已确认**。[审查报告](review-report.md) §5 的条件 1 已满足；条件 2（持久化模型先定稿）仍适用；条件 3 待 U1–U3 答复。

## 2026-10-24：答复 U1–U3

用户原话：

> U1: Also provide "regenerate" for completed answers.
>
> U2: Yes. Two ways to become read: the system determines it is read based on the user's reading progress exceeding a certain threshold (not read as soon as it is opened), or the user manually clicks the reading-status toggle to mark it as read.
>
> U3: Newly generated summaries do not trigger an explicit save prompt; just save them directly.

| 编号 | 决定 | 落入 v3 的方式 | 审查者的理解（请用户核对） |
| --- | --- | --- | --- |
| U1 | 选 B：已完成的回答也提供“重新生成” | 与重试使用同一保存规则（MW-C01）：另存新尝试，旧尝试保留 | 无 |
| U2 | 两条已读途径：阅读进度超过阈值（仅打开不算），或用户手动点阅读状态开关 | **改变原有确认规则**“打开某版本正文才记已读”；网络单击仍不计已读；手写保存版本记为已读、采用已读候选不重置未读等规则保留 | ① “开关”理解为可在已读与未读之间切换，即也能改回未读；② 阈值是实现参数，不需要用户先定 |
| U3 | 新生成总结的自动位置直接保存，不触发保存提示 | **改变原有确认规则**“新增节点位置产生待保存布局”；显式整理与将来的拖动仍需显式保存并保留离开提示 | ① 工作区首次出现节点时的自动布局也属于“新生成总结的位置”，同样直接保存；② 直接保存只写新节点自己的位置，不顺带提交用户尚未保存的整理结果 |

因此 [审查报告](review-report.md) §5 的条件 3 已满足。答复引出两个新问题，见审查报告 §6：MW-C29（重试／重新生成可作用于哪条回答，形成 U4）与 MW-C30（短正文如何避免“打开即超过阈值”）。

## 2026-10-25：答复 U4 并核对理解

用户原话：

> U4: retry and regenerate apply only to the conversation's last answer. Both of your interpretations of U2 and U3 are correct.

| 编号 | 决定 |
| --- | --- |
| U4 | 重试与重新生成只限会话最后一条回答（MW-C29 的审查建议成立） |
| U2 理解 | 阅读状态开关可在已读与未读之间切换；阈值是实现参数 |
| U3 理解 | 工作区首次出现节点时的自动布局同样直接保存；直接保存只写新节点自己的位置，不顺带提交尚未保存的整理结果 |

上述内容在 [v4](v4/initial-requirements.md) 中改标为〔用户已确认〕。

## 2026-10-25：写回现行文档

用户原话：

> Write v4 back into the root initial-requirements.md and architecture-discussion.md, and update the project README if needed.

- 项目根目录的 `initial-requirements.md`（现为 v0.11）与 `architecture-discussion.md` 已按 [v4](v4/initial-requirements.md) 写回。与 v4 的差别只有：链接改为从项目根目录解析；状态行、日期与版本改为现行稿；“审查标记约定”改为“审查标记”溯源说明；需求 §14 的 r2–r4 审查副本记录合并为一条 v0.11 记录；架构 §14.4 改为“审查采纳说明”。
- 项目 `README.md` 更新当前状态，并在阅读顺序中加入本审查入口。
- 本目录的 v1–v4 保持不变，v1 仍是写回前的原样基线。

## 待答复

当前没有待用户决定的产品事项。课程计数口径与 Root 等价性仍需老师确认（既有 MW-10），不属于本记录的产品取舍。
