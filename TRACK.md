# 稳健轨 · track/codex-steady

用户与 Codex 协作推进，是课程提交版本。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进。在用户已授权的任务范围内，Codex 自主连续完成必要的实现、验证和修复，无需逐步确认；需要用户作出产品取舍、扩大授权范围或明确要求审批的操作时，再向用户确认。人机协作通过讨论目标、解释关键决定和反馈结果开展，不为每个操作设置人工审批。
- 按任务需要只读参考激进轨的发现、证据或代码，独立验证后采纳结论；借用代码须经用户决定，不直接合并其代码。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 当前状态

- 阶段：S5总结网络／布局实施中，见[S5计划](docs/implementation/s5-network-plan.md)。S4知识总结／编辑阅读／版本与命名的软件、任务与整阶段审查、Mac合成原生及本轮DeepSeek受控真实样本均完成。最终产品源`790b000`，673通过／2原有凭据跳过、build零警告错误、Python15；真实5/16生成（3知识＋2命名）、目录0，剩余11不转移后续阶段。Windows11未实机、课程／发行独立。[S4计划](docs/implementation/s4-summary-plan.md)；[验证](docs/implementation/s4-verification.md)；[真实样本](docs/implementation/s4-supplier-acceptance.md)。S5按现行共享设计推进；工作目录`/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。
- 此前已验收参考产品：`1f9c6bd`，新格式6／完整有效约束提示；旧2–5默认及5的Disabled请求、key、cap、cache／prepared精确兼容，v1–v5 SQL不改。显式仅摘要关闭选项默认仍服务默认，普通回答和标题默认；S3专用真实配置显式Disabled。旧包及全部数据保留；S4整阶段审查通过代码`6374f6c`，schema6冻结，版本事务、共享调度、知识总结／整组命名、桌面列表／编辑／阅读和版本确认操作落地，完整669通过／2原有跳过、build零警告错误、Python15；任务初审问题与测试竞争均经限定复审关闭，整阶段软件审查已通过，S4候选双平台已从最终源构建；原生合成已验证生成／版本／阅读／保存失败修复，N1后继源`790b000`修复已过审（673通过／2原有跳过），新Mac包三种选择操作复测通过；S3旧包全部保留。
- S3验证：最终Release **407通过、2原有跳过、0失败**，build及双平台publish无警告／错误、Python8；任务、整项与格式6增量审查均Approved，0Critical／Important／Minor。控制器核验实际旧2–5副本15计划／10源尝试／10prepared／13次缓存使用，最终真实副本另8份2–6计划／6源尝试／4prepared／4次缓存使用，原文件SHA及副本逻辑行均不变。此前RED／golden证据保留；S2视口扫描、S1极端修订号P3及定量长历史性能仍未关闭。
- macOS S3：专用新schema5库，合成7000预算／128输出。实际观察压缩0/1、停止1/2后问题保留、重试续用及下一草稿、同缓存复用、重开历史notice／草稿／对应阅读消息恢复且不重发。解锁续测确认非法摘要Failed且无缓存／最终请求、恢复配置后重试成功；8034字符必要原文明确拒绝，0新增消息／任务／请求、完整草稿保留。最终10父任务（7完成Reply／1失败Reply／1停止Reply／1标题）、6源尝试、4缓存、7prepared、0活动，FK检查空，应用与夹具均已正常关闭。阅读恢复非逐像素一致；S2人工A→B→C和窄窗另见[S2记录](docs/implementation/s2-verification.md)。
- 平台与资产：S4候选`6374f6c`的macOS开发包在`artifacts/macos/`、ZIP在`artifacts/macos-s4/`，自包含Windows包在`artifacts/windows-s4-6374f6c/`、ZIP在`artifacts/windows-s4/`；首次双发布零警告错误，manifest逐文件hash及ZIP CRC通过；N1源`790b000`的Windows候选已另存`artifacts/windows-s4-790b000/`并校验，Mac新790b000包及ZIP在`artifacts/macos/`／`artifacts/macos-s4-790b000/`，hash／CRC和N1原生复测通过。合成应用／服务已正常关闭，独立真实入口已完成虚构资料A／B及A2闭环并正常关闭。Windows暂无设备，未实机；S3及更早的包、数据与reference全部保留，SDD证据使用中。
- 真实服务：S4本轮已明确授权最多16生成／8192输出／65536预算／0目录，最终5/16（3知识＋2命名）全部完成，0目录，A1／B1／A2、同名组命名及基线冲突采用闭环通过，剩余11不挪其他阶段，详见[S4真实记录](docs/implementation/s4-supplier-acceptance.md)。S1仍7/8生成、1/1目录、余1未用。S3最终 **11次（4回答＋1标题＋6摘要）／12上限、0目录、余1未用**。末次原U3摘要1675字符，回答五项正确，包含备用电池禁令、D2更正、人数分类、运输未决；prepared实际引用新缓存，估计9754≤10600，而原文10769仍需压缩。此前截断、过长、漏禁令和预算拒绝全部保留，不概括为普遍无损。9父任务／6源尝试／4缓存／4prepared／0活动、FK空，原问题和旧行不变；应用正常关闭，未读取凭据值或采集推理原文。
- 下一步：按[S5计划](docs/implementation/s5-network-plan.md)，以S4已过审与本轮验收源`790b000`为起点，依次实现元数据关系与布局事务、确定性布局与视口、工作区会话、两阶段离开及原生混合网络控件；上下文摘要不作为知识总结或网络节点。S3剩余1次与S1剩余1次不挪作S4；新阶段真实付费材料和额度另行说明。Windows、取消账单语义、长期质量与课程／发行验收仍独立。共享需求／架构与main无差异，未借用另一轨道代码、未推送或合并。

- 2026-10-25：已同步 `main` 的文档归置与路线说明；正式代码、测试和脚本后续按需进入 `src/`、`tests/`、`scripts/`。

## 对需求或架构的疑问

（发现文档有错或缺失时记在这里，由用户决定是否更正 `main`。）
