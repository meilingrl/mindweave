# S4 版本确认操作与原生夹具审查 · 2026-10-28

初版`57ba68e`独立审查发现2项Important：删除丢失确认后跨页修复的新输入恢复面不可达，以及正常删除后最终读取期间迟到输入丢失。修复`a556d24`限定复审已关闭原2项，新增N1多页面恢复owner／导航问题，`5a9f74e`修复N1并经fix2限定复审关闭，全部open0、无新问题。最终总结界面联合58通过、完整654通过／2原有跳过、build零警告错误、Python15通过；初版648通过的历史证据保留。原生、真实服务、Windows及整阶段审查仍独立。

## 初审

❌ Spec compliance: Issues found — Task 8 尚不能通过。两个未覆盖的 await／修复组合会使删除后新输入失去可访问的恢复界面，其中一个允许关闭成功而丢失输入；违反本任务明确的输入保护、预期业务状态与关闭门禁要求。

## Strengths

- `src/Mindweave.Desktop/ViewModels/SummaryViewModel.Confirmation.cs:20`、`:53`、`:70` 与 `SummaryViewModel.Operations.cs:132`：预览冻结实际 node token、候选／历史目标、删除集合与回退后果；确认前再次检查本地草稿，业务冲突重建预览而不把新 token 当作旧同意。`tests/Mindweave.Tests/SummaryVersionDesktopTests.cs:17`、`SummaryVersionDesktopTests.Edges.cs:16` 验证连续基线变化后仍需新确认。
- `src/Mindweave.Desktop/ViewModels/SummaryViewModel.Operations.cs:184`、`:195`、`:211`：未知结果修复先读取原目标的实际效果；未落库时只重放原 token，已有新输入时转入草稿解决，而非重新采用另一个候选。`tests/Mindweave.Tests/SummaryVersionDesktopTests.Lifecycle.cs:21`、`:45`、`:105` 检查真实 SQLite 变更、丢失确认返回与调用次数。
- `src/Mindweave.Desktop/ViewModels/SummaryDraftSession.Resolution.cs:31`、`:50`、`:78` 与 `SummaryDraftSession.cs:92`：草稿解决以显示过的 token／revision／完整文本为准，已确认丢弃推进 tombstone，期间较新输入取得新 lease；保存不把 flush 期间出现的新文本误当作用户刚确认的文本。`tests/Mindweave.Tests/SummaryVersionDesktopTests.Drafts.cs:11`、`SummaryVersionDesktopTests.Lifecycle.cs:124` 覆盖无效正文、取消与丢失确认返回。
- `src/Mindweave.Desktop/ViewModels/SummaryViewModel.Operations.cs:233`、`src/Mindweave.Desktop/Views/SummaryView.axaml:27`：具体页面内确认与多选删除沿用既有业务页，没有新 schema／操作日志／独立历史框架；删除当前不要求先设另一版，显示名与内容标题分开。`tests/Mindweave.Tests/SummaryVersionDesktopTests.cs:58`、`:78` 检查实际回退、正文清除、来源会话保留、手动名保护与命名通知。
- `tests/fixtures/summary_sse_fixture.py:42`、`:58`、`:61`：与实际 `KnowledgeSummaryInputPlanner.cs:73` 和 `NodeNamingProtocol.cs:60` 对照后，OwnConversation/Body、Members[].Member.NameIsManual/NodeId、Content.Markdown 的层级及大小写吻合；背景标记不提升为 own 内容，输出只覆盖非手动成员。`SummaryNaming.cs:7`、`:12` 的真实 record 形状也相符，未仅凭 Python 手写样例判断兼容。
- `tests/fixtures/summary_sse_fixture.py:74`、`:86`、`:115`、`:127` 与 `summary-native.md:3`、`:14`：独立 loopback、公开合成 key、继承的限长 framing、body-free 计数以及故障／截断／post-stop-malformed 模式符合本任务范围；原 S3 文件未出现在变更包中。材料明确不把合成输出当作模型质量。

## Critical (Must Fix)

- 无。

## Important (Should Fix)

### I1 — 已隐藏页面在未知删除修复中发现新输入，恢复面不可达且关闭永久受阻

- **位置：** `src/Mindweave.Desktop/ViewModels/SummaryViewModel.Operations.cs:238`–`:240`、`:260`–`:267`；与 `SummaryViewModel.cs:158`–`:164`、`MainViewModel.Summaries.cs:110`–`:111`、`MainViewModel.cs:224`、`:217` 的组合。
- **触发路径：** 最后版本删除事务已提交但确认返回丢失，同时删除 await 中到达新的草稿输入。此时 catch 保留原 pending intent，尚未执行 rebase，页面 `HasRemovedNodeInput` 仍为 false。普通导航合法地跳过 pending 版本操作的 Draft.Flush，用户可以切到来源会话；原页面随后 detached。显式 Retry 再在 `summaryPages` 中修复这个旧页面：AllDeleted 确认删除，Rebase 发现 node 已不存在且有新输入，`ShowRemovedInput()` 只把恢复面投影到已 detached 的旧页面并直接返回。
- **后果：** `FlushSummariesAsync` 接下来执行共享草稿 Flush，它抛 `SummaryInputResolutionRequiredException`；Retry 的预检查发生在上述发现之前，无法拦截这次状态转换。`GenerationCoordinator.cs:183`–`:190` 的外部修复包装把该预期输入状态再报告为持久化失败。后续 Retry／Close 被旧页面的 `HasRemovedNodeInput` 拦住；Return 只恢复当前会话。原节点已删除，`MainViewModel.Summaries.cs:49`–`:53` 的常规 route 验证不能重新打开该页，因此用户既不能看到／复制该输入，也不能点它的明确丢弃按钮来退出。当前代码中没有把此恢复面重新带到可访问位置的路径。
- **修复要求：** 在任何页面（包括 detached 页面）的原操作修复新发现此状态时，保证输入恢复面实际可访问，并在进入 runtime 外部 IO 修复之前／期间正确区分该业务状态；不能依赖仅在 Retry 开头检查已有 flag。保留原删除事实、原输入与精确 revision 同意，不复活节点；无需新通用路由框架或 schema。
- **确定性 RED 建议：** 复用 `SummaryVersionDesktopTests.Lifecycle.cs:169` 的代理，最后删除设 `Hold=true, LoseAck=true`，hold 期间注入新 Title/Markdown，release 后导航到来源会话，再 Retry。断言原节点／正文仍不存在、删除仅调用一次、实际可见界面能复制并明确解决该输入、Return 不把用户留在无恢复入口的普通会话；解决前 Close 以输入业务状态阻断，解决后可正常修复／关闭。现有 `Lifecycle.cs:145`（导航）与 `Edges.cs:111`（新输入）未覆盖这个组合。
- **证据性质：** 源码可达路径结论；本 reviewer 未运行新测试，交实现者按上述 barrier 验证。

### I2 — 正常删除确认返回后，最后一次异步投影读取仍可吞掉迟到输入

- **位置：** `src/Mindweave.Desktop/ViewModels/SummaryViewModel.Operations.cs:238`、`:249`、`:256`–`:257`；`SummaryDraftSession.Resolution.cs:116`–`:119`；`SummaryDraftSession.cs:65`。
- **触发路径：** 最后删除正常返回，不需要 IO 故障。Finish 首先调用 Rebase；此刻没有新输入时，Rebase 将 `saved=null` 并清空干净文本。之后 Finish 仍 await `LoadSummaryNodeAsync(NodeId)`（第 249 行）。若新 editor 事件在这个最终读取 await 期间到达，Draft 会有新 revision／Title／Markdown，但不会再次经过 Rebase；读取返回 null 后，第 256–257 行直接关闭确认并发布 NodeDeleted。
- **后果：** 新输入没有被标为 `HasRemovedNodeInput`，也没有可见恢复面。列表导航 Detach 虽因 IsDirty 暂时保留内存，但后续 Draft.Flush 的循环要求 `saved is not null`，对该 `saved=null + HasMemoryChanges` 状态直接成功返回。Main 的 Close 预检只看 `HasRemovedNodeInput`，所以可以成功释放窗口并丢失这份从未明确解决的新输入。这与 I1 不同：无需 lost-ack，也不需要显式 Retry。
- **修复要求：** 最后一次可能让新输入到达的 await 之后，在完成页面迁移／清除 intent／允许关闭之前再次落实输入所有权与 revision 检查；干净状态曾被清空不应令后到输入落入无 lease 且无恢复标记的状态。精确文本需继续可见可复制，节点仍保持删除。
- **确定性 RED 建议：** 最后删除正常执行；代理仅阻塞 `FinishVersionIntentAsync` 第 249 行的 **删除提交后最终 LoadSummaryNodeAsync**，不要误阻塞初始 preview，也不要仍只 hold DeleteSummaryVersionsAsync。在此 barrier 注入新 Title/Markdown，release 后运行 dispatcher／导航及 Root.Close。要求恢复面显示该精确输入、Close 在明确解决前失败且不标记 IO failure，显式丢弃正确 revision 后才可关闭，节点／版本不复活。可再用新输入刷新确认验证过期同意不能清除较新文本。
- **证据性质：** 源码可达路径结论；本 reviewer 未运行新测试。现有 `SummaryVersionDesktopTests.Edges.cs:111` 只在删除调用本身 hold，晚于 Rebase 的这个窗口不在其覆盖范围内。

## Minor (Nice to Have)

- 无独立 Minor finding；不以泛化测试覆盖建议重复上述实际缺陷。

## ⚠️ 范围与检查证据

- 固定 BASE `1dac648149ffc870512081e4f44d2ae41f1e7bf3`、HEAD `57ba68ebf3690ee4a06cd4ac0e15e659df2ea2f9`，唯一 `.superpowers/sdd/s4-summary-plan/review-1dac648..57ba68e.diff`（16 files）已从头到尾按四个有序区间读取。没有重新运行 git、修改生产／测试／index／HEAD，未派生 reviewer。
- 本任务列出的具体 Desktop 版本操作、草稿辅助、主导航投影、compiled XAML、测试与 S4 fixture 均有对应 hunk。Core／Application／Infrastructure／迁移／S3 fixture／黄金资产未改；其完整阶段约束由 Task1–7 与 whole-S4 gate 负责，本次没有扩大为重新审查整阶段。
- diff 外具名检查 1（实际协议风险）：读取真实 planner 的 Compose、KnowledgeSummaryProtocol 的 instruction/parser、NodeNamingProtocol 的 builder/parser、SummaryNaming record；核对 Python 字段层级、指令分类、精确非手动输出与规范化规则。额外查自动标题 instruction（`GenerationCoordinator.Scheduling.cs:8`），确认 source chat／title 分类可供原生操作使用。没有执行 HTTP 或声称真实 Core→fixture 端到端已验收。
- diff 外具名检查 2（草稿 lease／删除后输入生命周期风险）：因 diff 将 BeginAsync、CommitAsync/FinishCommitAsync 和 SelectVersionCoreAsync/RefreshMetadataAsync 的关键函数截断，补读 `SummaryDraftSession.cs:1`–`:160`、`SummaryViewModel.cs:1`–`:150` 的上下文；这两个窗口也包含了已在 diff 中出现的邻近声明。另读 `SqliteWorkspaceStore.SummaryDrafts.cs:10`–`:61` 核对真实 begin/save/discard tombstone 合同；没有继续爬 storage 其他操作实现。
- diff 外具名检查 3（隐藏恢复面／错误分类风险）：定向查看 Main 的 Navigate/OpenRequest/AddDetail/GoBack、Summary route validation、background refresh 与 Return；再读 `GenerationCoordinator.cs:177`–`:190` 的外部 repair 包装，验证 I1 的可访问性与错误来源路径。新增代码引用的这些跨组件调用点直接支撑 I1/I2，没有 broad repo review。
- diff 外具名检查 4（继承 HTTP helper 安全与旧行为隔离风险）：查看 `tests/fixtures/chat_sse_fixture.py:29`–`:132` 的 context、authorized、禁日志和 bounded Content-Length/chunked helper，确认 S4 复用的真实实现；未改旧 helper 或资产。
- 最终 full648 passed／2 original CredentialVault skips、build 0 warning/error、Python15 与 focus52 为控制器已核验的同源证据，本 reviewer 不重复 suite／stress／编译；上述两条新组合疑点不被既有绿色结果覆盖。没有将既有 credential 环境跳过或明确标记的早期 setup RED 误判为 Task8 新回归。
- `tests/fixtures/summary-native.md:14`、`:38` 与任务 brief 中的 native／包装／Windows／真实收费服务／模型语义／课程验收仍由控制器另行完成；本报告不为这些门禁背书，whole-S4 固定范围审查也仍待进行。

## Assessment

**Task quality: Needs fixes.**

精确确认、实际删除、未知结果与原 token 修复的主要结构有明确职责且具真实 SQLite／compiled-control 证据；但删除后输入保护目前只覆盖了较早、仍可见的页面路径。I1 的不可达恢复状态与 I2 的最终 await 后输入丢失都属于本任务承诺的正确性问题，应先用上述最小确定性测试闭环，再交回 Task8 门禁。


## Fix1限定复审

- **I1 — 已隐藏页面在未知删除修复中发现新输入，恢复面不可达且关闭永久受阻** — **ADDRESSED**。`src/Mindweave.Desktop/ViewModels/MainViewModel.Summaries.cs:11–20,99,121–131` 将任意 retained page 的恢复状态投影到 Main，并在 flush 内只截获预期输入结果、继续实际写入；`MainViewModel.cs:221–225,240–253` 在真实外部/runtime repair 完成后才抛出预期输入状态。`Views/MainView.axaml:71–94` 提供实际 Main overlay、只读可选中文本及独立 IO Retry。`SummaryViewModel.Operations.cs:285–291` 和 `SummaryDraftSession.Resolution.cs:37–44` 同步匹配原 revision/token/title/body，不等待异步导航锁。`tests/Mindweave.Tests/SummaryVersionDesktopTests.Recovery.cs:16–62` 的两个 theory case 覆盖 lost ACK → 新输入 → detached/source Chat → Retry，包含独立 selection IO 仍失败、可见 Retry、Return、过期同意不清较新输入、单次删除、无节点/正文/阅读记录及最终关闭。新增的多页面选择问题见 N1，不等同于原来的“根本无可达恢复面”。
- **I2 — 正常删除确认返回后，最后一次异步投影读取仍可吞掉迟到输入** — **ADDRESSED**。`src/Mindweave.Desktop/ViewModels/SummaryViewModel.Operations.cs:248–250,268–270` 在 final projection await 前登记删除，在 await 后重新检查输入再清 intent/发导航；`SummaryDraftSession.Resolution.cs:18–27` 和 `SummaryDraftSession.cs:33` 将 removed marker 留在共享 session，清楚区分无 saved lease 和后到内存输入。明确丢弃不清掉 marker。`MainViewModel.Summaries.cs:102` 的导航回调再次检查共享输入。`tests/Mindweave.Tests/SummaryVersionDesktopTests.Lifecycle.cs:179–208` 的 barrier 是已提交删除后的最终 node read；`SummaryVersionDesktopTests.Recovery.cs:67–104` 覆盖该 barrier、close 输入 gate，以及完成 discard/list projection 后再次输入的所有权，不只 hold 删除本身。

### New Breakage in the Fix Diff

- **N1 — Important：Main 恢复面绑定任意旧页面，恢复命令继承旧 quiesce 状态，且共享丢弃无法完成活动页面的删除导航。** 新增位置：`src/Mindweave.Desktop/ViewModels/MainViewModel.Summaries.cs:15` 的 `summaryPages.FirstOrDefault(page => page.HasRemovedNodeInput)`；联动 `SummaryViewModel.Operations.cs:68–77,285–291`、`MainViewModel.cs:229–230`。这不是丢失输入或节点复活，而是新 Main overlay 的操作 owner 与当前页面/关闭生命周期不一致。
  - **普通多实例可达时序：** 打开节点 N 得到 P1，切到 Chat，再打开 N 得到 P2。Main 保留 P1/P2，二者使用同一个 Draft（`MainViewModel.Summaries.cs:93–99,107`）；P1 detached。P2 最后删除时注入迟到输入，新的 Draft 事件让 P1 和 P2 都 `HasRemovedNodeInput=true`。恢复面没有优先选择 P2，也没有独立恢复 owner，集合首个匹配可为旧 P1。在该 overlay 上确认精确输入后，`Draft.DiscardRemovedInput` 同步让所有页面 handler 清掉 flag/intent，但 handler **不发布 NodeDeleted**；最后仅 P1 的 `DiscardRemovedInputAsync` 发布。Main 对 P1 的回调在 `MainViewModel.Summaries.cs:102` 因 `ActiveContent != page` 直接返回。P2 因共享 handler 已清 intent，也不会在后续 repair 中完成原删除导航；用户留在已删除的空 Summary 页，而非预期 Network 列表。需手动离开才能恢复正常导航。
  - **更严重的关闭恢复变体：** 在上面的打开 P2/删除之前，先让一次 Close 因真实 selection 写入失败而返回；`MainViewModel.cs:203` 已 Quiesce 所有 retained pages，Return 的新循环只 Resume active 或当时已有恢复输入的页面（`:229–230`），所以当时是干净 detached 的 P1 仍 quiescing。修复真实 IO 后正常打开 P2，再最后删除并注入新输入。新事件会将恢复面绑定到仍 quiescing 的 P1，但 `CanDiscardRemovedInput`（`SummaryViewModel.Operations.cs:50`）为 false；`DiscardRemovedInputAsync:287` 每次直接返回。外层按钮仅绑定 `Main.IsInteractionEnabled`（`Views/MainView.axaml:88–90`），因此可见且启用却无动作；Retry 不会解除 P1 quiesce。用户必须额外尝试 Close，再 Return，才能碰巧让 P1 被 Resume，界面没有说明这一绕行。
  - **源码证据界限：** 未新增运行或修改测试；以上是准确 caller/event/guard 的可达路径结论。现有 3 个 recovery case 都只创建一个 Summary 页面实例，故其通过不回答这一组合。没有将该问题夸大为永久死锁或已证实数据丢失。
  - **最小确定性验证建议：** 复用 RecoveryRoot/OperationStoreProxy；显式创建同一 node 的 P1→Chat→P2，assert P1/P2 Draft 同一对象；hold P2 最后删除并注入新输入，release 后通过实际 Main overlay 点击精确 discard（必要时先刷新旧 consent），drain dispatcher，断言所有页面无恢复 flag、ActiveContent 已为 SummaryList、删除调用恰一次。第二 case 在打开 P2 前用 `FailSelection` 制造 Close 失败，Return 后解除 fault 并 Retry，再走同一路径，要求实际 overlay 的确认按钮在正常工作台中可完成解析，无需第二次 Close/Return。修复需让具体恢复 owner 的可操作状态跟随 Main，并让共享 resolution 完成当前受影响页面的导航；不能只清旧页 flag。

### Out-of-Scope Observations

- None。未扩大到整项 Task8、全 S4、fixture 或旧需求/架构；未提出 diff 外阻塞项。

### Checks

- 唯一 review package 为 `.superpowers/sdd/s4-summary-plan/review-57ba68e..a556d24.diff`，固定 `57ba68ebf3690ee4a06cd4ac0e15e659df2ea2f9` → `a556d24cf69dcca0eacbcb770c738d5f0dd2db1b`，8 files / 266 insertions / 18 deletions；按两段一次读取，没有重新生成 diff。
- 已读 brief、constraints、逐字 I1/I2、Fix round 1 append 及完整最新 API handoff；report 和 pattern ruling 仅作为主张/约束，逐项与代码和测试核对。
- 具名风险所需未改 caller：`SummaryViewModel.cs:48–106,149–186`（同 Draft、load/drain/detach/quiesce）；`SummaryViewModel.Operations.cs:150–237,299–309`（intent 修复、清正文与事件）；`SummaryDraftSession.cs:43–150`（lease/flush/revision）；`MainViewModel.Summaries.cs:60–88` 与 `MainViewModel.cs:295–345,405–416`（route/selection/创建页/失败 gate）；`RootViewModel.cs:40–51,78–96`（Close/Return）；`GenerationCoordinator.cs:177–189`、`AppGenerationRuntime.cs:55–94,108–131`（外部 repair、participant repair、新 failure epoch）。未进行非具名广泛 caller 扫描。
- 实际 IO 与 epoch：新 expected-state defer 不吞真实 IO；`GenerationCoordinator` 仍按 capture epoch/`TryClear(epoch)` 清理原 source；runtime 继续检查其他 participant/新 epoch。独立 selection failure 测试对应真实失败留存与后续 Retry。未发现 fix 新增的 epoch 清理错误。
- 读取现有 raw summary：`08-final-focus.log:9` 55 passed/0 failed；`09-final-release.log:13` 651 passed/2 skipped/0 failed；`10-final-build.log:7–9` 0 warnings/errors；`11-final-python.log:3–5` 15/OK。与新增测试实现及报告一致；控制器已完整读取 raw。**本 reviewer 未运行测试、suite、stress、编译或原生操作**。
- 仅写本报告；未改 source/test/index/HEAD、其他报告或控制器文档。无 subagent、真实用户库/vault、远端 HTTP、费用、push/merge 或原生验收。

### Verdict

- **Fix round: Findings remain open — N1 (new Important breakage).** 原 I1/I2 均 ADDRESSED；新增 Critical 0 / Important 1 / Minor 0，out-of-scope 0。需要对 N1 做最小多页面 owner/lifecycle 修复及限定复审；不能据此声称 Task8/整阶段/原生/用户验收通过。


## Fix2 N1限定复审

- **N1 — Main 恢复面绑定任意旧页面，恢复命令继承旧 quiesce 状态，且共享丢弃无法完成活动页面的删除导航 — ADDRESSED。** 本轮仅核对 N1 与 fix2 新破坏；原 I1/I2 已在 fix1 复审关闭，不重新裁决。固定范围为 `a556d24cf69dcca0eacbcb770c738d5f0dd2db1b` → `5a9f74e68a58af293637e4c5ffa2282f49a73ae4`，唯一 `review-a556d24..5a9f74e.diff`（12372 bytes，3 files，72 additions / 3 deletions），已一次分段读取，未重跑 git 或扩大至 Task8/S4 全量复审。
- **N1 普通 P1 → Chat → P2 路径 — ADDRESSED。** `src/Mindweave.Desktop/ViewModels/SummaryViewModel.Operations.cs:71–81` 在 Draft 的同步 clear 事件中为每个仍有恢复标记的页面清除 flag、精确输入快照、pending intent 和确认面，再各自发出 `NodeDeleted`；`:287–293` 移除 initiating-page 私有完成通知，不重复发送。Main 的 `src/Mindweave.Desktop/ViewModels/MainViewModel.Summaries.cs:105–112` 仍只接受当前活动页面且共享 Draft 没有新输入的回调，所以旧 P1 owner 不再阻止活动 P2 返回 SummaryList，Chat 也不会被旧页面的通知带走。
- **N1 先前 Close 真实 IO 失败后的旧 quiesced owner — ADDRESSED。** `src/Mindweave.Desktop/ViewModels/MainViewModel.Summaries.cs:14–19` 只在选中确有 `HasRemovedNodeInput` 的恢复页面且 Main 非 closing 时调用 `ResumeAfterClose()`；`src/Mindweave.Desktop/ViewModels/SummaryViewModel.cs:180–181` 清除旧 quiesce 并刷新可用性。`src/Mindweave.Desktop/ViewModels/SummaryViewModel.Operations.cs:294–299` 在该页仍有恢复输入时直接返回，不加载已删除保存正文。Main closing 时不会由新 projection 恢复页面，原 close 的 quiesce / drain 保持 `src/Mindweave.Desktop/ViewModels/MainViewModel.cs:199–207` 的所有权。
- **N1 精确 consent / late input 边界 — 保持。** `src/Mindweave.Desktop/ViewModels/MainViewModel.cs:43–49` 仍同步取得具体 owner 并转发 discard，无 await 后替换 owner 的 consent 窗口；`src/Mindweave.Desktop/ViewModels/SummaryViewModel.Operations.cs:277–290` 保留旧 snapshot 与失配时仅刷新确认的行为。`src/Mindweave.Desktop/ViewModels/SummaryDraftSession.Resolution.cs:37–44` 同步按 token / revision / title / markdown 精确匹配后广播 clear；`src/Mindweave.Desktop/ViewModels/SummaryDraftSession.cs:29–34` 的永久 removed marker 继续将后到输入留在内存并重新发事件。Main posted 删除回调再次检查 `Draft.HasRemovedNodeInput`（`MainViewModel.Summaries.cs:108`），新输入不会被旧通知自动丢弃或导航隐藏。
- **New Breakage in the Fix Diff — None；Critical 0 / Important 0 / Minor 0。** 具名检查了三个与改动直接相连的风险：恢复调用误触发 detached 保存正文加载、共享通知导致非活动页导航或重复导航、同步 clear 后迟到输入被旧 callback 越过。所需旧 caller 仅限 Main close / synchronous forwarding / serialized callback guards、Summary resume / confirmation、Draft exact-clear / removed-input event；以上路径未发现本 diff 引入的新缺陷。`MainViewModel.cs:255–261,392–395` 保留 closing / detached guard 与序列化刷新。
- **覆盖检查 — 新三例对准 N1。** `tests/Mindweave.Tests/SummaryVersionDesktopTests.Recovery.cs:111` 新 theory 的三个 InlineData 覆盖普通多页面、真实 selection IO Close 失败后的 Return / repair、多页面 lost ACK 后在 Chat 上恢复；`:133` 通过实际 Main navigation 建立 distinct P1/P2 并断言共享 Draft，`:150–162` 点击 compiled overlay，检查 owner 可操作、双方恢复标记和 intent 清除、活动 SummaryList / Chat 正确、delete 恰一次、node / body / reading / selection 缺失及最终关闭。未发现测试绕过本轮修复的具体行为。
- **验证证据与本轮检查次数 — 未重跑测试（0），未运行 suite / stress / native / HTTP。** 已读 `task-8-report.md:151–193` 与 API Fix2 `task-8-api-handoff.md:80–86`，报告明确命名覆盖用例、命令和原始日志：RED 3 executable failures；GREEN 3 passed；focus 58 passed；full Release 654 passed / 2 原 CredentialVault skips / 0 failed；build 0 warnings / 0 errors；Python 15 passed。控制器提供其已 FULL-read 上述 raw logs 的证据，本复审未冒称独立重新执行或重新读取这些日志。diff 中的新增用例和两处产品修复与这些报告相符；阅读未提出需新 focused test 回答的具体疑问。
- **Out-of-Scope Observations — None。** 未审查未触及代码的其他 Task8/S4 行为；未授予 native、Windows、provider、model quality、费用、课程、包装或发布验收。
- **Fix round — All findings addressed, no new Critical/Important breakage。** 本轮 N1 1 / 1 addressed，open 0；新缺陷 C0 / I0 / M0；唯一写入为本复审报告，未改 source / tests / index / HEAD / branch。
