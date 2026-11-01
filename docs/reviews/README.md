# 审查材料入口

| 批次 | 用途 |
| --- | --- |
| [独立设计复审](review-20261023-023314-sgt/README.md) | 五轮报告、v1–v6 冻结副本、证据与 D1–D8 决策来源；继续分层导航 |
| [详细设计就绪审查](claude-design-readiness-20261024-172436/README.md) | v1–v4、用户答复、详细设计交接；继续分层导航 |
| [claude-auto 架构整改与课程合规审查](claude-auto-architecture-20261101/README.md) | 激进轨（`6843143`）的 Claude／Codex 联合审查：整改提案、课程架构符合性矩阵、往复讨论；仅本轨道 |

现行行为以 [需求](../requirements.md) 和 [架构](../architecture.md) 为准。

## 2026-10-25 迁移说明

原根目录 `reviews/` 整体迁至 `docs/reviews/`，两个批次内部文件保持原字节，不改冻结稿、历史事实或哈希清单。批次内部互链沿用原结构；跨出批次的历史相对路径或绝对路径按当时项目布局解释，不能用旧路径直接验证当前布局。

旧路径与现行位置：

- 根目录 `initial-requirements.md` → `docs/requirements.md`
- 根目录 `architecture-discussion.md` → `docs/architecture.md`
- 根目录 `design-history.md` → `docs/history/design-history.md`
- 根目录 `reviews/…` → `docs/reviews/…`
- 根目录 `prototypes/…` 与 `README.md` 位置不变。

冻结清单记录的是当时的文件路径及内容身份；现行文档已发生后续采纳与链接调整，不能把旧基线哈希误作现行文档的要求。需要原始布局时可查 Git 标签 `baseline-v0.11`；更早冻结版本仍在相应批次中。
