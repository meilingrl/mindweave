# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S2 本地实现、完整独立审查与 macOS 本地合成服务核心走查已完成（2026-10-26）；Windows 11 和真实模型背景理解仍待验收。已细化 [S3 上下文预算／压缩计划](docs/implementation/s3-context-plan.md)，按四项任务实施：纯规划 `c776da7` 已独立审查 Approved，源会话归属／兄弟复用修复 `e0db9be` 已限定复审 Approved，完整回归 248 通过／2 原有跳过，开始 Task 3 两槽前置阶段；[S3 验证](docs/implementation/s3-verification.md)记录分层边界，不等待暂不可用的 Windows 设备。[S2 计划](docs/implementation/s2-branch-plan.md)；[S2 验证](docs/implementation/s2-verification.md)；[S2 审查](docs/reviews/codex-s2-20261026.md)。本轨道目录 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 可用代码：最终产品 `492b80e`。延续 S1 聊天，新增正式 Markdown、已保存终态选区分支、祖先身份冻结、来源书签／路径／侧栏层级、返回来源、普通切换和阅读／草稿恢复、保存失败防重及修复。Main 保持唯一活动 ContentStack，Core／Application 无 UI／SQLite 依赖；总结、网络与自动压缩尚未实现。
- 当前验证：控制器在最终 `492b80e` 上完整 Release **198 通过、2 原有显式跳过、0 失败**；macOS 包构建 **0 警告、0 错误**；夹具实现的 5 项自测通过。三项任务和整体 S2 差异均独立审查 Approved，元数据限定复审 Approved，无开放 Critical／Important。视口跳动与 payload 边界问题已修复；O(M×B) 视口重复扫描、既有极端修订号问题保留 P3。
- macOS 原生：用户完成真实鼠标 A→B→C，控制器核对两层关系／冻结身份、正确重复词来源、祖先重试后旧来源、普通切换／书签、草稿／阅读关闭重开；最窄逻辑宽 880 下高亮与上方阅读在下方流式追加中保持。最终原生 DB 为 2 分支／7 Completed 本地任务／0 活动任务。损坏来源原生注入、分支事务中途杀进程与定量长历史性能未执行。
- 真实服务：S1 最终 `78f3fc1` 已做针对性原生复查及 DeepSeek 官方／`deepseek-flash` 有界验收，见 [S1 验证](docs/implementation/s1-verification.md)。已用 **7/8 次生成、1/1 次目录查询**；剩余一次生成未使用，S2 无新增真实调用。S2 夹具验证协议与存储身份，不证明真实模型理解背景。
- Windows：最终代码自包含 win-x64 测试包位于 `artifacts/windows-s2/Mindweave-Codex-S2-win-x64-492b80e.zip`，含运行时、独立数据入口、夹具及操作清单；逐文件 hash／ZIP CRC 已核对，不含 DB／真实凭据。用户暂时无 Windows 11，尚未实机执行。跨构建不代表 Windows 或课程验收。
- 下一步：细化并实现 S3 的关键原文＋近期问答＋较早历史结构化摘要、复用失效与失败提示，保持冻结祖先边界和两个真实请求槽；先本地合成验证，再单列真实长会话质量验收。无需对既有设计重复产品审批，新增付费调用和确需用户取舍时再提出。
- 凭据／隔离：程序固定 `codex-steady`；数据、锁和 IPC 有隔离测试，凭据按命名空间及随机版本引用访问。macOS 合成钥匙串 roundtrip 已实际通过，默认完整测试两个平台／opt-in 用例仍跳过。S1 真实密钥由用户本地录入，未读取其他软件凭据；S2 独立 `artifacts/s2-native` 使用公开合成 key。
- 临时资产：`artifacts/s0-native` 不再写；`artifacts/s1-native` 保留作 S1 对照。S1 旧包归档 `artifacts/macos-s1/Mindweave-Codex-S1.zip` 及原字节 `.reference` 目录，原 S2 包另存 `artifacts/macos-s2/Mindweave-Codex-S2-492b80e.zip`；当前 `artifacts/macos/Mindweave Codex.app` 是 `0d938cf` 脚本生成的独立启动 smoke 包（逻辑功能仍为 S2），宿主 RID 发布并已实际启动／运行保护检查。S2 测试应用及 loopback 服务已正常关闭。`.superpowers/sdd/s2-branch-plan` 与 S1 证据保留使用中，不重派已完成任务。测试资产与构建输出不进 Git，真实密钥仅在系统凭据服务；本轮未借用另一轨道代码，未更改共享需求／架构，未推送。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
