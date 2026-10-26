# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S3 本地代码、全部任务及整阶段独立审查已完成，macOS 合成核心、非法摘要和必要原文拒绝检查已完成；Windows及真实摘要质量仍未验收。[S3计划](docs/implementation/s3-context-plan.md)；[S3验证](docs/implementation/s3-verification.md)；[整阶段审查](docs/reviews/codex-s3-20261026.md)。目录 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 最终代码：产品／夹具 `3452065`，构建头 `ad2dd16`（只追加文档）。新增保守预算、关键原文与近期问答保护、源会话结构摘要／兄弟复用、引用＋hash实际请求清单、两槽内分阶段压缩、停止／保存修复／重开不重发、进度及历史提示。S3-ARCH-1原计划归属错误已按共享架构修复复审，不改main。S4知识总结／网络／删除归档尚未实现。
- 验证：控制器最终 Release **286通过、2原有显式跳过、0失败**；solution与macOS publish零警告／错误；Python夹具8通过。四任务、源归属修复、打包、引用清单调整及整阶段审查均Approved，无开放Critical／Important／新Minor。继承TDD最初RED未留证据，后续真实缺陷红绿已记录；保留S2视口重复扫描及S1人工极端修订号P3，定量长历史性能未验证。
- macOS S3：专用新schema5库，合成7000预算／128输出。实际观察压缩0/1、停止1/2后问题保留、重试续用及下一草稿、同缓存复用、重开历史notice／草稿／对应阅读消息恢复且不重发。解锁续测确认非法摘要Failed且无缓存／最终请求、恢复配置后重试成功；8034字符必要原文明确拒绝，0新增消息／任务／请求、完整草稿保留。最终10父任务（7完成Reply／1失败Reply／1停止Reply／1标题）、6源尝试、4缓存、7prepared、0活动，FK检查空，应用与夹具均已正常关闭。阅读恢复非逐像素一致；S2人工A→B→C和窄窗另见[S2记录](docs/implementation/s2-verification.md)。
- 平台与资产：最终macOS包及原字节ZIP在 `artifacts/macos/`、`artifacts/macos-s3/`；S3自包含Windows测试ZIP在 `artifacts/windows-s3/`，逐文件hash／CRC通过，含独立数据入口与合成问题，不含DB／真实凭据。用户暂时没有Windows11，未实机。S1/S2数据、ZIP和`.reference`均保留；临时证据及SDD目录仍使用中，不删除，不重派已完成任务。
- 真实服务：S1 DeepSeek官方／deepseek-flash原授权已用 **7/8生成、1/1目录查询**，剩余1次未用；S2/S3未增加真实调用。真实密钥仍仅本地凭据服务，未用于自动化。用户决定本轮不增加思考模式选项，沿用服务默认。真实长上下文质量须有界授权后另验。
- 下一步：[真实质量材料](docs/implementation/s3-supplier-acceptance.md)已准备三轮虚构输入、期望和离线规划计数，拟新增最多8次生成（含压缩／标题、每次最多1024），未请求或取得新授权、未执行。原生测试应用已正常关闭，合成服务已停止。S4尚未开始；完整课程／发行验收未执行。共享需求／架构与main无差异，本轮未借用另一轨道代码，未推送或合并。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
