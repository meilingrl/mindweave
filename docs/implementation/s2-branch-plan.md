# S2 分支与返回实施计划

> **For agentic workers:** 使用 `subagent-driven-development`，逐任务实现、测试和独立审查。现行需求／架构及用户连续推进授权已覆盖本阶段；产品范围变化、真实付费额度、系统权限及 Windows 实机协助另行提出。此计划不改变 main 的共享需求／架构。

**Goal:** 从已保存终态回答的真实 Markdown 选区创建至少两层分支，冻结祖先范围，正确返回来源、再次进入和恢复阅读／草稿。

**Architecture:** Core 保存不依赖 UI 的选区信封、分支与阅读身份；Application 统一生成上下文和业务导航；Infrastructure 在既有 SQLite 单写入队列中事务保存分支／来源／冻结范围；Desktop 用 LiveMarkdown 公共 API 捕获、校验、高亮和定位，Main 保持唯一活动 ContentStack。

**Tech Stack:** .NET 10；正式桌面与 Headless 固定 Avalonia 12.1.4，LiveMarkdown.Avalonia 2.5.1（共享小样已验证版本），SQLite 与 MVVM 版本延续 S1。

**Spec:** [需求](../requirements.md) §6.2–6.3、§9.5–9.6、§11；[架构](../architecture.md) §3–6、§12；[详细交接](../reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md) S2；[LiveMarkdown 小样](../../prototypes/livemarkdown-chat/README.md)。上述现行设计已确认，工程细化按此计划执行，不再重复产品审批。

## Global Constraints

- 产品代码只在 `track/codex-steady`；不改共享 requirements.md／architecture.md，不借用另一轨道代码，不推送或发布。
- Core／Application 不引用 Avalonia／SQLite；正式纯原生 Avalonia，不加 WebView、反射或供应商控件补丁。
- 必须真实跨段／格式拖选；重复词按来源块和块内 UTF-16 位置定位，不靠全文搜索猜位置。精确原文偏移仅在整个源块和显示块完全相同且重验成功时成立。
- 来源信封必须带格式版本与渲染配置身份；正文 hash、来源块、布局 hash、区间／引用全部验证后才改变选区／高亮。失败定位到稳定消息并显示保存引用及“无法精确定位原句”。
- 仅同工作区、指定父会话的 AI 终态回答且正文／终态已持久保存才能创建分支。允许 Completed／Stopped／Failed／Interrupted 的非空已保存正文；Running／Queued／脏终态拒绝。保存失败暂停分支提交和新 AI 请求。
- 分支会话＋直接来源＋展开后的祖先固定消息身份＋父阅读位置一个事务；相同操作身份重复提交只返回同一子会话，不重复创建。引用只指向祖先链，不允许跨工作区／兄弟混入。
- A→B→C 后 A/B 追加或重试不能改变 C 冻结身份；引用旧尝试仍有效。S2 不压缩或静默截断，必要背景超预算时保留问题、明确拒绝。
- 来源锚点与阅读位置分别保存；返回来源定位父入口，再次进入与普通切换恢复目标自身最近阅读位置；草稿保持 S1 语义。普通后退依据访问历史，返回来源依据关系。
- 本计划不增加付费调用；S1 已用7/8次生成、1/1次目录，剩余一次生成仅在确有必要且不扩展原授权时可用。实现和原生主验收使用独立测试库与明确标识的 loopback fixture，不读取用户密钥。
- macOS 原生和 Windows 11 原生分别记录，不能把 Headless 或小样当产品验收；Windows 等实际可用设备再执行。

## Shared Contracts

以下类型放 `Mindweave.Core.Branches`（读位置也可同目录按责任拆文件），不可包含控件实例：

- `SourceAnchor(int FormatVersion, string RendererProfile, string Payload)`：业务层不解析渲染器 payload。
- `SourceSelection(Guid MessageId, long MessageRevision, string SourceBodyHash, string Quote, SourceAnchor Anchor)`：捕获的正文 hash 为 UTF-8 SHA-256 大写十六进制，终态 revision 仍须存储重验。
- `ReadingLocation(Guid MessageId, int SourceStart, double OffsetWithinBlock, int FormatVersion, string RendererProfile)`：SourceStart 为源块起点，无法块定位可回到消息；offset 有限且非负。
- `ConversationReadState(Guid ConversationId, ReadingLocation? Location, long Revision)`：独立单调修订，空 Location 表示开头。
- `BranchSource(Guid ChildConversationId, Guid ParentConversationId, Guid MessageId, string Quote, SourceAnchor Anchor)`。
- `FrozenContextRange(Guid ConversationId, IReadOnlyList<Guid> MessageIds)`：有序消息身份，不从动态最大序号重新推导。
- `ChatContext(IReadOnlyList<ChatMessage> Messages, string? SourceQuote)` 放 Chat；`GenerationWork` 增加兼容默认值 `string? SourceQuote = null`。

Desktop `MarkdownSelectionAdapter` 公共契约：`RendererProfile` 常量 `livemarkdown/2.5.1;mindweave/1`；`Capture(Guid messageId, long revision, string markdown, MarkdownRenderer renderer)` 返回 `SourceSelection`；`Restore(SourceSelection selection, string markdown, MarkdownRenderer renderer)` 返回已验证块片段；`Reveal` 将首片段带入视口。读位置捕获／恢复以 ReadingLocation 表达，不将来源选区当阅读位置。

Task 2 产出 `IBranchStore`：`CreateBranchAsync(Guid parentConversationId, SourceSelection selection, Guid operationId, ConversationReadState? parentReadState = null)` 返回 Conversation；`LoadBranchSourceAsync(Guid childConversationId)`；`ListChildBranchesAsync(Guid parentConversationId)`；`ReadFrozenContextRangesAsync(Guid conversationId)`；`LoadReadStateAsync(Guid conversationId)`；`SaveReadStateAsync(ConversationReadState state)`。返回集合为 IReadOnlyList，单对象可空。

IChatStore 增加 `Task<ChatContext> ReadContextAsync(Guid conversationId)`，真实实现返回冻结祖先身份对应消息＋本会话最新尝试有效历史，单一算法供预算与接受共同使用。选文作为单独明确标识的上下文提示进入请求且纳入预算，消息输入 ID 清单仍记录实际使用的全部祖先／自身身份。

## Task 1: 正式 Markdown 与来源适配层

**Files:** 修改 Desktop／Tests csproj、必要 Avalonia 12 API 兼容处；新增 Core/Branches 的 SourceAnchor、SourceSelection、ReadingLocation／ConversationReadState 类型，Desktop/Markdown/MarkdownSelectionAdapter.cs、MarkdownMessageView.cs（必要样式）；修改 ChatView 正文控件与 MessageViewModel 的 revision／role 投影；新增 MarkdownSelectionTests.cs。不实现分支存储和业务导航，不改独立原型。

**Interfaces:** 产出上述选区／阅读类型和 MarkdownSelectionAdapter；为 Task3 保留实际 renderer、MessageId、revision 和终态可读性。可用公共 MarkdownMessageView 暴露自身 renderer 与捕获方法，接口写入报告。保留用户消息纯文字或同一安全 Markdown 表现均可，创建分支只能 AI。

- [x] 用真实 Headless＋Skia 布局写 RED 测试：同段第二个重复词、粗体／链接／行内代码跨两段、标题／列表／引用／代码；错误正文／hash／位置／渲染配置必须整体拒绝，不出现半数高亮。
- [x] 固定 Avalonia 12.1.4／LiveMarkdown 2.5.1，迁移必要编译 API；参考共享小样公共 API，独立产品适配，不能直接依赖 prototype 工程或默认用户小样存档。
- [x] 实现信封严格解析与有界区间，格式／profile 不匹配明确拒绝；quote 为按块顺序合成的显示引文。恢复前全量校验，禁止搜索相同文字。复杂表格／公式／嵌入内容选区不完整则拒绝。
- [x] 正文切换成正式 Markdown，流快照更新仍可读；追加／窄窗保留旧块的选区与阅读位置，无法匹配旧块时给出明确结果，不能悄悄跳到同词。将核心阅读捕获／恢复能力交给 Task3 使用。
- [x] 跑聚焦测试、完整 Release 构建／测试，保留 S1 行为；自审并提交。报告明确红绿、实际依赖和公共接口，不调用供应商／真实钥匙串／原生其他资产。

## Task 2: 分支事务、冻结上下文与阅读存储

**Files:** Core/Branches 模型、Application/Branches/IBranchStore.cs、Application ChatContext／契约／预算／Coordinator；Infrastructure/Storage/SqliteWorkspaceStore.Branches.cs、ReadState.cs、Generation.cs 和新 v4 migration（v1-v3 SQL 不改）；新增 BranchStorageTests.cs／必要 GenerationCoordinatorTests。仅在接口编译需要时适配测试 store 包装，不实现桌面流程。

**Interfaces:** 产出 Shared Contracts 中 IBranchStore、ReadContextAsync 和 GenerationWork.SourceQuote，SqliteWorkspaceStore 实现。Task3 必须可读父来源、按来源消息分组的子分支及阅读状态。

- [x] 真实临时 SQLite RED：原子分支与中断／失败回滚、相同操作ID幂等、跨工作区／用户消息／活动回答／修订或正文hash不符拒绝；空quote／未知anchor版本／无效信封拒绝，旧已保存尝试可引用。
- [x] v4 表保存 branch source（child唯一、operation ID唯一、parent／message复合归属、anchor版本与profile）、有序冻结范围身份和独立会话阅读状态；事务检查父会话与消息角色／终态／hash／revision。应用检查不能假称普通FK保证全部祖先关系。
- [x] 创建时继承父已冻结祖先范围，再加入父截至来源回答的有效消息身份（每个提问当时最新尝试，且选中旧回答按该截止点保留）；展开后只读不可随父后续改变。不复制整段祖先正文。
- [x] A→B→C 真库验证：A/B 后续提问、回答重试、兄弟根都不进入 C。输入清单可跨会话但只允许冻结祖先，消息顺序为祖先→自身；自会话最新尝试语义保持S1。SourceQuote 进入请求和保守预算，超限接受前拒绝，输入与草稿不丢失。
- [x] ReadContextAsync 与 AcceptReplyAsync 使用同一组装规则，避免重复 LatestInput 抹掉冻结旧尝试；接受事务重新验证实际输入身份，终态／快照保持S1；自动标题使用自身已成功问答，不能把祖先成功误当当前会话已回答。
- [x] 阅读状态保存验证消息归属、版本／profile／有限offset／正修订号；旧修订不覆盖；分支事务可同时保存父位置。v3升级先备份，真实分支与阅读重开，原迁移未改写。
- [x] 聚焦与完整 Release 测试，自审提交并报告；不访问原生测试库或用户凭据。S1-F3 极端long.MaxValue已记录未修复，避免无关修复扩大本任务。

## Task 3: 分支、返回与阅读的桌面闭环

**Files:** 源锚点共享边界常量及适配／存储的边界一致性小修（Core SourceSelection、MarkdownSelectionAdapter、SqliteWorkspaceStore.Branches 与对应测试）；MainViewModel 的分支／导航／阅读责任可拆partial；ChatViewModel、MessageViewModel、ConversationItemViewModel、ChatView／MarkdownMessageView、MainView、导航请求模型、CompositionRoot；新增 BranchDesktopTests.cs；fixtures 的明确 fixture-markdown 模式及协议自测；README、TRACK 和 s2-verification.md 由控制器最后汇总。不改共享需求／架构。

**Interfaces:** 消费前两任务真实 API；Main 是业务导航唯一协调点。来源导航请求需稳定 child／parent／message身份及 SourceSelection，普通历史保存快照；不得由 View 内部私建业务栈。ChatView 的控件事件只负责捕获布局、投递选择和恢复阅读，不持有数据库／服务配置。

- [ ] 统一来源 payload 为 1 MiB UTF-8 共享上限，Capture／Restore／Store 一致拒绝超限而不截断；真实有效中文引文的 JSON 可通过空白填充测试恰好边界与超一字节，不构造巨大布局。
- [ ] Headless真SQLite＋受控transport RED覆盖：选区创建B，再B→C；双击命令不重复分支；未保存或活动回答拒绝；故障暂停全局接收；分支成功后草稿保存／导航失败不再建第二分支。
- [ ] 原生拖选后提供“创建分支会话”入口；冻结捕获身份再提交，提交期防重入。显示当前路径与“返回来源”；父回答下列来源书签可再次进入已有子会话，侧栏按真实层级显示，不虚构总结节点。
- [ ] 返回来源按关系打开父并高亮定位；精确锚点校验失败仍定位来源消息、展示保存引用及明确降级提示。再次进入子分支和普通切换恢复目标自身最新阅读位置，不能用来源替代。
- [ ] 阅读位置随滚动节制保存，切页／切主题／正常退出补存；读取／布局等异步完成须检查当前会话身份，迟到回调不能滚动新页面。保留草稿和单ContentStack，不在等待导航期间接收输入竞态。
- [ ] 完整回归：父位置与来源不同、普通后退与返回来源结果表、重开跨工作区有效恢复、来源损坏降级不搜同词、长正文流式更新与窄窗不跳读。失败修复顺序包含草稿／阅读／协调器，只有全部成功才解全局暂停。
- [ ] 扩展测试夹具 fixture-markdown（返回显式标注的合成Markdown固定样例，至少含重复词／两段／粗体链接代码和足够滚动），不加生产假响应。只允许本地专用数据，真实密钥不参与自动化。
- [ ] 聚焦／完整 Release 测试与构建，自审并提交，报告真实接口和红绿；独立审查后由控制器重建独立S2原生包，用本地fixture做鼠标拖选、A→B→C、返回、再次进入、重开和窄窗。Windows单列待用户实机，供应商额外调用须重新明确额度。

## 自审与完成边界

任务共享文件按顺序修改，禁止并发实现。Task1只交付渲染／锚点，Task2交付存储／上下文，Task3交付导航／阅读／原生闭环；三者均有独立可否决的验收面。标准Markdown首版结构均归Task1，业务四条导航语义归Task3，祖先隔离和实际输入归Task2。S3压缩、S4总结、S5网络、S6删除／归档不实现；Windows待实际执行，不阻止本地工程推进。
