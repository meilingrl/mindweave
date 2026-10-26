# S3 上下文预算与压缩实施计划

> **For agentic workers:** 使用 `subagent-driven-development`，逐任务 TDD、自审、提交与独立任务审查，再做完整 S3 差异审查。既有需求／架构及连续开发授权已覆盖本阶段；真实付费调用、范围变化与系统权限另行提出。仅修改稳健轨。

**Goal:** 长历史超出配置预算时，保留关键原文和近期完整问答，用可追溯、可复用的结构化摘要替换较早材料；失败保留问题，不静默截断、不改变分支身份。

**Architecture:** Application 的纯规划器决定实际材料与分段；Infrastructure 在 v5 新表保存不可变计划、压缩尝试、完成摘要和最终请求使用清单。既有回答任务分为压缩前置阶段与回答阶段，每个真实请求结束并释放槽后才推进，等待依赖时不占网络槽。Desktop 只投影状态与轻量提示。

**Tech Stack:** 沿用 .NET 10、Avalonia 12.1.4、LiveMarkdown 2.5.1、xUnit v3 3.2.2 与 Microsoft.Data.Sqlite；不新增 tokenizer／供应商 SDK、WebView 或新协议。

**Spec:** [需求](../requirements.md) §6.2–6.3、§9、§11；[架构](../architecture.md) §6、§7、§12；[详细交接](../reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md) S3。现行架构已决定“关键原文＋近期完整问答＋较早历史结构化摘要”，本计划只细化工程，不重新审批产品。

## 设计与取舍

这是既有架构中尚未实现的子系统。采用按原消息分段、完成摘要缓存、回答任务阶段式调度。把压缩直接放进 Send 的接受锁会阻塞取消／退出；另建占槽父任务等待子请求容易自锁，均不采用。压缩阶段执行一个真实请求才占槽，完成后释放，持久保存成功再重新排入回答或下一压缩阶段。

原材料本来在保守预算内时直接发送，不为“提前优化”增加压缩调用。超限时保留当前问题、分支选文、直接来源回答及其提问、最近一组完整自身问答，再按剩余预算尽量保留更多近期完整问答（有则保留整组终态原文；Stopped／Failed／Interrupted 仍保留其状态，不能伪装 Completed）；其余材料全部进入摘要覆盖范围，不按固定轮数丢弃。若必要原文自身就超限，接受前明确拒绝，草稿不变。较早有效要求、纠正、疑问和指代要求出现在结构化摘要内；模型是否忠实保留是另行真实质量验收，结构校验不冒充语义证明。

预算统一使用版本化保守估算器：沿用每 UTF-8 字节一估算 token、每条消息 32、固定安全余量 256 加完整最大输出预留。它是缺少可靠对应 tokenizer 时的 fallback，会比供应商实际计数更保守，不声称准确 token 数或模型容量；配置仍由用户给出。官方 [DeepSeek token 说明](https://api-docs.deepseek.com/quick_start/token_usage/)说明字符换算仅近似、实际以 usage 为准，本阶段不把该近似比例当硬容量保证。估算须对真实请求（含引文、摘要提示和 JSON 包装）执行，预算计算集中复用。

## Global Constraints

- 产品代码只在 `track/codex-steady` 的既有 worktree；不改共享 requirements.md／architecture.md，不借用另一轨道代码，不推送、合并或发布。
- Core／Application 不引用 Avalonia／SQLite；正式纯原生 Avalonia；不新增依赖或供应商 SDK，不自动切换模型、服务或凭据。
- 保留全部原消息、S2 直接来源／冻结祖先身份与阅读位置。只读冻结祖先＋自身有效历史，不引入兄弟、其他根会话或父会话后续消息；重试排除规则只作用于当前自身回答。
- 当前问题、分支选文、直接来源回答及其提问、最近一组完整自身问答原样保留；较早材料全部由有序原文切片覆盖，不能静默丢弃。必要原文超限时接受前拒绝并保留草稿。
- 原文在预算内直接发送。压缩只在原文超限且可规划时触发；压缩失败／结构无效／结束不完整／结果仍超预算，保留原消息与问题，明确允许重试，不派发超预算回答。
- 压缩只基于原消息，不对摘要再摘要。缓存按目标会话、原消息顺序／ID／修订／正文 hash／切片、提示版本、估算 profile、完整生成配置身份确定，范围或身份变化不可错用；未变完成分段可复用。
- 全局最多两个物理 AI 请求，槽保留到实际枚举及 DisposeAsync 结束；用户回答及其必要压缩优先于标题。等待保存或依赖时不能占槽等待自身子任务；停止、关闭、保存失败、迟到分块与重启不重发的语义保持 S1。
- 压缩用当前回答接受时冻结的同一服务／模型／凭据，输出上限 `min(1024, configuration.MaxOutputTokens)`；每个真实请求的输入＋输出预留均先过同一估算。完整最终请求再次校验后才外呼。
- 压缩摘要只有完整 `finish_reason=stop`、非空、协议有效且持久保存成功后才可使用；部分或失败输出不进有效缓存，不出现在用户回答正文。配置说明须告知额外调用、等待和费用。
- v1–v4 迁移 SQL 不改写；只追加 v5 新表，不重建旧 generation_task。回答／标题种类不变，压缩尝试独立记录并关联回答任务；最终请求保存实际原文／摘要使用清单，不能只保存概念上的全部原消息 ID 来冒充实际输入。
- 本计划不新增真实付费调用。S1 额度已用 7/8 生成、1/1 查询；工人不访问真实钥匙串、S1/S2 原生库、外部服务或另一轨道。自动化只临时数据库／受控 transport／loopback；后续原生用独立 `artifacts/s3-native`。
- S4 知识总结、候选／已读版本、网络节点、删除归档与导出不实现。上下文摘要不成为知识总结或网络节点；Windows／真实长会话质量与本地工程验证分开。

## Shared Contracts

Task 1 在 Core/Chat 增加不可变记录；名称可作一次有记录的工程微调，但必须在报告给出精确公共 API，后续任务只消费最终接口：

- `ChatContext` 增加可选 `Guid? DirectSourceMessageId = null`，旧调用兼容。它是直接来源消息身份，不能从 quote 搜索猜测。
- `ContextSourceSlice(Guid MessageId, long Revision, string BodyHash, int Start, int Length)`：UTF-16 范围，禁止拆开 surrogate pair；hash 覆盖完整原消息。每份摘要覆盖有序原文切片，原文可重建。
- `ContextChunk(string CacheKey, IReadOnlyList<ContextSourceSlice> Slices, int MaxOutputTokens)`。
- `ContextPart` 的两种形式：`VerbatimContextPart(Guid MessageId)` 和 `SummaryContextPart(ContextChunk Chunk)`。
- `ReplyContextPlan(int FormatVersion, string EstimatorProfile, IReadOnlyList<Guid> OriginalMessageIds, IReadOnlyList<ContextPart> Parts, Guid? DirectSourceMessageId, string? SourceQuote)`；Parts 保持原文顺序，原消息要么整条原样，要么按无重叠连续切片完全覆盖。
- `ContextSummaryContent(string Background, string Requirements, string Corrections, string OpenQuestions, string References)`：五字段齐备的 JSON 对象，仅接受字符串，禁止未知／重复字段；可空字段表达无此项，但整体不能空白。UTF-8 payload 上限 65536 字节。该结构是校验与提示契约，不是模型忠实度证明。

Task 1 在 Application/Chat 新建纯 `ReplyContextPlanner`、`ContextSummaryProtocol`（可分 focused 文件）。公共能力包括：从已按 S2 重试规则准备、且末尾包含当前提问的 ChatContext 生成计划；依原消息创建压缩 ChatRequest；校验／序列化结构摘要；按计划与完成摘要组合实际 ChatInput；版本化预算估算与准确 Fits 判断。保留 ChatRequestBudget 已有调用，避免复制一份估算逻辑。计划与 request 的具体方法签名由 Task 1 报告锁定。

Task 2 给 `GenerationWork` 增加可选计划，不改变旧字段含义：InputMessages／GenerationRecord.InputMessageIds 仍是逻辑原消息清单；最终物理请求使用另外的不可变清单。新增 `IContextStore` 并让 IChatStore 继承，存储实现及测试代理转发。公开能力须覆盖：按回答任务读计划；读完成缓存；幂等开始指定计划片段的压缩尝试；单调修订写尝试／完成摘要；读尝试；组成并事务记录最终物理请求。具体签名由 Task 2 报告作为 Task 3 交接。缓存保存生成配置、实际模型／尝试身份与时间，只有成功完成摘要可命中。

Task 3 只在既有 GenerationCoordinator 增加阶段状态（可拆 Context partial）。待压缩的父 Reply 保持排队／运行业务状态，运行阶段先进行必要压缩，再回答；每个阶段的物理请求释放槽后重新调度。GenerationSnapshot 可用兼容默认字段增加阶段、轻量压缩状态／完成提示，禁止把摘要 token 投影到正文。

## Task 1: 纯预算规划、原文分段与摘要协议

**Files:** Core/Chat/ChatContext.cs、新增 ContextPlan.cs（必要按模型拆分）；Application/Chat/ChatRequestBudget.cs、新增 ReplyContextPlanner.cs、ContextSummaryProtocol.cs；tests/Mindweave.Tests/ContextPlannerTests.cs。不修改持久化、协调器调度、UI 或依赖。

**Interfaces:** 产出 Shared Contracts 的纯类型与规划／请求组合 API，旧 ChatContext／ChatRequestBudget 使用兼容。此时不接通生产自动压缩。

- [ ] RED：构造 A→B→C 的具体终态消息与旧尝试身份，验证原文 Fits 时完全原样、不造摘要；over-budget 时必要原文完整，较早材料按顺序全覆盖；选中旧祖先保留；当前问题或必要来源自身超限明确拒绝。
- [ ] RED：多字节中文、emoji、极长旧单条消息分段、空文本、预算整数边界；每个压缩请求含提示／源身份包装后都 Fits；不能拆坏 UTF-16／重复覆盖或漏片段。只读既有固定原消息，不从动态祖先重组。
- [ ] 实现纯规划：先保护必要材料，再按实际剩余预算保留更多近期完整问答；较早材料按预算分段，单条过长可切片；全部分段预检不可行则拒绝，不能先做部分付费调用后才发现规划本身无解。
- [ ] RED→GREEN：缓存 key 对相同材料稳定，顺序／正文 hash／修订／切片／目标会话／配置／prompt或estimator版本变化失效；只追加后续消息时不使未变旧分段无故失效。摘要请求只包含原片段，没有已有摘要输入。
- [ ] 实现五字段结构协议与严格大小／JSON 校验；组合时摘要明确标识为引用背景，来源选文仍保留独立原文提示；最终 Fits 不通过明确失败，不截摘要或来源来凑数。结构无效、空白、截断、太长摘要的聚焦测试先 RED 再 GREEN。
- [ ] 聚焦 ContextPlannerTests、完整 Release 测试与构建一次，自审并提交；报告所有精确 API、算法边界、RED／GREEN 命令输出及尚未接通部分。不得执行供应商／原生操作或另派代理。

## Task 2: v5 压缩计划、尝试与实际使用清单持久化

**Files:** Core/Chat 压缩尝试／缓存身份记录与 GenerationWork 可选计划；Application/Chat 新增 IContextStore.cs、ChatContracts.cs；Infrastructure/Storage/DatabaseSchema.cs（仅 v5）、SqliteWorkspaceStore.Context.cs、Generation.cs、新增 ContextCompression.cs／ContextRequests.cs；tests/Mindweave.Tests/ContextStorageTests.cs，必要既有测试代理接口转发。不实现网络调度／UI。

**Interfaces:** 消费 Task1 精确 API；SqliteWorkspaceStore 产出上述 IContextStore。现行 AcceptReplyAsync 签名不变，事务内按实际新 user 身份／重试原消息重新规划；返回包含计划的 GenerationWork。ReadContext 提供已持久直接来源 ID。既有原始输入 ID 清单不改变含义，另外记录实际 request 和每份摘要使用 ID。

- [ ] 真实 SQLite RED：v4 升级与备份仍含 v4 无新表；旧迁移字节未变；接受回答时原问题、回答占位、逻辑输入和计划同事务，规划／写入故障全部回滚，草稿不被清空。
- [ ] v5 新表持久完整版本化计划、压缩尝试及完成缓存、最终请求材料和原文／摘要使用 ID；新表 FK／唯一键和事务重验覆盖实际归属、活动父任务、精确计划片段和单调修订。无须改 generation_task 的旧 kind CHECK。
- [ ] 相同回答＋片段幂等开始只回同一活动尝试；完成缓存可复用，失败／Stopped／Interrupted／部分结果不能命中；完成写入必须 `stop` 路径传入的有效协议，缓存与尝试完成同事务。终态后迟到结果不能复活或替换缓存。
- [ ] 真实库验证 A→B→C、祖先重试、兄弟／他工作区／配置变更／缓存损坏拒绝；缓存范围 hash 与实际消息重验，不能将任意 key 或伪造外部摘要塞进实际请求。只从已接受固定 ID 重建原文，不重跑 LatestInput 抹去旧尝试。
- [ ] 最终物理请求在事务内重验必要原文、完成摘要和最终预算，幂等保存完整实际材料及使用清单；不完整或超限拒绝。重开可核对确切原文和摘要身份，原消息／来源／阅读记录字节与关系不被压缩改写。
- [ ] RecoverInterrupted 同时处理未结束压缩尝试与父 Reply，不重发；保留完成缓存。SQL ABORT／ROLLBACK 覆盖摘要完成与实际材料迟失败，不能只测调用计数。
- [ ] 聚焦真实存储、完整 Release 测试／构建一次，自审提交，报告精确接口给 Task3；不调用真实凭据／供应商／原生库，不另派代理。

## Task 3: 两槽内的压缩前置阶段与恢复

**Files:** Application/Chat/GenerationCoordinator.cs、Scheduling.cs、Persistence.cs、新增 Context partial、GenerationSnapshot.cs；必要 ChatContracts／IContextStore 小修与测试代理；tests/Mindweave.Tests/ContextCoordinatorTests.cs、必要 GenerationCoordinatorTests。只应用层调度，不改 UI、传输协议或依赖。

**Interfaces:** 消费 Task1纯规划／协议和Task2真实存储 API。接受预检与事务重检一致；原文 Fits 保持原来的 Reply 路径。压缩前置阶段的物理请求结束释放槽，完成摘要保存成功后再准备下一阶段／最终 Reply；没有占槽父任务等待子任务。

- [ ] 受控真实通道＋SQLite RED：两个会话均需压缩时可推进、不死锁；第三个普通用户回答等待物理槽，必要压缩／用户回答优先于标题。包括适配器忽略取消／延迟 Dispose 时严格两个真实请求上限。
- [ ] 每次接受冻结配置和原始输入；缓存命中不外呼，不命中只发原消息片段，所有片段完成并保存后使用存储返回的已记录实际 request；不从内存临时重新拼出另一份外呼材料。
- [ ] 压缩 token 不追加到 AI 回答；阶段进度只在 snapshot 的专门字段。空白／无 stop／无效 JSON／失败／结果不够短时父任务明确失败，保留问题和原文；没有部分摘要回退或静默去掉来源。
- [ ] 停止与退出同时终止父任务和相关活动尝试，迟到分块／完成回调不进入缓存或派发回答。页面切换不停止；重开所有未完任务 Interrupted、不自动重发，完成缓存仍可供显式 Retry 复用。
- [ ] 保存失败注入分别覆盖压缩尝试开始、完整摘要提交、最终材料保存和父终态保存；全局 gate 暂停新接收／派发。内存保留未保存结果，显式修复按依赖顺序落盘成功后才继续，不通过 FlushPending 绕过外部 gate。
- [ ] 保留 S1 自动标题资格与128 token上限、S2分支原子性／读取身份／阅读保存语义；补必要回归而不跳过旧探针。聚焦+完整 Release+构建一次，自审提交并报告精确阶段投影和故障结果，禁止另派代理。

## Task 4: 桌面提示、本地质量样例与阶段验证

**Files:** Desktop/ViewModels/ChatViewModel.cs、MessageViewModel.cs 或 focused context projection、Views/ChatView.axaml／SettingsView.axaml；fixtures/chat_sse_fixture.py 及自测；tests/Mindweave.Tests/ContextDesktopTests.cs；README／TRACK／s3-verification由控制器最终维护。不更改共享需求／架构。

**Interfaces:** 消费Task3阶段snapshot与持久实际材料；维持Main唯一ContentStack，不在View中发生成／压缩请求。夹具只增加明确本地模式，不能作为生产假响应。

- [ ] Headless 真库 RED：压缩中显示准备上下文、可停止，摘要不进入回答正文；成功轻量提示“较早对话已压缩，原记录保留”；普通重入／重开能从持久使用记录说明该回答使用摘要。失败保留问题并给可理解的重试／缩小来源／调整预算提示。
- [ ] 设置说明长历史可能增加压缩请求、等待及费用、同一服务，不逐次弹确认；预算文字改为版本化保守估计，必要原文超限不发送。辅助功能命名表达状态，不只靠颜色。
- [ ] 扩展明确 `fixture-context` 本地协议夹具：识别压缩提示返回固定五字段有效结构；最终回答能暴露是否收到预期来源／纠正／指代测试标记，供协议检查；另有无效摘要与中途失败模式。必须明确合成，不冒充模型语义理解；Python协议自测验证请求与完整SSE。
- [ ] 自动集成用预置合成长会话触发摘要，核对最终物理 request 的来源、较早纠正、未解决疑问、指代标记与已记录 cache IDs 一致；改变／重试相应范围失效，未变缓存复用不增加压缩外呼。故障不丢原始数据／草稿／关系。
- [ ] 聚焦Headless、完整Release、构建和夹具自测，自审提交并独立审查。控制器最终重新执行完整检查，并单列完整S3审查及macOS独立原生走查；真实长会话质量、Windows实机另列，不伪造结果。

## 完成与验证边界

计划分为四个可独立否决的门禁：纯规划／协议、真实存储、实际物理请求调度、桌面闭环。共享文件严格顺序修改。已有 S2-M1 视口性能与 S1-F3 极端修订号 P3 保留，不扩展本阶段为无关修复。

实施前控制器自查每条架构要求均有任务：原文与祖先边界1/2；预算与覆盖1/2；摘要复用与生成身份1/2；两槽与取消恢复3；费用提示与可见失败4；真实质量与平台边界由控制器独立记录。没有新产品决策，无外部付费许可被计划自动扩大。
