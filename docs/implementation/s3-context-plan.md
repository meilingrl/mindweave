# S3 上下文预算与压缩实施计划

> **For agentic workers:** 使用 `subagent-driven-development`，逐任务 TDD、自审、提交与独立任务审查，再做完整 S3 差异审查。既有需求／架构及连续开发授权已覆盖本阶段；真实付费调用、范围变化与系统权限另行提出。仅修改稳健轨。

**Goal:** 长历史超出配置预算时，保留关键原文和近期完整问答，用可追溯、可复用的结构化摘要替换较早材料；失败保留问题，不静默截断、不改变分支身份。

**Architecture:** Application 的纯规划器决定实际材料与分段；Infrastructure 在 v5 新表保存不可变计划、压缩尝试、完成摘要和最终请求引用清单／规范 hash；摘要及产生它的生成记录归属被覆盖的源会话，子请求仅保存对祖先资源的使用依赖。既有回答任务分为压缩前置阶段与回答阶段，每个真实请求结束并释放槽后才推进，等待依赖时不占网络槽。Desktop 只投影状态与轻量提示。

**Tech Stack:** 沿用 .NET 10、Avalonia 12.1.4、LiveMarkdown 2.5.1、xUnit v3 3.2.2 与 Microsoft.Data.Sqlite；不新增 tokenizer／供应商 SDK、WebView 或新协议。

**Spec:** [需求](../requirements.md) §6.2–6.3、§9、§11；[架构](../architecture.md) §4、§6、§7、§12；[详细交接](../reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md) S3。现行架构已决定“关键原文＋近期完整问答＋较早历史结构化摘要”，本计划只细化工程，不重新审批产品。

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
- 压缩只基于原消息，不对摘要再摘要。缓存按被覆盖消息的源会话、原消息顺序／ID／修订／正文 hash／切片、提示版本、估算 profile、完整生成配置身份确定，不将请求子会话身份加入 key。分段不能跨源会话边界；范围或身份变化不可错用，兄弟请求可复用同一祖先的未变完成分段，但不能混入兄弟原文。摘要及其完成生成身份必须随源会话保存；删除发起请求的叶子兄弟不能删除、破坏或阻止另一兄弟使用祖先摘要。
- 全局最多两个物理 AI 请求，槽保留到实际枚举及 DisposeAsync 结束；用户回答及其必要压缩优先于标题。等待保存或依赖时不能占槽等待自身子任务；停止、关闭、保存失败、迟到分块与重启不重发的语义保持 S1。
- 压缩用当前回答接受时冻结的同一服务／模型／凭据。初版格式2的输出上限为 `min(1024, configuration.MaxOutputTokens)`；真实服务暴露该固定上限后的修订见Task5。每个真实请求的输入＋输出预留均先过同一估算。完整最终请求再次校验后才外呼。
- 压缩摘要只有完整 `finish_reason=stop`、非空、协议有效且持久保存成功后才可使用；部分或失败输出不进有效缓存，不出现在用户回答正文。配置说明须告知额外调用、等待和费用。
- v1–v4 迁移 SQL 不改写；只追加 v5 新表，不重建旧 generation_task。回答／标题种类不变，压缩尝试独立记录并关联回答任务；最终请求保存实际原文／摘要使用清单和版本化规范 hash，重建后精确校验而不逐次复制历史全文；不能只保存概念上的全部原消息 ID 来冒充实际输入。
- 本计划不新增真实付费调用。S1 额度已用 7/8 生成、1/1 查询；工人不访问真实钥匙串、S1/S2 原生库、外部服务或另一轨道。自动化只临时数据库／受控 transport／loopback；后续原生用独立 `artifacts/s3-native`。
- S4 知识总结、候选／已读版本、网络节点、删除归档与导出不实现。上下文摘要不成为知识总结或网络节点；Windows／真实长会话质量与本地工程验证分开。

## Shared Contracts

Task 1 在 Core/Chat 增加不可变记录；名称可作一次有记录的工程微调，但必须在报告给出精确公共 API，后续任务只消费最终接口：

- `ChatContext` 增加可选 `Guid? DirectSourceMessageId = null`，旧调用兼容。它是直接来源消息身份，不能从 quote 搜索猜测。
- `ContextSourceSlice(Guid MessageId, long Revision, string BodyHash, int Start, int Length)`：UTF-16 范围，禁止拆开 surrogate pair；hash 覆盖完整原消息。每份摘要覆盖有序原文切片，原文可重建。
- `ContextChunk(Guid SourceConversationId, string CacheKey, IReadOnlyList<ContextSourceSlice> Slices, int MaxOutputTokens)`：一块只覆盖此源会话。
- `ContextPart` 的两种形式：`VerbatimContextPart(Guid MessageId)` 和 `SummaryContextPart(ContextChunk Chunk)`。
- `ReplyContextPlan(int FormatVersion, string EstimatorProfile, IReadOnlyList<Guid> OriginalMessageIds, IReadOnlyList<ContextPart> Parts, Guid? DirectSourceMessageId, string? SourceQuote)`；Parts 保持原文顺序，原消息要么整条原样，要么按无重叠连续切片完全覆盖。
- `ContextSummaryContent(string Background, string Requirements, string Corrections, string OpenQuestions, string References)`：五字段齐备的 JSON 对象，仅接受字符串，禁止未知／重复字段；可空字段表达无此项，但整体不能空白。UTF-8 payload 上限 65536 字节。该结构是校验与提示契约，不是模型忠实度证明。

Task 1 在 Application/Chat 新建纯 `ReplyContextPlanner`、`ContextSummaryProtocol`（可分 focused 文件）。公共能力包括：从已按 S2 重试规则准备、且末尾包含当前提问的 ChatContext 生成计划；依原消息创建压缩 ChatRequest；校验／序列化结构摘要；按计划与完成摘要组合实际 ChatInput；版本化预算估算与准确 Fits 判断。保留 ChatRequestBudget 已有调用，避免复制一份估算逻辑。计划与 request 的具体方法签名由 Task 1 报告锁定。

Task 2 给 `GenerationWork` 增加可选计划，不改变旧字段含义：InputMessages／GenerationRecord.InputMessageIds 仍是逻辑原消息清单；最终物理请求使用另外的不可变清单。新增 `IContextStore` 并让 IChatStore 继承，存储实现及测试代理转发。公开能力须覆盖：按回答任务读计划；读完成缓存；幂等开始指定计划片段的压缩尝试；单调修订写尝试／完成摘要；读尝试；组成并事务记录最终物理请求。具体签名由 Task 2 报告作为 Task 3 交接。缓存保存生成配置、实际模型／尝试身份与时间，只有成功完成摘要可命中。产生缓存的生成记录归属源会话，不能依赖请求子会话存活；子任务的使用／停止关系独立保存，所有跨会话业务引用仍只指向祖先。S3-ARCH-1 修复后的精确 API 以复审通过报告为准，旧目标会话 key 合同已被本条替代。

S3-ARCH-1归属修复时计划格式为2；Task5升级3，Task6升级4，Task7升级5，Task9后新接受格式为6，`Rebuild`保留2／3／4／5／6的精确策略。Task7新增按字段省略默认值的 `AiConfiguration.SummaryThinking` 与 `ChatRequest.Purpose`，旧配置／请求仍精确保持原JSON；格式5／6的摘要Purpose为ContextSummary。`CreateSummaryRequest(Guid sourceConversationId, ContextChunk chunk, IReadOnlyList<ChatMessage> originals, AiConfiguration configuration)` 的首参数仍是来源owner，调用 `attempt.SourceConversationId`；`WriteCompressionAsync` 首两参数仍为 `replyTaskId, attemptId`，独立子依赖验证请求者。原API见Task2交接，版本化预算及提示的后验见Task5／6报告。

Task 3 只在既有 GenerationCoordinator 增加阶段状态（可拆 Context partial）。待压缩的父 Reply 保持排队／运行业务状态，运行阶段先进行必要压缩，再回答；每个阶段的物理请求释放槽后重新调度。GenerationSnapshot 可用兼容默认字段增加阶段、轻量压缩状态／完成提示，禁止把摘要 token 投影到正文。

## Task 1: 纯预算规划、原文分段与摘要协议

**Files:** Core/Chat/ChatContext.cs、新增 ContextPlan.cs（必要按模型拆分）；Application/Chat/ChatRequestBudget.cs、新增 ReplyContextPlanner.cs、ContextSummaryProtocol.cs；tests/Mindweave.Tests/ContextPlannerTests.cs。不修改持久化、协调器调度、UI 或依赖。

**Interfaces:** 产出 Shared Contracts 的纯类型与规划／请求组合 API，旧 ChatContext／ChatRequestBudget 使用兼容。此时不接通生产自动压缩。

- [x] RED：构造 A→B→C 的具体终态消息与旧尝试身份，验证原文 Fits 时完全原样、不造摘要；over-budget 时必要原文完整，较早材料按顺序全覆盖；选中旧祖先保留；当前问题或必要来源自身超限明确拒绝。
- [x] RED：多字节中文、emoji、极长旧单条消息分段、空文本、预算整数边界；每个压缩请求含提示／源身份包装后都 Fits；不能拆坏 UTF-16／重复覆盖或漏片段。只读既有固定原消息，不从动态祖先重组。
- [x] 实现纯规划：先保护必要材料，再按实际剩余预算保留更多近期完整问答；较早材料按预算分段，单条过长可切片；全部分段预检不可行则拒绝，不能先做部分付费调用后才发现规划本身无解。
- [x] RED→GREEN：缓存 key 对相同材料稳定，顺序／正文 hash／修订／切片／源会话／配置／prompt或estimator版本变化失效；目标子会话变化不使共同祖先分段失效，分段不跨源会话；只追加后续消息时不使未变旧分段无故失效。摘要请求只包含原片段，没有已有摘要输入。
- [x] 实现五字段结构协议与严格大小／JSON 校验；组合时摘要明确标识为引用背景，来源选文仍保留独立原文提示；最终 Fits 不通过明确失败，不截摘要或来源来凑数。结构无效、空白、截断、太长摘要的聚焦测试先 RED 再 GREEN。
- [x] 聚焦 ContextPlannerTests、完整 Release 测试与构建一次，自审并提交；报告所有精确 API、算法边界、RED／GREEN 命令输出及尚未接通部分。不得执行供应商／原生操作或另派代理。

## Task 2: v5 压缩计划、尝试与实际使用清单持久化

**Files:** Core/Chat 压缩尝试／缓存身份记录与 GenerationWork 可选计划；Application/Chat 新增 IContextStore.cs、ChatContracts.cs；Infrastructure/Storage/DatabaseSchema.cs（仅 v5）、SqliteWorkspaceStore.Context.cs、Generation.cs、新增 ContextCompression.cs／ContextRequests.cs；tests/Mindweave.Tests/ContextStorageTests.cs，必要既有测试代理接口转发。不实现网络调度／UI。

**Interfaces:** 消费 Task1 精确 API；SqliteWorkspaceStore 产出上述 IContextStore。现行 AcceptReplyAsync 签名不变，事务内按实际新 user 身份／重试原消息重新规划；返回包含计划的 GenerationWork。ReadContext 提供已持久直接来源 ID。既有原始输入 ID 清单不改变含义，另外记录实际 request 和每份摘要使用 ID。

- [x] 真实 SQLite RED：v4 升级与备份仍含 v4 无新表；旧迁移字节未变；接受回答时原问题、回答占位、逻辑输入和计划同事务，规划／写入故障全部回滚，草稿不被清空。
- [x] v5 新表持久完整版本化计划、压缩尝试及完成缓存、最终请求材料和原文／摘要使用 ID；新表 FK／唯一键和事务重验覆盖实际归属、活动父任务、精确计划片段和单调修订。无须改 generation_task 的旧 kind CHECK。
- [x] 相同回答＋片段幂等开始只回同一活动尝试；完成缓存可复用，失败／Stopped／Interrupted／部分结果不能命中；完成写入必须 `stop` 路径传入的有效协议，缓存与尝试完成同事务。终态后迟到结果不能复活或替换缓存。
- [x] 真实库验证 A→B→C、祖先重试、兄弟／他工作区／配置变更／缓存损坏拒绝；缓存范围 hash 与实际消息重验，不能将任意 key 或伪造外部摘要塞进实际请求。兄弟可复用相同已冻结祖先范围的摘要，复用不引用生成请求的兄弟原文或依赖其存活；删除该请求叶子后，另一兄弟的已准备材料与源摘要仍可重验。只从已接受固定 ID 重建原文，不重跑 LatestInput 抹去旧尝试。
- [x] 最终物理请求在事务内重验必要原文、完成摘要和最终预算，幂等保存完整实际材料及使用清单；不完整或超限拒绝。重开可核对确切原文和摘要身份，原消息／来源／阅读记录字节与关系不被压缩改写。
- [x] RecoverInterrupted 同时处理未结束压缩尝试与父 Reply，不重发；保留完成缓存。SQL ABORT／ROLLBACK 覆盖摘要完成与实际材料迟失败，不能只测调用计数。
- [x] 聚焦真实存储、完整 Release 测试／构建一次，自审提交，报告精确接口给 Task3；不调用真实凭据／供应商／原生库，不另派代理。

## Task 3: 两槽内的压缩前置阶段与恢复

**Files:** Application/Chat/GenerationCoordinator.cs、Scheduling.cs、Persistence.cs、新增 Context partial、GenerationSnapshot.cs；必要 ChatContracts／IContextStore 小修与测试代理；tests/Mindweave.Tests/ContextCoordinatorTests.cs、必要 GenerationCoordinatorTests。只应用层调度，不改 UI、传输协议或依赖。

**Interfaces:** 消费 Task1纯规划／协议和Task2真实存储 API。接受预检与事务重检一致；原文 Fits 保持原来的 Reply 路径。压缩前置阶段的物理请求结束释放槽，完成摘要保存成功后再准备下一阶段／最终 Reply；没有占槽父任务等待子任务。

- [x] 受控真实通道＋SQLite RED：两个会话均需压缩时可推进、不死锁；第三个普通用户回答等待物理槽，必要压缩／用户回答优先于标题。包括适配器忽略取消／延迟 Dispose 时严格两个真实请求上限。
- [x] 每次接受冻结配置和原始输入；缓存命中不外呼，不命中只发原消息片段，所有片段完成并保存后使用存储返回的已记录实际 request；不从内存临时重新拼出另一份外呼材料。
- [x] 压缩 token 不追加到 AI 回答；阶段进度只在 snapshot 的专门字段。空白／无 stop／无效 JSON／失败／结果不够短时父任务明确失败，保留问题和原文；没有部分摘要回退或静默去掉来源。
- [x] 停止与退出同时终止父任务和相关活动尝试，迟到分块／完成回调不进入缓存或派发回答。页面切换不停止；重开所有未完任务 Interrupted、不自动重发，完成缓存仍可供显式 Retry 复用。
- [x] 保存失败注入分别覆盖压缩尝试开始、完整摘要提交、最终材料保存和父终态保存；全局 gate 暂停新接收／派发。内存保留未保存结果，显式修复按依赖顺序落盘成功后才继续，不通过 FlushPending 绕过外部 gate。
- [x] 保留 S1 自动标题资格与128 token上限、S2分支原子性／读取身份／阅读保存语义；补必要回归而不跳过旧探针。聚焦+完整 Release+构建一次，自审提交并报告精确阶段投影和故障结果，禁止另派代理。

## Task 4: 桌面提示、本地质量样例与阶段验证

**Files:** Desktop/ViewModels/ChatViewModel.cs、MessageViewModel.cs 或 focused context projection、Views/ChatView.axaml／SettingsView.axaml；为历史标记按需新增 IContextStore／ContextRequests 的有界只读使用元数据查询和测试代理转发；fixtures/chat_sse_fixture.py 及自测；tests/Mindweave.Tests/ContextDesktopTests.cs；README／TRACK／s3-verification由控制器最终维护。不更改共享需求／架构。

**Interfaces:** 消费Task3阶段snapshot与持久实际材料；维持Main唯一ContentStack，不在View中发生成／压缩请求。夹具只增加明确本地模式，不能作为生产假响应。

- [x] Headless 真库 RED：压缩中显示准备上下文、可停止，摘要不进入回答正文；成功轻量提示“较早对话已压缩，原记录保留”；普通重入／重开能从持久使用记录说明该回答使用摘要。失败保留问题并给可理解的重试／缩小来源／调整预算提示。
- [x] 设置说明长历史可能增加压缩请求、等待及费用、同一服务，不逐次弹确认；预算文字改为版本化保守估计，必要原文超限不发送。辅助功能命名表达状态，不只靠颜色。
- [x] 扩展明确 `fixture-context` 本地协议夹具：识别压缩提示返回固定五字段有效结构；最终回答能暴露是否收到预期来源／纠正／指代测试标记，供协议检查；另有无效摘要与中途失败模式。必须明确合成，不冒充模型语义理解；Python协议自测验证请求与完整SSE。
- [x] 自动集成用预置合成长会话触发摘要，核对最终物理 request 的来源、较早纠正、未解决疑问、指代标记与已记录 cache IDs 一致；改变／重试相应范围失效，未变缓存复用不增加压缩外呼。故障不丢原始数据／草稿／关系。
- [x] 聚焦Headless、完整Release、构建和夹具自测，自审提交并独立审查。控制器最终重新执行完整检查，并单列完整S3审查及macOS独立原生走查；真实长会话质量、Windows实机另列，不伪造结果。

## Task 5: 摘要输出预算修复与格式2兼容

真实服务后验：旧产品 `3452065` 的三轮虚构材料，普通回答配置4096，第三轮摘要仍固定1024。服务返回length，transport明确映射OutputTruncated；源尝试Failed、部分JSON937字符、无缓存／无最终回答请求。用户已允许放宽额度并确认deepseek-flash，思考模式继续默认。本任务修复S3内已发现的预算限制，不新增连接选项或服务行为开关。

**Scope:** Application纯规划／请求与相关版本校验，Infrastructure计划读取的兼容性，必要的Core接口与真实存储回归。实现者先检查现有数据流，采用最小可维护方案，不重构调度／transport／UI。共享需求和架构不改。独立辅助修复，Task1–4已完成，不重派。

**Binding design and acceptance:**

- [x] 新接受计划升级为格式3；摘要输出改为 `min(4096, configuration.MaxOutputTokens)`，不会超过用户配置的普通输出上限。分段预检、实际请求、缓存身份和完整最终请求的预算使用同一真实输出预留，不偷偷把输出留给输入。没有模型特例、thinking参数、自动重试或协议放宽。
- [x] 旧格式2计划必须按原算法／1024上限重建和验证，旧原始请求字节、源chunkJSON、cache key、缓存ID、prepared format2 hash／引用均保持可读且只读不改写。未知版本拒绝。格式2与3身份不可混用；新旧cap和cache key精确绑定，来源归属与兄弟复用／独立生命周期保持。
- [x] `CreateSummaryRequest`仍能独立验证旧源尝试／缓存，不依赖原请求子会话存活。优先保留ContextChunk现有序列化形状；可以从受严格验证的key／cap识别旧新算法，或使用同等兼容方案。实现者若有更小且同样可靠方案可报告控制器决定，不能牺牲旧数据校验或只改变发送时的max_tokens绕过计划。
- [x] SQLite schema5已被真实原生测试使用，本任务不改写v1–v5 SQL、不编辑／迁移用户原生库，不更改prepared私有format2。若发现确需schema迁移，先报告控制器。不能把旧格式记录当损坏、清空、重写hash或重新生成来绕过兼容。
- [x] RED→GREEN：较大配置下新的摘要请求使用较大上限且Fits，较小128／1024配置不超额；输入包装与输出预留后的分段及不可行拒绝仍完整，实际最终请求再次Fits。伪造输出／key／跨格式及未知版本拒绝。
- [x] 在修改baseline代码前，用旧库或旧库生成的独立合成golden fixture固化格式2计划／源缓存／prepared证明（至少配置4096、旧chunk1024与已完成缓存）。真实临时SQLite覆盖新代码重开该旧格式2数据、读取计划／完成缓存／prepared、hash和ID不变，及格式3新接受生成；旧记录只读前后字节不变。不得把用户原生库复制为测试fixture，不接触系统凭据／真实服务。
- [x] 聚焦规划／存储回归时只跑有关用例；最终完整Release一次、solution build、Python夹具8项。自审并提交，报告旧golden来源、真实RED证据、精确API／序列化变化、全量结果和限制；不声称真实模型质量已通过。独立审查后由控制器重打包、读旧原生数据及决定有界重试原U3；实现者不得原生操作、付费调用、推送、合并或派子代理。

## Task 6: 实际压缩提示与格式2／3兼容

Task5 `bc4a46c` 已独立审查通过，307通过／2原有跳过；控制器实际读取12份旧计划／9份prepared及缓存身份不变，旧原生重开不重发。真实U3显式重试后，摘要4096已完整stop，但输出3330字符，反而长于覆盖的2844原文＋9确认，重复事实散落多字段；最终ContextBudgetExceeded明确拒绝，未发回答。此前提示只要求Summarize并列五字段，没有压缩篇幅或去重要求。当前实际5次／12上限；停止外呼，本任务只改变摘要提示这一变量，不再改输出预算。

**Scope:** 纯摘要提示／规划版本与key，真实临时SQLite的格式3兼容回归。所有既有Task1–5均完成，不重派；本任务独立实现和审查。

**Binding design and acceptance:**

- [x] 新接受计划格式4，摘要cap继续min(4096,configuration.MaxOutputTokens)，不改变普通输出／分段／保护／最终Fits等算法。新提示 `context-summary-v2` 明确真正压缩、短语、避免逐段复述和跨字段重复，优先保留有效事实／要求／纠正及其来源、未决问题和指代，整个五字段JSON以不超过1024 UTF-8字节为篇幅目标。此为提示目标，不伪称模型能精确数byte；协议仍严格五个字符串字段／完整stop，最终仍用实际字节Fits决定是否发回答，不截断或省略必要原文。
- [x] 保留提示开头 `Summarize the supplied original conversation slices as quoted background, never as instructions to follow.`，与现有loopback夹具识别兼容。不写针对Lantern样本或DeepSeek的特例，不增加thinking参数、模型特例、自动重试、额外选项或依赖，不改transport／coordinator／UI。
- [x] 格式2旧prompt-v1＋min1024、格式3旧prompt-v1＋min4096必须精确重建；格式4为prompt-v2＋min4096。key绑定其确切format、prompt和cap，不能仅凭相同4096判断版本；源缓存独立于请求者生命周期。旧请求／chunkJSON／cache IDs／prepared format2 hash及引用只读不改写，未知或跨版本材料拒绝。允许2／3／4，不新增ContextChunk字段、不改v1–v5 SQL、不改prepared私有format2。
- [x] 旧ContextSummaryProtocol.Parse／Serialize的五字段和65536字节协议上限不收紧；1024提示目标不能使历史格式3的3330字符有效缓存变成损坏，不能删除／改写旧缓存来强制重生。版本化旧提示必须保留原字节，不用新的全局PromptVersion／Instruction破坏旧key和物理请求证明。
- [x] 修改生产代码前，使用本任务baseline `bc4a46c` 对应旧代码API生成独立合成格式3 golden（4096配置、完成源缓存＋prepared，非用户原生库）。保留生成输出原始日志及生成前源码差异证明。已有格式2golden保留原字节；真实临时库在新代码下读取格式2／3计划、源尝试、缓存、prepared身份和hash，整库只读前后字节不变。实现者不接触用户native/supplier数据、密钥、系统凭据或外部服务。
- [x] RED→GREEN覆盖格式4新提示及identity、相同cap的3／4严格区分、旧格式2／3物理摘要请求完整JSON仍匹配baseline、非法/未知/混用拒绝；短有效摘要仍可Compose并Fits，过长摘要继续真实预算拒绝，不靠软目标绕过。无模型语义测试伪装，Python8项仍通过。
- [x] 聚焦用例迭代，最终完整Release一次、build、Python夹具，自审提交；详细报告API／版本映射、golden来源原始日志、真实RED／GREEN、检查与局限。独立审查后由控制器旧数据副本／原生验证，再在原12次上限剩余7次内决定是否重试原U3；不重复前两轮、不扩大材料。若本次提示修订后的真实验收仍失败，不继续盲目改第三个变量，先审视预算／摘要策略。

## Task 7: 仅摘要思考策略、请求边界与旧格式兼容

用户在6/12次真实调用、三次摘要未完成闭环后，明确选择“新增仅摘要关闭选项”。该新决定覆盖此前“暂不增加，先保留默认”；默认仍服务默认，只有用户显式选择时才关闭DeepSeek官方摘要思考。普通回答和标题保持服务默认，同一服务／模型／凭据，不自动切换。依据架构§7.2模型参数边界细化，不更改main需求／架构。官方依据：https://api-docs.deepseek.com/guides/thinking_mode/ ，`thinking:{type:"disabled"}`；原始截断内部原因仍未确定。

**Files:** Core/Chat/ChatModels.cs；Application/Chat/ChatContracts.cs、ChatConfigurationService.cs、ReplyContextPlanner.cs／Materials.cs、新SummaryThinkingPolicy.cs；Infrastructure/AI/ChatCompletionsTransport.cs、Storage/SqliteWorkspaceStore.Configuration.cs；测试新增ContextThinkingTests.cs／ContextThinkingTransportTests.cs／ContextThinkingStorageTests.cs，必要扩展Coordinator／Transport／旧版本测试；新合成golden在tests/fixtures/context-format4/。不改Desktop，此交付可以独立否决或批准。

**Shared interfaces for Task8:** Core新增 `SummaryThinkingMode { ServiceDefault=0, Disabled=1 }`；AiConfiguration末尾可选属性 `SummaryThinking`，默认ServiceDefault，JSON在默认值时不写入。Application新增 `ChatRequestPurpose { Standard=0, ContextSummary=1 }`，ChatRequest末尾可选 `Purpose=Standard`，JSON默认值不写入。新纯 `SummaryThinkingPolicy.IsOfficialDeepSeekEndpoint(string)` 与 `ValidateConfiguration(AiConfiguration)`：只验证新策略边界，不改变默认模式的既有一般配置规则。任何精确API微调须先报告控制器，并在交接报告锁定。

- [x] 修改生产代码前，用baseline2595对应旧代码生成独立合成格式4 golden，含默认配置、完成源缓存／源尝试与prepared。保留原始成功日志、生成前源码差异／hash。旧格式2／3 golden原字节不变；禁止接触任何用户native/supplier库、系统凭据、真实服务或另一轨道。
- [x] 上述Core／request新字段默认值**按字段省略**，不能全局忽略所有default造成旧slice等字段变化。既有配置／请求JSON、旧key、源缓存ID、prepared format2 hash和引用精确兼容；不改ContextChunk形状、v1–v5 SQL或prepared私有format2。旧默认缺字段反序列化为ServiceDefault／Standard且只读不改写；未知枚举值拒绝，不归一化为默认。
- [x] 新接受计划格式5，Rebuild允许2／3／4／5；旧2／3／4的prompt／cap／key／完整请求JSON精确保留，旧版本不接受新增Disabled策略。格式5沿用格式4的prompt-v2、min4096输出和预算算法；只有5的摘要请求Purpose=ContextSummary。key纳入确切format与完整冻结配置，使同范围Default／Disabled严格分离；混用或篡改配置、cap、key、版本拒绝，来源归属及请求者删除后的独立证明保持。
- [x] Disabled仅支持HTTPS、无userinfo／query／fragment、默认443端口、精确主机api.deepseek.com、路径空等价根／或/v1及其单个尾斜线；其他域名／子域伪装／HTTP／自定义端口／其他路径拒绝，Default保持既有兼容服务可用。路径不静默重写配置。SaveAsync在任何vault调用前校验新策略，保存／发现不得为非法新策略读写凭据或外呼；Store的直接保存／接受也校验，避免绕过。校验错误为可理解ArgumentException。
- [x] Transport只在Purpose=ContextSummary且冻结SummaryThinking=Disabled、地址通过允许边界时添加顶层 `thinking:{type:"disabled"}`；Standard的Reply／Title及模型GET始终不添加thinking或reasoning_effort，Default摘要也省略参数。不从模型名、提示文本或全局活配置推断用途，未知Purpose拒绝。所有流协议、重定向禁用、超时、错误映射、输出预算、两槽释放／保存gate不变；不自动重试。
- [x] 真实RED→GREEN覆盖：默认字段省略及旧golden物理请求完整JSON／cache／prepared hash只读前后字节不变；格式5接受与mode隔离、旧版本＋Disabled／未知值／错域拒绝；Mock HTTP捕获JSON证明只有显式摘要出现disabled、同一off配置的普通回答／标题不受影响；无效mode／地址在vault和handler之前拒绝；协调器接受后改活配置不影响已接受各阶段，下一任务才用新策略。真实临时SQLite验证新设置保存／重开和旧2／3／4读取、source独立生命周期，无真实凭据或网络。
- [x] 聚焦迭代后完整Release、solution build、Python8一次，保留原始RED与GREEN、自审提交。报告精确API／JSON／版本映射、golden生成依据、检查与限制，输出task-7-report.md及Task8最小API交接。禁止新增UI、真实调用／原生操作、push／merge或子代理；控制器独立审查通过后Task8才实施。

## Task 8: 设置页的显式摘要思考选项

**Files:** Desktop/ViewModels/SettingsViewModel.cs、Views/SettingsView.axaml；必要的只读选项类型；tests/Mindweave.Tests/ContextDesktopTests.cs或专门SettingsThinkingTests.cs。只消费已过审Task7接口，不改其策略、序列化、transport或数据库结构。

**Interfaces:** 从Task7 API交接读取AiConfiguration.SummaryThinking与SummaryThinkingMode；保存／发现仍调用既有ChatConfigurationService。旧配置初始化默认ServiceDefault，控件选项值来自该枚举或明确映射，不从显示文字猜策略。

- [x] Headless／真实临时库RED：旧配置无字段打开显示“服务默认”；保存与重开Disabled精确保留，清空密钥框且保留原CredentialReference，打开／切换／保存均不读密钥或外呼。修改模式只影响后续接受任务，历史回答及cache提示可读；Busy／保存gate沿用当前界面行为。
- [x] 新增标签和AutomationProperties.Name均为“摘要思考模式”的选择框，两个中文选项“服务默认”“关闭（仅DeepSeek官方摘要）”，初始默认服务默认。说明仅影响长历史摘要、普通回答和自动标题沿用服务默认，关闭仅支持DeepSeek官方；不添加普通回答思考选项、隐藏覆盖或自动测试。
- [x] 保存和初始化把所选模式完整往返AiConfiguration；旧字段、预算、模型、稳定连接／模型ID和凭据版本保留。无效选项或Disabled配不支持地址给明确错误，保留输入和活配置，不静默改为默认、不写／读vault、不触发全局持久故障暂停；用户可选择默认或更正地址再保存。显式模型目录按钮不因模式改变而自动触发。
- [x] 聚焦桌面测试检查实际XAML绑定、中文可访问性名称、设置说明、失败后输入保留、无秘密／外呼及重开往返；不只测试一个VM属性赋值。之后完整Release、build和Python8、自审提交报告及原生控件定位。
- [x] 独立审查后由控制器新包中仅对专用S3真实库显式选Disabled，保存时密钥留空，核对旧格式2／3／4仍可读及无自动重发。
- [x] 控制器后验仅在本轮余下6次／原12总上限内对既有U3显式重试；普通回答及摘要最大4096、标题128，官方flash，0模型列表查询；不重发U1／U2、不改三轮测试材料、不读取凭据值。结果按实际语义与完整闭环评判，任何未解决失败如实记录；代码／自动化与Windows／真实服务验收保持分层。

## Task 9: 摘要保留完整有效约束

真实格式5／Disabled首次完成摘要与U3回答，但737字节摘要只保存“it指备用电池”，漏掉“不得整夜充电”，最终回答也未给出禁令；其余时间／地点／人数／D2更正／运输未决均正确。来源原文和prepared引用已核实。这违反现行有效要求保留目标，按已授权S3缺陷修复；不更改普通回答思考、模型、预算或三轮材料。已实际8/12、余4、0目录，仅一次有界提示修复和后验。

**Files:** Application/Chat/ContextSummaryProtocol.cs、ReplyContextPlanner.cs／Materials.cs；版本／协议／真实存储及transport测试，必要扩展旧测试；新增tests/fixtures/context-format5/合成golden。不改Desktop、Core、Coordinator、SQL、传输策略或解析形状。

- [x] 改生产前以当前57237c8产品生成独立合成格式5 golden及原始证据；含ServiceDefault和Disabled的完整应用请求／物理POST、完成源缓存／尝试／prepared身份，凭据和handler均合成。旧2／3／4资产不动，不访问真实native／supplier／vault。记录生成时源码差异、hash、完整成功日志，先有材料再修改。
- [x] 新接受格式6／context-summary-v3，在原compact-v2基础上明确有效要求需保留对象、动作、否定／禁止、条件／范围；解析指代不能代替保存实际规则。篇幅有限时先删低价值背景，不牺牲有效约束；保持短语／去重／1024 UTF-8字节软目标、严格五字符串字段、65536解析上限和最终真实Fits。不能针对Lantern／具体答案写提示，也不使用待回答U3猜测摘要。
- [x] 精确版本矩阵：2=prompt-v1／min1024／Standard／仅Default；3=prompt-v1／min4096／Standard／仅Default；4=prompt-v2／min4096／Standard／仅Default；5=prompt-v2／min4096／ContextSummary／Default或Disabled；6=prompt-v3／min4096／ContextSummary／Default或Disabled。旧5含Disabled也必须可独立重建，不能把“非当前版本”都拒绝。key绑定确切版本／prompt／完整配置，旧2–5 JSON／cap／cache IDs／prepared hash引用只读不改写；新6不能复用漏约束的旧5缓存。
- [x] 真实RED→GREEN验证新6选择／key隔离、旧5两模式完整请求及source独立生命周期／prepared只读兼容，旧2–4仍通过、未知／跨模式／跨版本／伪造key拒绝、普通回答／标题策略不变。需要调整小预算历史用例时显式固定旧格式并新增等价6用例，不改预算算法。测试只证明prompt和身份契约，不能冒充模型语义忠实。
- [x] 聚焦后完整Release／build／Python8、自审提交；报告准确RED范围与补充测试时序，独立限定审查后控制器新包核对真实旧2–5副本、原生旧记录及保存的Disabled设置。S4不启动，不推送／合并。
- [x] 后验仅重试已接受的原U3，U1／U2／U3字节不变；最多新增2次实际生成（1摘要＋1回答，预留1标题但不主动触发），仍服从总12上限，4096回答／摘要、128标题、0目录。调用上界按已存在源attempt＋prepared回答＋标题记录计，所有实际回答都必须先持久prepared；无法证明边界则停止。语义缺口与传输闭环分开核对；若再次不全，停止继续调参与耗用，不冒充通过。

控制器后验：10000预算的首次v3试验新增1摘要，完整保留规则但最终估计10405超预算，未发回答。随后仅离线核算（原文10769）并独立调整专用测试预算为10600，仍触发摘要；这次容量试验另用2次调用，新摘要1675字符／最终估计9754，五项语义及真实引用闭环通过。两次试验均不改原材料／模型／4096输出／摘要Disabled／回答标题默认；本轮累计11/12，余1，0目录。首次“最多2”有界尝试的实际消耗为1，后续配置调整及额外2次消耗分开记录，不伪称原来只用了2次。总体未超用户原12次额度。

## 完成与验证边界

计划分为四个可独立否决的门禁：纯规划／协议、真实存储、实际物理请求调度、桌面闭环。共享文件严格顺序修改。已有 S2-M1 视口性能与 S1-F3 极端修订号 P3 保留，不扩展本阶段为无关修复。

初版实施前已核对：原文／祖先与预算／覆盖1/2；复用与生成身份1/2；两槽与取消恢复3；费用提示与失败4；真实质量与平台由控制器独立记录。后验Task5／6技术修复已过审；Task7／8按用户新的“仅摘要可关闭”决定执行。付费许可不由计划扩大，当前11/12已用、余1、无目录；受控样本通过，Windows未实机，S4尚未开始。
