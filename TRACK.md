# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S3初版四任务与整阶段审查、macOS合成核心／失败／拒绝检查完成；Task5预算与格式2兼容修复已审查并完成后验，Task6压缩篇幅提示与格式2／3兼容修订进行中。真实完整压缩闭环及Windows未验收。[S3计划](docs/implementation/s3-context-plan.md)；[S3验证](docs/implementation/s3-verification.md)；[初版整阶段审查](docs/reviews/codex-s3-20261026.md)；[预算修复审查](docs/reviews/codex-s3-budget-20261027.md)。目录 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 已审查产品基线：Task5 `bc4a46c`，原始S3包 `3452065`／构建头`ad2dd16`保留对照。保守预算、关键原文保护、源摘要复用、引用＋hash清单、两槽分阶段压缩、停止／修复／重开不重发及历史提示已实现；Task5新接受格式3并保留旧格式2精确校验。Task6尚未审查。S3-ARCH-1归属错误已按共享架构修复，不改main；S4知识总结／网络／删除归档尚未实现。
- 验证：初版控制器286通过／2原有跳过；Task5最新完整Release **307通过、2原有显式跳过、0失败**，build零警告错误、Python8通过，独立审查Approved。控制器新代码读取两套旧数据副本12计划／9prepared及缓存身份／hash保持、逻辑行不变，原文件SHA不变；原生旧历史与草稿恢复且不重发。Task6待其单独门禁。继承最初RED及Task5 golden初次生成原始日志缺口已如实记录；保留S2视口扫描及S1极端修订号P3，长历史定量性能未验证。
- macOS S3：专用新schema5库，合成7000预算／128输出。实际观察压缩0/1、停止1/2后问题保留、重试续用及下一草稿、同缓存复用、重开历史notice／草稿／对应阅读消息恢复且不重发。解锁续测确认非法摘要Failed且无缓存／最终请求、恢复配置后重试成功；8034字符必要原文明确拒绝，0新增消息／任务／请求、完整草稿保留。最终10父任务（7完成Reply／1失败Reply／1停止Reply／1标题）、6源尝试、4缓存、7prepared、0活动，FK检查空，应用与夹具均已正常关闭。阅读恢复非逐像素一致；S2人工A→B→C和窄窗另见[S2记录](docs/implementation/s2-verification.md)。
- 平台与资产：最终macOS包及原字节ZIP在 `artifacts/macos/`、`artifacts/macos-s3/`；S3自包含Windows测试ZIP在 `artifacts/windows-s3/`，逐文件hash／CRC通过，含独立数据入口与合成问题，不含DB／真实凭据。用户暂时没有Windows11，未实机。S1/S2数据、ZIP和`.reference`均保留；临时证据及SDD目录仍使用中，不删除，不重派已完成任务。
- 真实服务：S1仍用7/8生成、1/1目录，剩余1次未用。S3独立库实际 **5次（2回答＋1标题＋2摘要）／12上限、0目录**；两轮Recorded.完成，初次摘要1024截断，Task5后4096摘要完整但3330字符长于覆盖2853，最终ContextBudgetExceeded且未发回答，完整闭环未通过。格式3有效缓存保留，U3及旧尝试不改。密钥仅本地凭据服务、录入时不读界面；思考默认。
- 下一步：Task6仅改压缩篇幅与去重提示，新格式4／prompt-v2，旧格式2／3仍精确只读兼容，不改SQL、thinking或协议。独立审查及后验后，才在剩余7次内离线规划并重试原U3，不重发前两轮、不扩大[测试材料](docs/implementation/s3-supplier-acceptance.md)；如该次仍失败则审视策略，不盲改其他变量。所有本轮测试应用与合成服务均已关闭，其他轨道进程不操作。S4尚未开始；完整课程／发行验收未执行。共享需求／架构与main无差异，未借用另一轨道代码，未推送或合并。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
