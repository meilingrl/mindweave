# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S1 会话与聊天实施中（2026-10-26 用户要求继续推进）。[S1 计划](docs/implementation/s1-chat-plan.md)；S0 已完成本地实现和 macOS 验证，见[数据关系](docs/implementation/s0-foundation.md)、[验证记录](docs/implementation/s0-verification.md)。
- 可用代码：四工程原生桌面、主题工作区与会话创建／切换／重开、可选复制的正文、流式发送／停止／末尾重试、手动与自动标题、草稿和有效会话详情恢复、系统凭据配置、显式模型发现，以及确认后停止／保存／排空的退出流程。Main 仍持有唯一活动 ContentStack；Core／Application 无 UI 依赖。
- 当前本地验证：完整 Release **122 通过、2 显式跳过、0 失败**，构建零警告／零错误；新增 13 项真实 SQLite／Headless 桌面回归，测试专用 loopback SSE 夹具另有 2 项进程协议自测。范围与命令见 [S1 验证记录](docs/implementation/s1-verification.md)。S0 曾完成的 macOS 操作保留在原 [S0 记录](docs/implementation/s0-verification.md)。
- 审查／下一步：S1 存储、协议／凭据、应用协调器已完成各自独立审查；Task 4 桌面代码已本地验证，待控制器独立审查及 macOS 新窗口走查，不以 Headless 代替原生验收。用户选择 DeepSeek 官方／`deepseek-flash` 为后续真实验收对象，尚无用户密钥与付费调用范围授权，当前未外呼。自动标题实际 128 token 上限兼容性仍需供应商验收。
- 凭据／平台：macOS 钥匙串随机合成值写／读／删已由前任务另行实际通过，未读取既有凭据；当前完整测试中的两个平台／显式 opt-in 用例仍跳过。Windows 11 原生与完整课程验收未执行。
- 隔离：程序固定为 `codex-steady`；默认／指定根目录下的数据子目录、锁与 IPC 身份已隔离并有测试。凭据实现按命名空间和随机版本引用访问，不能读取其他命名空间。
- 本轮临时资产：`artifacts/s0-native/` 的专用测试数据与 `artifacts/macos/Mindweave Codex.app` 开发启动器仍用于体验，保留使用中；不包含用户正式数据、不进入 Git。`tests/fixtures/chat_sse_fixture.py` 保留使用中，供后续原生协议验证，启动命令见 S1 验证记录；新 `artifacts/s1-native/` 仅为控制器后续专用测试数据的建议位置，Task 4 未重建或操作现有原生包。构建 `bin/obj` 与 xUnit 临时库可再生成。本轮没有借用激进轨代码，没有修改共享需求／架构。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
