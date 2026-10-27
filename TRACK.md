# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S4知识总结与版本管理计划就绪（[S4计划](docs/implementation/s4-summary-plan.md)），尚未实现；此前S3实现、自动回归／审查、macOS失败保护与旧数据恢复完成；DeepSeek官方flash的三轮受控样本在专用10600预算／4096输出下通过真实压缩语义闭环，Windows未实机。[计划](docs/implementation/s3-context-plan.md)；[验证](docs/implementation/s3-verification.md)；[整项审查](docs/reviews/codex-s3-thinking-integrated-20261027.md)；[完整约束修复审查](docs/reviews/codex-s3-constraints-20261027.md)。工作目录 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 当前产品：`1f9c6bd`，新格式6／完整有效约束提示；旧2–5默认及5的Disabled请求、key、cap、cache／prepared精确兼容，v1–v5 SQL不改。显式仅摘要关闭选项默认仍服务默认，普通回答和标题默认；S3专用真实配置显式Disabled。旧包及全部数据保留；S4尚未实现，当前正在准备首个纯规则任务。
- 验证：最终Release **407通过、2原有跳过、0失败**，build及双平台publish无警告／错误、Python8；任务、整项与格式6增量审查均Approved，0Critical／Important／Minor。控制器核验实际旧2–5副本15计划／10源尝试／10prepared／13次缓存使用，最终真实副本另8份2–6计划／6源尝试／4prepared／4次缓存使用，原文件SHA及副本逻辑行均不变。此前RED／golden证据保留；S2视口扫描、S1极端修订号P3及定量长历史性能仍未关闭。
- macOS S3：专用新schema5库，合成7000预算／128输出。实际观察压缩0/1、停止1/2后问题保留、重试续用及下一草稿、同缓存复用、重开历史notice／草稿／对应阅读消息恢复且不重发。解锁续测确认非法摘要Failed且无缓存／最终请求、恢复配置后重试成功；8034字符必要原文明确拒绝，0新增消息／任务／请求、完整草稿保留。最终10父任务（7完成Reply／1失败Reply／1停止Reply／1标题）、6源尝试、4缓存、7prepared、0活动，FK检查空，应用与夹具均已正常关闭。阅读恢复非逐像素一致；S2人工A→B→C和窄窗另见[S2记录](docs/implementation/s2-verification.md)。
- 平台与资产：1f9c6bd候选macOS包在 `artifacts/macos/`，ZIP在 `artifacts/macos-s3/`；自包含Windows测试包在 `artifacts/windows-s3-1f9c6bd/`，逐文件字节／manifest hash与CRC通过，独立测试入口无DB／真实凭据。全部旧包、数据与reference保留。暂无Windows11，未实机；SDD证据保留用于复核。
- 真实服务：S1仍7/8生成、1/1目录、余1未用。S3最终 **11次（4回答＋1标题＋6摘要）／12上限、0目录、余1未用**。末次原U3摘要1675字符，回答五项正确，包含备用电池禁令、D2更正、人数分类、运输未决；prepared实际引用新缓存，估计9754≤10600，而原文10769仍需压缩。此前截断、过长、漏禁令和预算拒绝全部保留，不概括为普遍无损。9父任务／6源尝试／4缓存／4prepared／0活动、FK空，原问题和旧行不变；应用正常关闭，未读取凭据值或采集推理原文。
- 下一步：按[S4计划](docs/implementation/s4-summary-plan.md)依次完成纯合同、事务、调度、生成／命名和桌面闭环；上下文摘要不作为知识总结或网络节点。S3剩余1次与S1剩余1次不挪作S4；新阶段真实付费材料和额度另行说明。Windows、取消账单语义、长期质量与课程／发行验收仍独立。共享需求／架构与main无差异，未借用另一轨道代码、未推送或合并。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
