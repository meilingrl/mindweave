# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S3默认思考／4k真实闭环未通过；用户已批准新增“仅摘要关闭”选项，Task7请求策略／旧格式兼容和Task8设置页均已独立过审，整项审查与原生后验待完成，尚未新调用或验收。此前软件门禁、macOS失败保护与旧数据恢复证据保留，Windows未实机。[计划](docs/implementation/s3-context-plan.md)；[验证](docs/implementation/s3-verification.md)；[初版审查](docs/reviews/codex-s3-20261026.md)；[预算修复审查](docs/reviews/codex-s3-budget-20261027.md)；[提示修订审查](docs/reviews/codex-s3-prompt-20261027.md)。工作目录 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 当前产品：`57237c8`，新格式5、仅DeepSeek官方摘要显式关闭思考、默认仍服务默认；普通回答和标题默认。格式2／3／4默认请求、key、cap、cache／prepared精确兼容，v1–v5 SQL不改。旧包和全部数据保留；S4尚未实现。
- 验证：Task8完整Release **389通过、2原有跳过、0失败**，build、双平台publish无警告／错误、Python8；Task7与[设置页独立审查](docs/reviews/codex-s3-thinking-settings-20261027.md)均Approved，0Critical／Important／Minor，整项审查尚在进行。控制器新代码核验两套旧数据副本14计划／9源尝试／9prepared／12次缓存使用，ID／hash、全部逻辑行及原文件SHA不变；含3330字符有效格式3缓存。此前阶段与RED／golden时序证据保留；S2视口扫描、S1极端修订号P3及定量长历史性能仍未关闭。
- macOS S3：专用新schema5库，合成7000预算／128输出。实际观察压缩0/1、停止1/2后问题保留、重试续用及下一草稿、同缓存复用、重开历史notice／草稿／对应阅读消息恢复且不重发。解锁续测确认非法摘要Failed且无缓存／最终请求、恢复配置后重试成功；8034字符必要原文明确拒绝，0新增消息／任务／请求、完整草稿保留。最终10父任务（7完成Reply／1失败Reply／1停止Reply／1标题）、6源尝试、4缓存、7prepared、0活动，FK检查空，应用与夹具均已正常关闭。阅读恢复非逐像素一致；S2人工A→B→C和窄窗另见[S2记录](docs/implementation/s2-verification.md)。
- 平台与资产：2595候选macOS包在 `artifacts/macos/`，原字节ZIP在 `artifacts/macos-s3/`；新版自包含Windows测试包在 `artifacts/windows-s3-2595b98/`，字节／manifest hash及CRC通过，含独立数据入口与合成问题，无DB／真实凭据。旧windows-s3目录、S1/S2和S3旧包／数据／reference均保留。暂无Windows11，未实机；SDD证据仍使用中，不删除或重派已完成任务。
- 真实服务：S1仍7/8生成、1/1目录，剩余1次未用。S3独立库实际 **6次（2回答＋1标题＋3摘要）／12上限、0目录**；两轮Recorded.完成，三次摘要分别1024截断、4096完整但过长、格式4／4096正文0截断；均无U3物理回答，闭环未通过。最后问题与全部旧尝试保留，6父任务／3源尝试／1缓存／2prepared／0活动、FK空，应用正常关闭；关闭后格式2／3／4副本重读成功。未采集usage／推理原文，不断言内部原因；思考仍默认，密钥仅本地凭据服务。
- 下一步：完成整项审查及新包原生设置核对；默认仍服务默认，仅显式DeepSeek官方摘要关闭，普通回答／标题默认，旧JSON／cache／prepared身份兼容。随后仅在余6次内对原U3做新策略验证，[材料与策略](docs/implementation/s3-supplier-acceptance.md)。当前实际仍6／12、0目录、未新增调用；本轮应用／服务已关闭，不操作其他轨道。S4未开始，课程／发行未验收；共享需求／架构与main无差异，未借用另一轨道代码、未推送或合并。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
