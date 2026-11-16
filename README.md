# Mindweave

面向个人日常好奇与非专业专题学习的 AI 对话探索工具。

**当前状态 · 2026-11-16：** 首版范围（S0–S7）与需求 v0.13 的首版后增量（推荐问题与推荐节点、内容关联）已实现。macOS 为主要开发与使用平台，Windows 11 x64 为次要支持与演示平台。

**仓库：** [GitHub 私有仓库](https://github.com/meilingrl/mindweave)。怎样构建、运行与测试见[运行说明](docs/design/run-guide.md)。

## 建议阅读顺序

| 入口 | 用途与范围 |
| --- | --- |
| 1. [产品需求与首版范围](docs/requirements.md) | 现行产品行为、核心流程、首版／后续边界与验收；回答“做什么” |
| 2. [架构与技术方案](docs/architecture.md) | 现行工程职责、导航、数据与生命周期、网络保存、验证状态及实施待办；回答“如何实现” |
| 3. [运行说明](docs/design/run-guide.md) | 构建、测试、运行、数据目录与发布 |
| [演进方向与工程指引](docs/evolution.md) | 首版之后的七个扩展方向、前置接缝与推进机制；方向性指引，不是现行需求，采纳状态见文末 |
| [设计沿革与历史记录](docs/history/design-history.md) | 旧路线比较、阶段停止点、原型验收与复审决定溯源；按需查阅，不作为现行待办 |
| [详细设计就绪审查](docs/reviews/claude-design-readiness-20261024-172436/README.md) | 2026-10-24 独立审查：问题分级、v1–v4 版本、用户决定记录及[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)；v4 已写回现行需求与架构，交接不是开发授权 |
| [独立设计复审](docs/reviews/review-20261023-023314-sgt/README.md) | 继续分层导航：冻结的五轮报告、版本与D1–D8讨论来源；后续已确认调整以现行需求和架构为准 |

审查材料的迁移说明与旧路径解释见 [审查入口](docs/reviews/README.md)。产品代码在 `src/`，测试在 `tests/`，构建与打包脚本在 `scripts/`；原型独立保留。

需求与架构按职责维护，避免各自复制完整细则；历史记录保留当时结论，不以旧“待定”覆盖后续决定。发现冲突时回查明确决定，不自行扩大范围。

## 验证资产

以下只列入口，不展开内部清单；使用方法、版本及证据边界以各自README为准。

| 入口 | 用途 |
| --- | --- |
| [Avalonia聊天原型](prototypes/avalonia-chat/README.md) | 聊天视觉、选区分支、返回来源、模拟流式和恢复参考 |
| [LiveMarkdown组件小样](prototypes/livemarkdown-chat/README.md) | 跨段选区、来源校验、重排和追加的独立验证 |
| [API兼容探针](prototypes/responses-compat/README.md) | Responses、Chat Completions及Messages的小规模协议验证记录；不存密钥 |
| [网络交互样例](prototypes/network-layout/README.md) | 本轮验证结束，保留布局浏览、拖动牵引、悬停吸引作为交互参考 |

原型通过不等于正式产品、全部平台或课程验收通过，也不证明延迟回顾价值。暂不继续扩展样例，不默认沿样例直接开发产品。
