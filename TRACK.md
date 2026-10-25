# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S1 本地实现、自动化验证和代码审查完成；最终包原生复查及真实服务验收待用户协助（2026-10-26）。[S1 计划](docs/implementation/s1-chat-plan.md)；S0 历史见[数据关系](docs/implementation/s0-foundation.md)、[验证记录](docs/implementation/s0-verification.md)。本轨道目录为 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 可用代码：四工程原生桌面、主题工作区与会话创建／切换／重开、可选复制的正文、流式发送／停止／末尾重试、手动与自动标题、草稿和有效会话详情恢复、系统凭据配置、显式模型发现，以及确认后停止／保存／排空的退出流程。Main 仍持有唯一活动 ContentStack；Core／Application 无 UI 依赖。
- 当前本地验证：控制器在最终代码 `78f3fc1` 上重跑完整 Release：**129 通过、2 显式跳过、0 失败**，构建零警告／零错误；20 项真实 SQLite／Headless 桌面用例，loopback 夹具另有 **4 项**进程协议自测。范围与命令见 [S1 验证记录](docs/implementation/s1-verification.md)。
- 原生证据：`fba2ef6` 实际完成正常流／重试／自动与手动标题、慢流切换／停止、取消退出、草稿与工作区切换、正常关闭；强制结束后恢复 1321 字检查点，正文哈希不变、标为中断且不重发。最终包已从 `78f3fc1` 重建，但复查时 Mac 锁定，等待用户手动解锁；不能将旧版原生结果冒充最终包复查。
- 审查／下一步：四项任务已分别审查；[全量审查与限定复审](docs/reviews/codex-s1-20261026.md)确认两项 P2 已修复、无剩余 Critical／Important，保留一个极端修订号 P3。先在解锁后完成最终包针对性复查，再由用户在本地设置页录入 DeepSeek 官方／`deepseek-flash` 密钥并确认具体调用上限。尚无真实密钥或付费调用授权，未进行真实供应商 API 调用；自动标题 128 token 上限兼容性仍待真实验收。
- 凭据／平台：macOS 钥匙串随机合成值写／读／删已由前任务另行实际通过，未读取既有凭据；当前完整测试中的两个平台／显式 opt-in 用例仍跳过。Windows 11 原生与完整课程验收未执行。
- 隔离：程序固定为 `codex-steady`；默认／指定根目录下的数据子目录、锁与 IPC 身份已隔离并有测试。凭据实现按命名空间和随机版本引用访问，不能读取其他命名空间。
- 本轮临时资产：`artifacts/s0-native/` 保留原 S0 测试数据，本轮不再写入；`artifacts/s1-native/` 和 `artifacts/macos/Mindweave Codex.app` 继续用于最终包／服务验收。loopback 服务已停止，夹具脚本保留使用中，命令见验证记录；合成测试密钥无访问权限，测试资产不含用户正式数据、不进入 Git。`.superpowers/sdd/s1-chat-plan/` 为本计划的临时报告与恢复进度，因待用户解锁／服务验收继续保留使用中，不应重新派发已完成任务。构建 `bin/obj` 与临时库可再生成。本轮没有借用激进轨代码，没有修改共享需求／架构。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
