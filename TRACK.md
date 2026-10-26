# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S2 分支与返回的本地代码、自动化和三项独立任务审查已完成，整体审查与原生验收进行中（2026-10-26）。[S2 计划](docs/implementation/s2-branch-plan.md)；[S2 验证](docs/implementation/s2-verification.md)。本轨道目录为 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 可用代码：延续 S1 真实聊天，新增正式 Markdown、已保存终态选区分支、祖先身份冻结、来源书签／路径／侧栏层级、返回来源、普通切换与阅读／草稿恢复，以及保存失败后的分支防重和全局修复。Main 保持唯一活动 ContentStack，Core／Application 无 UI／SQLite 依赖；总结、网络与自动压缩仍未实现。
- 当前本地验证：控制器在代码 `a6b77ad` 上重新执行完整 Release：**198 通过、2 原有显式跳过、0 失败**；构建 **0 警告、0 错误**；loopback 夹具 **5 项通过**。分任务最终代码分别为 `ea247a9`、`d03c147`、`a6b77ad`，均独立审查 Approved；一项共享视口跳动 Important 已修复并复审，来源 payload 边界已统一。整体审查待完成，不能把 Headless 结果当原生验收。
- 原生／服务：S1 最终 `78f3fc1` 已做针对性原生复查及 DeepSeek 官方／`deepseek-flash` 有界真实验收，详细范围见 [S1 验证](docs/implementation/s1-verification.md)。已用 **7/8 次生成、1/1 次目录查询**；剩余一次生成额度未使用，S2 实施未追加真实调用。S2 原生将用独立测试库与公开合成 loopback 配置，不复用用户真实凭据。
- 下一步：对全部 S2 差异做整体审查，重建 S2 专用包并验证实际鼠标选区、A→B→C、逐层返回、再次进入、普通切换、重开及窄窗。Windows 11 仍须实机；分支事务已测 SQL ABORT／ROLLBACK，尚未单独做途中杀进程。保留视口重复扫描 Minor 与既有极端修订号 P3，交整体审查判断。
- 凭据／隔离：程序固定 `codex-steady`；数据子目录、锁与 IPC 身份有实际隔离测试，凭据按命名空间和随机版本引用访问。macOS 合成钥匙串 roundtrip 前任务已实际通过；默认完整测试的两个平台／opt-in 用例仍跳过。S1 真实密钥由用户本地录入，未读取其他软件既有凭据。Windows 与完整课程验收未执行。
- 临时资产：`artifacts/s0-native/` 保留不再写；`artifacts/s1-native/` 和 `artifacts/macos-s1/Mindweave Codex.app` 保留作 S1 对照，原 S1 进程已正常关闭。`artifacts/macos/Mindweave Codex.app` 待替换为 S2 包，S2 将使用 `artifacts/s2-native/`。夹具脚本、`.superpowers/sdd/s2-branch-plan/` 使用中；S1 临时报告作为证据保留使用中，不重派已完成任务。测试资产与构建输出不进入 Git；真实密钥仅在系统凭据服务。本轮未借用另一轨道代码，未更改共享需求／架构，未推送。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
