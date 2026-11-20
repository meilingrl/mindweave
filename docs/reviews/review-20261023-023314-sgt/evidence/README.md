# 证据入口与原始语境

状态：本次审查的保留证据。原样快照用于核对原字节，不是从本目录运行的工程。

| 入口 | 用途与相对链接语境 |
| --- | --- |
| [基线清单](baseline-manifest.json) | 本轮实际涉及的47个原始文档/源码/配置/脚本的绝对路径、时间与SHA-256；不含生成二进制或用户应用数据 |
| [项目 README 原样快照](project-README.md) | 相对链接按[原项目目录](~/Developer/Projects/mindweave/)解释 |
| [聊天原型 README 原样快照](avalonia-chat-README.md) | 相对链接按[原型目录](prototypes/avalonia-chat/)解释 |
| [组件小样 README 原样快照](livemarkdown-chat-README.md) | 相对链接按[小样目录](prototypes/livemarkdown-chat/)解释 |
| [五类验证边界](verification-boundaries.md) | 历史原型技术记录、用户操作确认、正式实现、课程与产品价值分别处理 |
| [固定库版本核对](official-library-check.md) | 官方元数据与固定提交资料，未以main/latest倒推 |
| [课程原件取证](../round-1/verification-evidence-course.json) | 只读OOXML段落、原始路径与SHA、解释边界；不是课程通过证明 |
| `v*-sha256.json` | 每个形成版本的两份完整副本哈希，版本形成后不再改写 |

v1及三个README快照均保留原字节，不修其中相对链接。v2起的审查副本已把本轮依赖的原型/课程入口指回原实际位置；最终链接核查区分“原样历史快照链接语境”和“新交付导航”，不把前者为保持原字节而留存的原链接报告成原项目坏链。
