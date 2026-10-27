# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S3软件门禁及有界真实复测已执行；当前flash默认思考／4k输出下真实压缩闭环**未通过**，停止尝试并等待用户策略决定。macOS失败保护与旧数据恢复已验证，Windows未实机。[计划](docs/implementation/s3-context-plan.md)；[验证](docs/implementation/s3-verification.md)；[初版审查](docs/reviews/codex-s3-20261026.md)；[预算修复审查](docs/reviews/codex-s3-budget-20261027.md)；[提示修订审查](docs/reviews/codex-s3-prompt-20261027.md)。工作目录 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 当前产品：`2595b98`（新格式4／prompt-v2、摘要min(4096,配置)），格式2／3的旧提示、key、cap、cache／prepared保持精确兼容；v1–v5 SQL、thinking／transport／UI不改。原始345包、Task5 bc4包及数据保留。保守预算、关键原文、源摘要复用、hash引用清单、两槽前置阶段和停止／修复／重开保护已实现；S3-ARCH-1已按共享架构修复，不改main。S4尚未实现。
- 验证：Task6最新完整Release **328通过、2原有跳过、0失败**，build与双平台publish无警告／错误、Python8；独立审查Approved无Critical／Important／Minor。控制器新代码核验两套旧数据副本13计划／8源尝试／9prepared／12次缓存使用，ID／hash、全部逻辑行及原文件SHA不变；含3330字符有效格式3缓存。初版286和Task5 307的证据保留，历史RED／golden时序边界如实记录；S2视口扫描、S1极端修订号P3及定量长历史性能仍未关闭。
- macOS S3：专用新schema5库，合成7000预算／128输出。实际观察压缩0/1、停止1/2后问题保留、重试续用及下一草稿、同缓存复用、重开历史notice／草稿／对应阅读消息恢复且不重发。解锁续测确认非法摘要Failed且无缓存／最终请求、恢复配置后重试成功；8034字符必要原文明确拒绝，0新增消息／任务／请求、完整草稿保留。最终10父任务（7完成Reply／1失败Reply／1停止Reply／1标题）、6源尝试、4缓存、7prepared、0活动，FK检查空，应用与夹具均已正常关闭。阅读恢复非逐像素一致；S2人工A→B→C和窄窗另见[S2记录](docs/implementation/s2-verification.md)。
- 平台与资产：2595候选macOS包在 `artifacts/macos/`，原字节ZIP在 `artifacts/macos-s3/`；新版自包含Windows测试包在 `artifacts/windows-s3-2595b98/`，字节／manifest hash及CRC通过，含独立数据入口与合成问题，无DB／真实凭据。旧windows-s3目录、S1/S2和S3旧包／数据／reference均保留。暂无Windows11，未实机；SDD证据仍使用中，不删除或重派已完成任务。
- 真实服务：S1仍7/8生成、1/1目录，剩余1次未用。S3独立库实际 **6次（2回答＋1标题＋3摘要）／12上限、0目录**；两轮Recorded.完成，三次摘要分别1024截断、4096完整但过长、格式4／4096正文0截断；均无U3物理回答，闭环未通过。最后问题与全部旧尝试保留，6父任务／3源尝试／1缓存／2prepared／0活动、FK空，应用正常关闭；关闭后格式2／3／4副本重读成功。未采集usage／推理原文，不断言内部原因；思考仍默认，密钥仅本地凭据服务。
- 下一步：当前已按约定停止，不再盲改或外呼。待用户决定是否重新纳入“仅摘要可关闭思考”的显式设置（默认仍服务默认，普通回答／标题默认，冻结配置和旧缓存兼容），或先评估更大摘要预算／暂挂S3并推进S4；[材料与策略](docs/implementation/s3-supplier-acceptance.md)。此前用户“暂不增加，先保留默认”仍有效，未经新决定不实现新设置。余6次未用，本轮应用／服务均关闭，不操作其他轨道。S4未开始，课程／发行验收未执行；共享需求／架构与main无差异，未借用另一轨道代码、未推送或合并。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
