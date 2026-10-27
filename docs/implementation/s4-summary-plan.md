# S4 知识总结与版本管理实施计划

> **For agentic workers:** 使用 `subagent-driven-development`，逐任务 TDD、自审、提交与独立审查，末尾整阶段审查。沿用用户持续开发授权与已确认设计；费用、不可恢复原生删除及外部权限按具体操作边界另行提出。只修改稳健轨。

**Goal:** 用户可把当前会话保存为可追溯、可编辑、可回顾的知识总结；更新采用候选，阅读状态独立，历史操作保护已有内容。

**Architecture:** SQLite追加schema6，保留旧generation_task与S3外键图；新总结／命名任务采用专用存储合同。应用拥有共享物理请求调度和生命周期，聊天、知识总结、后台命名分别投影自己的产物。桌面在现有Network主区提供总结列表／详情，探索区发起生成，S5再接图形网络。

**Tech Stack:** .NET10、现有四工程、Microsoft.Data.Sqlite、Avalonia12.1.4／LiveMarkdown2.5.1、xUnit v3；不加供应商SDK／WebView／新依赖。

**Spec:** [需求](../requirements.md) §6.4、§7.1–7.2、§8.1、§9、§11；[架构](../architecture.md) §4、§6、§8、§10、§12；[已确认交接](../reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md) §2.6、§4。工程细化不更改main需求／架构；发现冲突记入TRACK。

## 已确认范围与工程选择

这是既有设计中的S4子系统，产品取舍已由现行需求和U1–U4确定。实施前已只读核对存储、调度和输入三个边界，证据在本计划SDD工作区；不引用另一轨道代码。

知识总结输入v1发送全部冻结的本会话有效终态消息，加原有冻结分支背景／直接选文。整个系统指令和JSON来源封装计入预算，超限在接受前明确拒绝、0任务／版本／外呼；不取最后N条、不伪造用户消息、不以S3续聊摘要冒充知识总结。用户可按服务容量调整配置后显式重试。通用总结输入压缩不是本阶段的隐含实现；需求§9.6定义的续聊压缩保持原样。这一限制须在总结入口明确说明。

首次成功直接保存为未读节点，不自动打开；后续成功是唯一待采用候选。只有当前版本可编辑；候选／历史只读。手写保存生成已读新版本、覆盖范围不变，标题或正文无变化不增版。打开历史不切换当前。

阅读度量用实际进入视口的正文文本区间，阈值默认85%、短正文连续可见且窗口活动3秒，两者均可注入调整。仅打开不立刻记已读；手动开关可改回未读，当前阅读会话的迟到自动记录不能立即覆盖手动未读，新阅读会话才恢复自动判定。版本阅读互不影响，采用已读候选仍已读；不推断理解。

删除当前后按创建序号选择之前最近保留版本；没有之前版本时选最新保留版本，候选从不成为回退。最后版本删除则删节点、保留会话。受影响的候选、未保存草稿或活动总结任务须先显式处理；删除无关历史正文可保留必要基线身份，不应仅因同节点存在待处理状态就一概阻止。删除预览绑定版本／节点修订，变化后重新确认。后台命名不得永久阻塞删除，迟到结果不能复活节点。

## Global Constraints

- 产品代码只在 `track/codex-steady` 的既有worktree。共享需求／架构不在轨道悄悄修改；不借用另一轨道代码，不推送、合并或发行。
- Core／Application不引用Avalonia／SQLite；正式原生Avalonia，保留Root／Menu／Content三层导航和参数传递。
- 全局最多两个AI物理请求；用户提问及其必要压缩优先于总结与自动标题／命名。纯准备／依赖和请求结束后的落盘不预留物理槽；在途流检查点不释放仍活动的物理槽，槽保留至枚举器DisposeAsync真正结束。
- 页面／会话／工作区切换不取消任务。Stop保留现有即时冻结／取消／flush语义；独立跟踪尾任务，成功Shutdown／Dispose才完整排空并释放store／transport。
- 接受时冻结目的地、模型／凭据引用及范围；已有排队／生成回答须先结束或停止才接受总结；总结接受后会话可继续增长，范围不随之扩大。
- 总结／命名的部分输出只在内存；失败、截断、取消、中断或完成持久化失败都不产生伪完整版本。修复已完成响应不得重发AI。
- schema1–5 SQL与所有S3格式2–6请求、cache、source生命周期、prepared私有format2／hash不变。schema6与S3请求格式6是独立命名空间。新迁移先备份、事务提交、失败原库不变。
- 版本删除实际清除正文及阅读状态，必要基线／任务只保留少量身份；不保留隐蔽完整输出副本，不自动清理历史或创建回收站。NodeLayout待S5，勿预建图模型。
- 本阶段测试只用合成资料、新临时库、受控transport。S1／S2／S3原生／supplier库、密钥及包保留；迁移验证只用控制器副本，禁止拿真实库自动升级。S1、S3各余1次调用不挪为S4授权。
- 原生不可恢复删除须在具体确认框就绪时按工具规则请求当次确认；自动化合成临时库测试正常执行。真实服务另有具体材料／模型／额度说明，密钥不进聊天／日志。

## Shared Contracts 与文件职责

Task1定义Core/Summaries不可变节点／版本／覆盖／任务／草稿／阅读／命名快照，以及Application/Summaries的纯规则与接口。类型可作一次有记录的工程微调，但报告必须给出完整精确API；后续任务只消费过审交接，不自行猜签名。

- `KnowledgeSummaryCoverage`：来源会话、所有自身行的接受高水位、实际有效尝试的有序消息身份／修订／hash／角色／状态／reply与supersedes；冻结祖先身份和直接选文绑定。高水位含被取代旧行，实际来源按LatestInput，祖先保持原ID，二者不混淆。
- `KnowledgeSummaryInputPlan`：独立format1、`knowledge-summary-v1`、估算器版本、覆盖／实际材料引用、冻结配置及规范请求hash。不存完整messages_json。物理JSON用户封装不是历史ChatMessage身份。
- `KnowledgeSummaryInputPlanner`／`KnowledgeSummaryProtocol`：完整来源规划、精确Request／Fits、严格唯一两个字符串字段Title／Markdown、完整解析。payload≤256KiB UTF-8，Title trim后1–160 Unicode标量、单行无控制符，Markdown非空且保留格式。KnowledgeSummary用Standard；只有S3 ContextSummary可以使用Disabled线参数。
- `IKnowledgeSummaryStore`：幂等接受／读取任务及冻结请求／metadata状态／原子完成／恢复。接受使用调用者操作ID，丢失成功响应可按ID核对，不重发。首次节点只在完整成功事务出现，后续候选基线冻结。
- `ISummaryVersionStore`：一致节点／版本页眉／正文读取，编辑epoch草稿与提交，阅读会话／修订，采用／设当前／删除预览令牌及提交，手动节点名和总结页选择。预期冲突与Blocked不是持久化失败；SQL／保存故障进入共享暂停。
- `INodeNamingStore`：同工作区规范化基础标题组、完整成员／当前版本／手改标志／nameRevision快照，幂等接受、原子整组CAS、恢复。输出不保存为版本；无依据允许同名，失败保留原名，不无限自动重试。
- `SummaryReadPolicy`：归一化可读正文区间并集、阈值／短文停留、手动未读的阅读epoch；无Avalonia依赖。`SummaryVersionPolicy`：指针／候选／dirty草稿／活动任务校验与确定回退。标题规范化使用NFKC、空白折叠及不区分大小写的稳定规则，不做语义同义词归并。
- Task4锁定共享`AiRequestScheduler`／应用生命周期精确API，Task5／6消费其交接。不得在旧协调器独立两槽上再套共享semaphore；两个低优先级等待者会隐藏新Reply。
- 桌面使用现有ContentKind.Summary／Network路由。v5 app_selection只支持Conversation，schema6用伴随总结选择记录配合旧Overview行原子保存，不将总结ID塞进conversation FK。恢复无效版本回当前，无效节点回Network概览。

## Task 1: 纯领域、输入协议及合同

**Files:** 新建`src/Mindweave.Core/Summaries/`内按节点版本／覆盖／工作状态分责的记录；`src/Mindweave.Application/Summaries/{SummaryContracts,KnowledgeSummaryInputPlanner,KnowledgeSummaryProtocol,SummaryReadPolicy,SummaryVersionPolicy}.cs`；`tests/Mindweave.Tests/{SummaryPolicyTests,SummaryInputTests}.cs`。

**Interfaces:** 产出Shared Contracts全部精确类型／接口与纯规则，供2／3／5／6／7消费；不接存储／协调器／UI。

- [x] 先写可运行RED，记录首次类型编译与行为RED的区别。案例命名包括`SummaryProtocol_rejects_duplicate_or_partial_fields`、`SummaryInput_preserves_frozen_scope_and_requires_complete_budget`、`Manual_unread_rejects_older_progress_epoch`、`Current_deletion_chooses_previous_else_latest_retained`；含中文／emoji／转义与边界±1。
- [x] 实现全部冻结来源＋必要分支背景的独立input1／protocol1，SourceQuote引用原BranchSource绑定；空自身内容拒绝，Partial/Failed/Stopped终态状态不冒充完整解释，祖先旧尝试不按当前LatestInput重选。原指令作为引用数据，协议明示不执行源消息命令。
- [x] 实现严格Title／Markdown解析和跨delta UTF-8有界累积，完整流结束及Dispose前不判成功；保留现有ChatRequestBudget／S3代码不动。纯版本／阅读／规范化规则覆盖上文所有状态。
- [x] 聚焦GREEN命令`dotnet test Mindweave.slnx -c Release --filter 'FullyQualifiedName~SummaryPolicyTests|FullyQualifiedName~SummaryInputTests'`；完整Release／build后自审提交，报告逐项测试结果、完整精确API和后续消费示例。独立审查后锁定合同。

## Task 2: schema6、冻结任务及原子首次／候选完成

**Files:** `src/Mindweave.Infrastructure/Storage/DatabaseSchema.cs`仅追加6、独立`SummarySchema.cs`；新增`SqliteWorkspaceStore.Summary{Jobs,Inputs,Completion,Queries}.cs`等聚焦partial；`tests/Mindweave.Tests/{SummaryMigrationTests,SummaryGenerationStorageTests}.cs`及合成旧schema5夹具。

**Interfaces:** 消费Task1；实现`IKnowledgeSummaryStore`；schema6包含3／6所需节点、身份／完整内容、source、read、draft、summary job、naming job/member、summary selection字段，DDL实施前核对这些任务与过审API。

- [x] RED覆盖schema5→6全旧行不变／一致备份，v6晚期DDL失败与子进程中断回滚；SQL1–5字节相同，S3各种格式身份只读可重建。只用合成fixture或控制器副本。
- [x] 用既有单writer事务实现新专用表，保留generation_task旧CHECK／triggers／S3外键图。版本小身份与完整正文分离，当前／候选只能指同节点完整内容，首次节点与版本同事务。完整版本不可UPDATE；读／覆盖随正文删除，基线身份不会级联删别版。
- [x] 接受事务独立拒绝active Reply／active Summary／未处理候选，冻结有效输入和所有自身行高水位，按完整request Fits再接受；幂等操作ID／精确元数据，0隐藏副本。准备先持久实际引用＋hash，读回精确重建，篡改只读拒绝。
- [x] 原子Complete只接受完整有效结果和仍活动任务；首次current未读、后续唯一candidate未读，current手写变化仍留候选原基线；任务metadata／版本指针全一起提交。失败／取消／中断0产物，LostCommit按ID核对，迟到不复活旧节点。
- [x] 聚焦GREEN筛选`FullyQualifiedName~SummaryMigrationTests|FullyQualifiedName~SummaryGenerationStorageTests`，完整Release／build、自审提交及schema/API交接，独立审查。schema6一经产生保留数据即冻结，后续缺字段追加迁移，不暗改旧SQL。

## Task 3: 编辑、阅读、采用与历史事务

**Files:** 新增`SqliteWorkspaceStore.Summary{Versions,Drafts,Reading,Deletion,Selection}.cs`；按需扩展选择保存；`tests/Mindweave.Tests/{SummaryVersionStorageTests,SummaryReadStorageTests}.cs`。

**Interfaces:** 消费Task1／2；实现`ISummaryVersionStore`，给Task5／7／8读取、编辑、确认令牌与失败语义。

- [x] RED覆盖真实SQLite整套编辑／采用／删除矩阵、草稿epoch与迟到write、阅读CAS、选择恢复／删除修复、并发冲突和事务故障注入。
- [x] 当前版本编辑草稿独立自动保存，dirty可在导航后恢复；显式保存无变化不增版、变化生成已读版本且复制原覆盖，保留候选及基线。清除草稿留epoch tombstone防复活，迟到旧autosave不覆盖新会话。
- [x] 阅读按版本保存；仅打开不置已读，手动未读抑制当前阅读会话自动回写，旧epoch拒绝；候选采用／历史当前切换保留自己的读状态，后续终态内容标记与高水位独立。
- [x] Adopt基线变化返回包含明确当前／候选的Conflict，确认绑定新修订再CAS；dirty先保存／丢弃。设历史为当前不删其他版本，受影响候选／草稿／任务先处理；单纯查看历史不切指针。
- [x] 先PreviewDelete给精确选集、受影响依赖、回退、永久删除文案及token；仅删除无关历史版本时保留当前草稿／候选／活动任务。再同事务重检并删正文／读／source、修指针或删最后节点。候选单独丢弃，原聊天／分支不动；旧读／草稿／命名结果不得复活。保留仅必要被引用身份，不存全文输出副本。
- [x] 总结选择伴随记录与旧app_selection一起保存／清除，跨工作区拒绝，删除修复最后页。聚焦GREEN筛选`FullyQualifiedName~SummaryVersionStorageTests|FullyQualifiedName~SummaryReadStorageTests`，完整Release／build、自审提交和独立审查。

## Task 4: 全局阶段调度与旧聊天参与者

**Files:** 新建`src/Mindweave.Application/Generation/{AiRequestScheduler,AppGenerationRuntime,GenerationRuntimeContracts}.cs`聚焦边界；必要修改GenerationCoordinator各partial与`DesktopChatSession`／Root生命周期、MainViewModel／CompositionRoot全局gate与修复组合；`tests/Mindweave.Tests/{AiRequestSchedulerTests,GenerationRuntimeTests}.cs`及现有协调器回归。

**Interfaces:** 消费既有chat生命周期；产出共享调度／参与者／接收与暂停修复精确API，Task5／6只消费过审报告；旧构造默认独立运行兼容测试，生产只一个app-owned实例。

- [x] 先RED覆盖共享调度缺失及本地两槽等待者隐藏新Reply的对抗场景，区分前瞻性错误接法与旧应用已有行为；控制latch覆盖全部类型聚合peak≤2、Reply准备屏障、FIFO、队列取消0外呼、依赖不持槽、无phase重叠、Dispose后才释放、释放后落盘尾仍被跟踪。
- [x] 共享logical demand贯穿准备／持久化间隙；ready phase统一派发，去掉参与者独立物理准入上限。优先级Reply及必要压缩0、显式知识总结1、自动标题／命名2；运行请求不抢占，准备中的高优先级需求阻止低优先级越过。
- [x] App-owned接受／失败epoch／退出修复聚合，保留现有Stop即时语义；成功Shutdown／Dispose等待所有阶段和落盘尾后才释放依赖，失败保留实例可Retry／Return，重开不重发。健康参与者不能清除别人的故障或并发新失败；外部UI草稿故障纳入同一gate。
- [x] S1／S3终态、检查点、cache来源、冻结配置、显式retry、旧构造和枚举器对抗测试原语义不变。不得顺手修S1极端revision或S2视口扫描。
- [x] 聚焦GREEN筛选`FullyQualifiedName~AiRequestSchedulerTests|FullyQualifiedName~GenerationRuntimeTests|FullyQualifiedName~CoordinatorTests`，完整Release／build、自审提交API交接与独立审查。

## Task 5: 知识总结协调器及应用生命周期

**Files:** `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator*.cs`；`DesktopChatSession`／Root／CompositionRoot的单实例装配；`tests/Mindweave.Tests/KnowledgeSummaryCoordinatorTests.cs`。

**Interfaces:** 消费1／2／3／4；应用持有独立总结参与者，暴露接受／停止／snapshot／Changed及失败修复，节点完成后发已提交事件给6／7。

- [ ] RED用受控transport与真实store覆盖首次／候选、排队时改活配置、后续聊天不扩范围、跨页不中断、同会话互斥、响应完整但提交失败／丢失响应、冻结范围恢复、取消和退出。
- [ ] 知识结果只在内存累积，完整stop＋正常枚举／Dispose＋严格解析后一次原子Complete。截断、空、非法、stop后throw／Dispose失败、取消／中断均0新版本； metadata检查点不含部分标题正文。
- [ ] 接受与Reply共享门禁并flush终态后读；store再次检查。后续Reply优先且不影响本任务输入；凭据、地址、参数来自冻结配置，KnowledgeSummary保持Standard／服务默认思考。
- [ ] 完成保存失败保留完整内存与提交身份、全局暂停，显式修复只重试落盘／读回，无AI重发；恢复仅Interrupted。snapshot／HasActive包含未结束尾；Root成功退出才释放store，错误可返回阅读／复制。
- [ ] 聚焦GREEN筛选`FullyQualifiedName~KnowledgeSummaryCoordinatorTests`，完整Release／build、自审提交、独立审查。不得原生／真实外呼。

## Task 6: 两层节点命名与过期结果保护

**Files:** `Application/Summaries/{NodeNamingProtocol,GroupNamingCoordinator}.cs`、`SqliteWorkspaceStore.SummaryNaming*.cs`；必要装配；`tests/Mindweave.Tests/{SummaryNamingStorageTests,GroupNamingCoordinatorTests}.cs`。

**Interfaces:** 消费1–5；实现`INodeNamingStore`，监听已提交current变更，后台低优先级工作不阻止总结阅读。

- [ ] RED覆盖规范化同名整组、首次基础名、采用／恢复／手改名、保留仍有效区分词、无法区分允许同名、迟到结果／删除／组成员／当前版本／手改标志变化、低优先级／合并去重。
- [ ] 同工作区基础标题组生成有依据的“话题－区分词”，手动显示名保护；版本原始标题／正文／阅读／位置不变。已有自动区分词在current变更后需要检查，删除同名伙伴不强制缩名。无语义同义词归并／跨主题。
- [ ] 精确冻结成员／版本／nameRevision／manual／组epoch，输出仅允许成员集合的有界名字；结果全组CAS，不部分更新、不upsert复活。任务不留总结正文输出副本；实际读取材料与hash可核对，来源已失效则过期拒绝。
- [ ] 每组一个active＋最多一个最新待处理意图；失败不自动重试，变更连发最多一次有界跟进，rename-only不再触发自身。重启不自动外呼；所有请求进入共享两槽及gate，命名使用Standard／服务默认。
- [ ] 聚焦GREEN筛选`FullyQualifiedName~SummaryNamingStorageTests|FullyQualifiedName~GroupNamingCoordinatorTests`，完整Release／build、自审提交和独立审查。

## Task 7: 总结列表／详情、阅读与编辑导航

**Files:** 新建Desktop `SummaryListViewModel`／`SummaryViewModel`聚焦partial、对应Views与正文阅读适配器；修改ChatViewModel／View总结入口、MainViewModel导航／selection／draft、ViewLocator；`tests/Mindweave.Tests/SummaryDesktopTests.cs`。

**Interfaces:** 消费前述过审API，沿用Root／Menu／Content。Network目前是列表／详情，图形关系／布局留S5；知识总结不提供选文分支。

- [ ] Headless真实绑定RED：生成入口scope／费用／预算说明、active Reply禁用、保存首次不强制跳转、列表三独立标记、候选／历史只读、返回来源／普通后退／主题切换、重开最后有效总结和阅读／编辑草稿。
- [ ] 探索区“总结当前会话”明确发起；Network列表可按当前显示名与基础标题查找，点击元信息不记已读，“查看总结”打开正文。详情显示来源追问／选文（有则）、覆盖时间与后续内容、当前／候选／历史身份及模型来源；独立已读开关和当前编辑区，无变化不增版。
- [ ] 正文使用现有Markdown配置及资源限制；测量实际可读文本进入视口区间并注入纯policy，非仅scroll比例。初始打开不已读，短文活动可见3秒／长文85%覆盖，离开／隐藏不累积；手动未读、迟到观察、切版本和已读候选采用正确。
- [ ] 当前草稿跨页自动保存且错误保留内存，阅读候选／历史不丢当前编辑；采用／切当前前显式解决dirty。读取失败不把旧正文贴上新身份，已删除内容不可经backstack重开。原会话草稿／来源阅读恢复保留。
- [ ] 中文可访问名称与状态明确，设置说明区分“长历史上下文压缩”与“知识总结”的思考行为，保存不外呼。聚焦GREEN筛选`FullyQualifiedName~SummaryDesktopTests`，完整Release／build、自审提交和独立审查。

## Task 8: 版本确认操作、原生夹具与阶段门禁

**Files:** Desktop总结版本操作／确认dialog、手动节点显示名、必要主导航投影；`SummaryVersionDesktopTests.cs`；S4专用fixture及Python测试、验证记录。仅本阶段实际需要文件。

**Interfaces:** 消费3／5／6／7的预览／冲突token；所有确认基于真实当前目标，不把UI选择当提交授权。

- [ ] RED覆盖候选采用基线变更二次确认、确认后再变拒绝；丢弃、历史多选、删除当前回退／最后节点明确不可恢复、活动／draft／候选阻止和解决、手动显示名保护。真实compiled XAML／SQLite，必要fault注入。
- [ ] 实现明确按钮与确认文案、预览目标／回退／后果、取消0变更。正文／读取记录实际删除，候选和迟到命名不得复活；删除当前不是先设另一版的强制流程。版本内容标题与节点显示名分开。
- [ ] 专用合成fixture支持Title＋Markdown、同名组输出、故障／延迟／截断，loopback公开非访问密钥；不改旧S3夹具行为或启用真实mock回退。保存S4独立data-root运行入口与测试材料，原S1–S3资产保留。
- [ ] 聚焦GREEN后完整Release／build／Python全fixture，整阶段固定base到head独立审查及必要修复。数据迁移只读原库副本／真实原生新库，旧S3原始文件SHA不变。打包从实际最终源生成，保留旧包，hash／CRC分开于Windows原生验收。
- [ ] 控制器原生核对生成／候选／编辑／阅读／后退／恢复与失败gate。不可恢复删除在具体dialog就绪时按工具规则请用户当次确认，或请用户执行；Windows暂无设备不重复询问。真实S4验收另备可审查虚构材料／模型／新调用上限再请求授权，不挪S3余量；密钥仅本机。

## 自审与验收边界

需求6.4→1／2／5／7；7.1→1／3／7；7.2→1／6／8；8.1→1／2／3／7／8；9.2–9.3→2／4／5／7／8；来源／预算→1／2／5；课程导航→7；实际删除／反馈／命名迟到→3／6／8。S3上下文摘要与S4知识总结在数据／协议／读状态／产物上分离。

每个任务先聚焦行为RED再GREEN，自审提交后由独立reviewer核对spec与quality。报告记录补充测试时序与真实日志，不把编译失败或合成输出称为模型质量。全阶段实际API／平台／课程／价值验证各自记录。任务间不重复要求“是否继续”，只有真实费用、外部权限或产品范围改变才向用户提出具体问题。
