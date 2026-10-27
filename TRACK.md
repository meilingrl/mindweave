# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S3“仅摘要关闭”已实现、自动回归和整项审查通过，原生设置与真实传输闭环已完成；真实摘要漏掉一条有效禁令，完整语义质量未通过，正按Task9修复。Windows未实机。[计划](docs/implementation/s3-context-plan.md)；[验证](docs/implementation/s3-verification.md)；[整项审查](docs/reviews/codex-s3-thinking-integrated-20261027.md)。工作目录 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 当前产品：`57237c8`，新格式5、仅DeepSeek官方摘要显式关闭思考、默认仍服务默认；普通回答和标题默认。格式2／3／4默认请求、key、cap、cache／prepared精确兼容，v1–v5 SQL不改。旧包和全部数据保留；S4尚未实现。
- 验证：Task8完整Release **389通过、2原有跳过、0失败**，build、双平台publish无警告／错误、Python8；Task7与[设置页独立审查](docs/reviews/codex-s3-thinking-settings-20261027.md)均Approved，0Critical／Important／Minor，整项审查Approved，0Critical／Important／Minor。控制器新代码核验两套旧数据副本14计划／9源尝试／9prepared／12次缓存使用，ID／hash、全部逻辑行及原文件SHA不变；含3330字符有效格式3缓存。此前阶段与RED／golden时序证据保留；S2视口扫描、S1极端修订号P3及定量长历史性能仍未关闭。
- macOS S3：专用新schema5库，合成7000预算／128输出。实际观察压缩0/1、停止1/2后问题保留、重试续用及下一草稿、同缓存复用、重开历史notice／草稿／对应阅读消息恢复且不重发。解锁续测确认非法摘要Failed且无缓存／最终请求、恢复配置后重试成功；8034字符必要原文明确拒绝，0新增消息／任务／请求、完整草稿保留。最终10父任务（7完成Reply／1失败Reply／1停止Reply／1标题）、6源尝试、4缓存、7prepared、0活动，FK检查空，应用与夹具均已正常关闭。阅读恢复非逐像素一致；S2人工A→B→C和窄窗另见[S2记录](docs/implementation/s2-verification.md)。
- 平台与资产：57237c8候选macOS包在 `artifacts/macos/`，ZIP在 `artifacts/macos-s3/`；自包含Windows测试包在 `artifacts/windows-s3-57237c8/`，逐文件字节／manifest hash与CRC通过，含独立入口和合成问题，无DB／真实凭据。全部旧包、数据与reference保留。暂无Windows11，未实机；SDD证据仍使用中，不删除或重派已完成任务。
- 真实服务：S1仍7/8生成、1/1目录、剩余一次未用。S3实际 **8次（3回答＋1标题＋4摘要）／12上限、0目录**。格式5／Disabled的737字节摘要与U3回答均完成，日期地点／分类人数／D2更正／运输未决正确，指代正确但摘要漏了“不得整夜充电”，回答也未补全；完整质量未通过。7父任务／4源尝试／2缓存／3prepared／0活动、FK空，所有原问题和旧行不变，应用正常关闭；副本6份格式2–5计划／4源尝试／3prepared重读成功。未读凭据值或采集推理原文。
- 下一步：完成Task9完整有效约束提示及新格式6身份兼容，审查和新包核对后，仅对原U3再做一次最多2次实际生成的有界验证，仍在余4次／总12次内、0目录；回答／标题保持默认思考，摘要显式关闭。若再次缺失，停止调参与耗用并报告。[材料与结果](docs/implementation/s3-supplier-acceptance.md)。S4未开始，课程／发行未验收；共享需求／架构与main无差异，未借用另一轨道代码、未推送或合并。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
