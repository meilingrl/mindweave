# S1 会话与真实聊天实施计划

> **For agentic workers:** 使用 `subagent-driven-development`；按任务测试先行、独立审查，再接下一任务。用户已授权按现行设计继续开发；需新增付费调用、用户凭据或实机协助时再提出具体请求。

**Goal:** 从工作区进入真实会话，配置兼容 Chat Completions 的服务并发送／停止流式回答，可靠保存和恢复，不因页面切换丢失生成。

**Architecture:** 在 S0 的同一 SQLite 写入队列上增加会话、消息、任务与配置。Application 的应用级生成协调器持有内存流、两槽调度和退出状态，Desktop 只投影；HTTP 适配与平台凭据由 Infrastructure 实现。正式 Markdown 渲染及选区适配在 S2 接入，本轮文字必须可选择、复制。

**Tech Stack:** 延续 .NET 10、Avalonia 11.3.21、Microsoft.Data.Sqlite 10.0.13、CommunityToolkit.Mvvm 8.4.2 和 xUnit；HTTP 使用 HttpClient，不引入供应商 SDK。

**Spec:** [需求](../requirements.md) §6.1–6.3、§9；[架构](../architecture.md) §2–7、§12；[交接](../reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md) S1；以 main 当前文档为共享依据。本文件是稳健轨实现细化。

## Global Constraints

- Core、Application 不引用 Avalonia、SQLite 或供应商 SDK；四产品工程不增加 Web 后端。
- 每个会话最多一个未结束回答；全局最多两个 AI 请求，用户提问优先于自动标题。排队可取消；切换页面／工作区不取消生成。
- 接受请求时，用户消息、回答占位、GenerationTask、配置快照和实际输入身份先同事务提交，然后才外呼。配置快照不含密钥，只含版本化凭据引用。
- 终态为 Completed／Stopped／Failed／Interrupted。旧检查点不回退新内容；终态正文固定；迟到事件不能改变终态。重启将未结束任务标为中断，不自动重发。
- 重试／重新生成只限会话最后一条回答，新尝试另存并引用旧尝试；上下文按每个提问最新尝试选择，旧消息可阅读。
- 核心保存失败保留最新内存与输入、暂停新的请求；显式重试保存成功才恢复。退出先完成可取消提示，再取消与排空；保存失败窗口保留可重试／返回，已取消任务不复活。
- 一套活动配置，连接／模型稳定身份分开。密钥只在系统凭据服务；不读 Codex 或其他应用凭据，不降级明文，不在命令参数、日志、异常或导出中记录密钥。
- 服务 URL 只允许 HTTPS（本机回环 HTTP 用于测试）；不接受 userinfo、query 或 fragment；拒绝重定向，Bearer 不发往新主机；不自动重试或切换服务／模型。
- 模型发现是显式目录读取，连接测试是单独显式、可能计费的生成。自动标题可能产生调用费用，设置页说明；配置／发现不自动触发测试或发送。
- S1 超预算明确拒绝并保留输入，不静默截断；自动压缩随 S3 实施。
- 仍复用现有 worktree、数据轨道与单实例。只读参考另一轨道不是代码借用授权。本轮不推送、发布或部署。

## 接口与数据约定

下列签名是任务间契约，Task 1 负责落盘；确有必要调整由控制器记录并通知后续任务。

Core 命名空间 `Mindweave.Core.Chat`：

```csharp
public enum MessageRole { User, Assistant }
public enum GenerationStatus { Queued, Running, Completed, Stopped, Failed, Interrupted }
public enum GenerationKind { Reply, Title }
public sealed record Conversation(Guid Id, Guid WorkspaceId, string Title, bool TitleIsManual, long TitleRevision, DateTimeOffset CreatedAt);
public sealed record ChatMessage(Guid Id, Guid ConversationId, long Sequence, MessageRole Role, string Body,
    GenerationStatus Status, long Revision, Guid? ReplyToUserId, Guid? SupersedesMessageId);
public sealed record AiConfiguration(Guid ConnectionId, Guid ModelConfigId, string Name, string BaseUrl,
    string CredentialReference, string ModelId, int ContextTokenBudget, int MaxOutputTokens, string TokenLimitParameter = "max_tokens");
public sealed record GenerationRecord(Guid Id, Guid ConversationId, GenerationKind Kind, Guid? AnswerMessageId,
    GenerationStatus Status, long Revision, AiConfiguration Configuration, IReadOnlyList<Guid> InputMessageIds,
    long? ExpectedTitleRevision, string? ErrorCode, string? ActualModelId);
public sealed record GenerationWork(GenerationRecord Task, IReadOnlyList<ChatMessage> InputMessages);
public sealed record ConversationDraft(string Text, long Revision);
```

Application 命名空间 `Mindweave.Application.Chat`：

```csharp
public interface IChatStore {
    Task<IReadOnlyList<Conversation>> ListConversationsAsync(Guid workspaceId);
    Task<Conversation> CreateConversationAsync(Guid workspaceId);
    Task<Conversation?> LoadConversationAsync(Guid conversationId);
    Task RenameConversationAsync(Guid conversationId, string title);
    Task<IReadOnlyList<ChatMessage>> ReadMessagesAsync(Guid conversationId);
    Task<AiConfiguration?> LoadActiveConfigurationAsync();
    Task SaveConfigurationAsync(AiConfiguration configuration);
    Task<GenerationWork> AcceptReplyAsync(Guid conversationId, string? userText, AiConfiguration configuration);
    Task<GenerationWork?> AcceptTitleAsync(Guid conversationId, AiConfiguration configuration);
    Task<bool> WriteGenerationAsync(Guid taskId, long revision, string body, GenerationStatus status, string? errorCode = null, string? actualModelId = null);
    Task<bool> CompleteTitleAsync(Guid taskId, string title);
    Task<GenerationRecord?> LoadGenerationAsync(Guid taskId);
    Task<IReadOnlyList<GenerationRecord>> ListActiveGenerationsAsync();
    Task RecoverInterruptedAsync();
    Task<ConversationDraft> LoadDraftAsync(Guid conversationId);
    Task SaveDraftAsync(Guid conversationId, string text, long revision);
}
public interface ICredentialVault {
    Task<string> WriteNewAsync(string secret); // returns new immutable version reference
    Task<string> ReadAsync(string reference);
    Task DeleteAsync(string reference); // only a reference the caller owns and has checked for use
}
public sealed record ChatInput(string Role, string Content);
public sealed record ChatRequest(AiConfiguration Configuration, IReadOnlyList<ChatInput> Messages);
public sealed record ChatStreamEvent(string? Text = null, string? FinishReason = null, string? ActualModelId = null);
public interface IChatTransport {
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string secret, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListModelsAsync(AiConfiguration configuration, string secret, CancellationToken cancellationToken);
}
```

`AiConfiguration` 中的预算是用户配置的容量估计，不宣称模型真实支持该容量。设置默认上下文 32768、输出 2048，可改；`TokenLimitParameter` 只允许 `max_tokens`／`max_completion_tokens` 两种。S1 根据 UTF-8 字节数保守估算（每条固定开销与安全余量由实现写明），不发送超估算预算的请求。

接受时存不可变配置 JSON 和消息 ID 清单；旧尝试保留，未来分支仍能引用原 ID。普通写入使用已存在的单写入队列，不开第二个连接作写入者。以 partial class 将会话、配置和生成方法分开，迁移目录独立为 `DatabaseSchema`，S0 旧库测试显式只用 v1 迁移，保留原验收含义。

自动标题在首条成功回答后作为后台任务入同一两槽调度器，不能覆盖手动标题。只在已有标题修订与接受时一致、未手改时应用；失败不影响聊天，标题任务不保存部分标题。无额外自动生成其他内容。

凭据更新先创建新版本项，再提交配置；提交失败可以移除刚创建且无引用的新项。旧引用仍被任务快照需要时保留，不在本轮自动批量清理。读取只允许本应用本轨道命名空间的引用，其他条目不可枚举。

## Task 1: 会话、任务与配置存储

**Files:** 新建 `src/Mindweave.Core/Chat/ChatModels.cs`、`src/Mindweave.Application/Chat/ChatContracts.cs`、`src/Mindweave.Infrastructure/Storage/DatabaseSchema.cs`、`SqliteWorkspaceStore.Chat.cs`、`SqliteWorkspaceStore.Generation.cs`、`SqliteWorkspaceStore.Configuration.cs`；修改 `SqliteWorkspaceStore.cs` 为 partial 并保持现有队列；新建 `tests/Mindweave.Tests/ChatStorageTests.cs`，按需修正 S0 旧库 fixture；只修改本任务文件。

**Interfaces:** 产出上节全部 Core／Application 契约与 `IChatStore` 实现；`SqliteWorkspaceStore` 同时实现 `IWorkspaceStore`、`IChatStore`。`DatabaseSchema.Migrations` 为只读迁移清单，v2 增加 S1 表，不动已提交 v1 SQL。新建工作区行为保持兼容。

- [x] 先写测试：临时库接受请求后消息／任务／配置／输入身份可重开；同会话活动任务拒绝；终态后新旧修订均不可改写；取消后末尾重试另存，旧尝试保留且后续输入只含最新尝试。例：`Assert.False(await store.WriteGenerationAsync(id, 99, "late", GenerationStatus.Running));`。
- [x] 测试启动恢复将排队／运行标 Interrupted 保留正文、不发送；手动标题竞争拒绝自动覆盖；草稿防修订回退；配置密钥引用冻结；v1 升级前备份且内容完整。先运行 `dotnet test Mindweave.slnx --filter FullyQualifiedName~ChatStorageTests` 确认预期失败，再实现。
- [x] 实现事务与复合外键／事务内归属检查：来源角色、回复同会话、唯一顺序、同会话活动回答唯一、重试限最后回答、配置预算验证。错误不携带原文、密钥或服务响应。
- [x] 默认会话“新会话”；创建时无自动网络调用。标题记录手改标记和修订，自动标题结果应用同事务检查基线；活动任务与终态更新同事务。草稿由明确修订号更新，接受新消息时清除已提交输入对应的旧草稿，不误删后续草稿。
- [x] 运行完整 Release 测试，提交本任务；报告 RED／GREEN 命令、关键输出、文件和接口差异。

## Task 2: Chat Completions 与系统凭据

**Files:** `src/Mindweave.Infrastructure/AI/ChatCompletionsTransport.cs`、`SseReader.cs`、`Credentials/SystemCredentialVault.cs`（可拆平台实现）；`tests/Mindweave.Tests/ChatTransportTests.cs`、`CredentialVaultTests.cs`。不修改存储或 Desktop。

**Interfaces:** 消费 Task 1 的 `IChatTransport`／`ICredentialVault`；产出 `ChatCompletionsTransport(HttpClient)`、`SystemCredentialVault(string credentialNamespace)`；默认生产 HttpClient handler 必须拒绝重定向，通过工厂 `CreateHttpClient()` 提供。`AiConfiguration` 验证可新增 Core 纯函数并测试，但通知控制器。

- [x] 先写真实解析／HttpMessageHandler 边界测试：UTF-8 分块、多行 SSE、CRLF、注释、空 choices／usage、content delta、finish_reason stop／length、`[DONE]`、无终态 EOF、无正文、服务错误、取消、超时；断言发出准确 URL、Bearer、消息和 token 参数，未发额外请求。先观察失败。
- [x] 实现 POST `{BaseUrl}/chat/completions`、GET `{BaseUrl}/models`，只 `delta.content` 作为正文，不把 reasoning／工具调用当回答；SSE 有界解析，不打印原始 JSON／错误体；未知结束、过滤、长度截断明确失败并保留已收文本。成功必须有有效正文及明确 stop 结束，单纯 EOF 不能成功。不自动重试。适配器保留 finish reason 与实际模型事件，不暴露供应商类型。
- [x] 端点只允许合法 HTTPS，回环地址可 HTTP。默认不重定向；即使注入测试 handler 也拒绝 3xx 响应。请求超时以每次调用的 linked CancellationTokenSource 覆盖流读取，不只 headers；不要把调用者取消误报服务失败。响应 dispose 结束客户端连接；不声称服务端停止计费。
- [x] 系统凭据实现 macOS Security.framework 原生 API、Windows CredWrite／Read／Delete；不通过带密钥参数的子进程、不写文件。版本引用包含应用命名空间与随机 ID，不能读取任意其他条目；错误仅状态码与安全说明。未知平台明确不支持且旧聊天仍能读取。
- [x] 凭据测试默认仅验证引用范围／空密钥拒绝与不支持平台；可另提供显式环境开关的系统 roundtrip，用随机合成秘密和专属测试 namespace，清理自己创建的条目。不读取现有用户凭据，不使用真实密钥；原生系统调用结果与非原生测试分别报告。
- [x] 根据 [DeepSeek 官方 Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/) 与 [OpenAI Chat reference](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)核对兼容字段。运行聚焦测试和完整 Release 测试，提交并报告。

## Task 3: 应用级生成协调器

**Files:** `src/Mindweave.Application/Chat/GenerationCoordinator.cs`、`GenerationSnapshot.cs`、`ChatConfigurationService.cs`（按职责拆辅助类）；`tests/Mindweave.Tests/GenerationCoordinatorTests.cs` 与 `ProcessProbe.cs` 中恢复探针。原则上只消费已确认契约；必要调整先向控制器说明。

**Interfaces:**

```csharp
public sealed record GenerationSnapshot(Guid TaskId, Guid ConversationId, Guid? AnswerMessageId,
    GenerationKind Kind, GenerationStatus Status, string Body, long Revision, string? Error,
    bool HasUnsavedChanges);
public sealed class GenerationCoordinator : IAsyncDisposable {
    public GenerationCoordinator(IChatStore store, IChatTransport transport, ICredentialVault vault);
    public event Action? Changed; // non-UI callback; consumer marshals to UI
    public IReadOnlyList<GenerationSnapshot> Snapshots { get; }
    public bool HasActiveWork { get; }
    public bool HasPersistenceFailure { get; }
    public Task InitializeAsync();
    public Task<Guid> SendAsync(Guid conversationId, string userText);
    public Task<Guid> RetryAsync(Guid conversationId);
    public Task StopAsync(Guid conversationId);
    public Task RetryPersistenceAsync();
    public Task ShutdownAsync(); // caller already confirmed; no new dispatch, cancel queued/running, flush
    public void ResumeAccepting(); // after failed exit; cancelled jobs never resurrect
}
public sealed class ChatConfigurationService {
    public ChatConfigurationService(IChatStore store, ICredentialVault vault, IChatTransport transport);
    public Task SaveAsync(AiConfiguration configuration, string? replacementSecret);
    public Task<IReadOnlyList<string>> DiscoverModelsAsync(AiConfiguration configuration, string? unsavedSecret, CancellationToken cancellationToken);
}
```

- [x] 用真实 SQLite＋受控流式 transport 测试：第三请求排队、运行最多2、同会话重复发送拒绝、排队取消、运行停止保留部分正文、迟到 delta 不改变终态、切换页面不影响协调器。先 RED 再实现，不用固定 sleep 推测顺序，使用 TCS／通道控制。
- [x] 接受前读活动配置，估算超预算则拒绝并不清空输入；读取冻结凭据版本，不换新 key。提交工作后调度器按 Reply 优先于 Title；不持有槽等待自己的前置请求。本轮无压缩子任务。
- [x] 流正文在协调器内存，UI 不等待磁盘检查点；写入约每250ms或明显内容增长，终态立即写。每个任务修订单调；停止先封闭该流接受入口再取消，不等不合作 transport 继续修改；取消／超时／EOF 有明确不同结果，消息与任务同事务终态保存。
- [x] 注入存储保存失败后保留最新正文与状态、设置全局暂停，阻止新请求；显式重试落盘后恢复。退出保存失败可重试，不得吞错后关闭或释放实例。启动只恢复记录为中断，不调 transport；子进程模拟流检查点后 kill→重开验证。
- [x] 完成首条回答后接受自动标题，走同一全局2槽且优先级低于回答；标题短输出、绑定配置与输入，手动改名期间迟到结果不覆盖。错误不会破坏正文，不保存部分标题。
- [x] 配置服务保存新密钥版本与 DB 引用，失败清理新建的无引用版本；保留旧版本，避免排队快照失效。未保存 secret 只留当前设置 VM 内存，不能写普通配置／日志。显式模型发现与生成测试保持分离。
- [x] 运行聚焦和完整 Release 测试，提交，记录实际时间／大小常数、TDD 与故障证据。

## Task 4: 桌面聊天、配置与退出交互

**Files:** 修改 Desktop 组装／Root／Main／Content/ViewLocator／MainWindow；新增 `ChatViewModel.cs`、`SettingsViewModel.cs`、`MessageViewModel.cs`、`ChatView.axaml`、`SettingsView.axaml`，按需新增 UI 对话服务接口；新增 `tests/Mindweave.Tests/ChatDesktopTests.cs`；更新 README、TRACK 和本轮验证记录。

**Interfaces:** 消费 Task1–3；旧 `CompositionRoot.CreateMain(IWorkspaceStore)` 和 Root 测试构造入口可保留兼容重载，生产只组装一个 store、一个 coordinator、一个 vault、一个 transport。Main 的唯一活动 ContentStack 继续成立，不能在 ChatViewModel／服务私建业务后退栈。

- [x] 先写 Headless 测试：真实工作区新建会话、输入发送、可观察流、停止；切换两会话后回到最新内存正文；重开读取中断／终态、草稿和最后有效会话；设置保存失败保留输入。既有 S0 导航测试中模拟 ID 的用例按新实体有效性更新，不削弱真实导航断言。
- [x] 会话列表按工作区展示、当前项可辨认，提供新建会话、标题手改；正文显示所有重试尝试与不完整标记，发送／停止／末尾重试可访问，输入失败保留。文字可选择复制；不提供尚未实现的选区分支按钮。
- [x] 使用统一 NavigationRequest 传会话 ID；普通菜单切换保存当前草稿再切换，防抖草稿修订防回退；最近内容恢复必须确认 ID 属于当前工作区。app_selection 扩展详情身份字段属于本任务迁移 v3，需正常迁移备份与验证，不改 v1/v2。
- [x] 设置界面提供名称、Base URL、遮蔽密钥、模型 ID、模型发现、保存、两种 token 参数和上下文／输出预算；说明发送内容到所填服务、自动标题与可能费用；无密钥时可阅读旧会话。模型发现失败仍可手填。真实连接测试可以通过新建测试会话显式发送完成，不新增隐蔽生成请求。
- [x] 正在生成时退出显示可取消确认，取消不停止任何任务；确认后暂停接收、取消队列／运行、刷新保存、排空再关库／释放实例。保存失败保持当前阅读内容并提供重试保存、重试退出和返回工作台；不自动复活已停止任务。设置密钥不经聊天回传。
- [x] macOS 用专用测试库和本地 loopback SSE fixture 验证真实窗口发送、停止、切换、重开；夹具是明确的协议测试服务，不能冒充真实模型。系统合成凭据 roundtrip 若弹 OS 许可则请用户处理。等待真实服务配置／少量调用授权时继续不依赖它的测试与修复。
- [x] 完整 Release 构建／测试、独立全量审查与必要修复；更新 TRACK 区分本地协议验证、原生凭据验证、真实供应商与 Windows 实机。提供可体验启动器和本地提交，不推送。


## 自审与边界

本计划按已确认 S1 推进，未新增产品取舍。重试、队列、标题、退出和配置冻结都沿现行行为；只在对应任务固定当前必要字段。S2 的富文本选区与来源、S3 自动压缩、S4 总结和后续网络不计为本轮完成。真实服务仍需用户在本地填凭据及授权调用额度，不能挪用原型或其他软件凭据。

执行状态（2026-10-26）：最终代码 `78f3fc1` 构建、129 项测试及独立复核通过；早期版本原生流程已实测。用户手动解锁后，最终包针对性原生复查已完成；用户本地保存 DeepSeek 密钥并授权后，已完成 7/8 次生成及 1/1 次目录查询的有界真实验收；长请求截断与停止／并发证据的具体限制见验证记录。准备按既定路线推进 S2。详见 [S1 验证记录](s1-verification.md) 与 [TRACK](../../TRACK.md)。
