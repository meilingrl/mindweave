# S5 总结网络与布局 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将当前工作区的已保存总结投影为可浏览的真实来源网络，实现稳定自动布局、独立布局保存、视口恢复及可取消的离开流程。

**Architecture:** Application 从同一 SQLite 读取快照投影关系，不维护第二份图数据库；Infrastructure 只负责查询和布局事务。Desktop 独立纯布局／视口模块供工作区 NetworkSession 使用，Main 持有每工作区会话，轻量普通节点控件与自绘连线共用坐标变换。

**Tech Stack:** .NET SDK 10.0.402（允许更新补丁版本）、C#、Avalonia 12.1.4、CommunityToolkit.Mvvm 8.4.2、Microsoft.Data.Sqlite、xUnit v3 3.2.2。

**Spec:** [需求 §7.3–7.4、§9、§11](../requirements.md)、[架构 §11、§12.3](../architecture.md)、[详细交接 §2.7、§4、S5](../reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)。这些共享文档与 main 一致，不在轨道内改写。当前用户和 TRACK 已授权持续执行 S0–S6；此计划细化既有设计，不新增产品取舍。

## Global Constraints

- “第一版网络支持缩放、平移、标题查找和显示全部。单击选择并显示元信息，双击或‘打开总结’进入正文页；节点状态不能仅依靠颜色表达，大小不代表掌握程度。”
- “未总结的中间会话通过可区分的路径边与说明保留，不伪装成直接父子关系。”仅当前工作区、已保存知识总结；上下文压缩与未总结会话不是节点。
- “正式版采用背景／连线自绘、节点使用轻量Avalonia普通控件。共同坐标变换保证节点与连线一致”。Core 无图形依赖，Infrastructure 不布局、不动效。
- “SQLite布局记录按节点一行：WorkspaceId、SummaryNodeId、X、Y；工作区与节点ID组合唯一，位置为节点中心的绝对画布坐标，不是屏幕坐标。”不读总结正文绘图，不用整图 JSON、不建视口表。
- “只有显式‘整理布局’才整体重排”。首版自动布局／浏览／显式保存；手动拖动、牵引、悬停吸引、3D 和装饰动效不在本轮。
- “新生成总结的自动位置计算后直接写入该节点的布局行，单行提交，不连带保存其他节点的待保存改动”。直接保存失败安静降级，不置 dirty、不拦离开。
- “保存期间允许继续交互，以布局修订号区分保存快照和后续改动；旧快照成功不得清除后续改动的待保存标记”。有效节点全图一次事务，删除与迟到保存不能复活节点。
- “布局保存失败，改动仍保留在当前窗口”。失败保留旧数据库和当前内存，不影响独立核心 AI gate。
- “保存并继续／不保存／取消”。仅切换工作区（含新建并进入）及退出时提示；打开总结与菜单切换不提示。
- “所有可取消的提示必须在第一个不可逆步骤（停止任务、丢弃内存布局）之前完成。”退出布局选择后还有任务提示时，‘不保存’必须延迟执行，后者取消保留布局与任务。
- “视口不持久化”。重启、切换工作区／工作区入口重进 fit all；正文返回保留中心、缩放和选择；resize 保留中心与缩放；新节点不强制改相机；整理后 fit；显示全部只改视口。
- 复用四工程和 Main 唯一 ContentStack；本轮源码基线 `790b000c6c5e33c2a019b7f44f81aafde7110723`、开始 HEAD `2e680d8800d84ca311c0e9c8cde445cad2ba1fff`。已有 SQL 1–6、S3 请求格式 2–6／golden 不改；新增 schema **7**，schema 6 SQL SHA256 `ECA38643CEE6137AF4E6B01BCE2BF52C2B5024BE823167FBC5E37DC909D1B0F9`。
- 不借用另一轨道代码；不动旧库、旧包或旧证据。S4 余下 11 次付费额度不转移；S5 全程本地合成，真实服务 0 次。Windows 无实机，不把 Headless、跨平台 publish 或历史原型当成实机验收。

## 详细实现决定与文件边界

1. 使用 **已保存坐标**为自动放置参照（MW-C12）。缺位置节点以稳定顺序从保存图放置；同批先算好完整初始图，再逐节点独立保存。内存存在未保存整理时，已存在节点保留内存坐标，新节点取保存参考结果；之后选择不保存，旧节点回旧坐标，新节点仍在自身已提交位置。可能暂时与整理图重叠，显式整理解决；避免把未保存位置间接固化。
2. 图快照有内容指纹 `DataVersion`（包含有序节点元数据／会话路径），用于拒绝迟到计算；布局保存单独以当前有效节点 ID 集合比较，改名／已读变化不误拒绝保存。存储在 writer 事务内重新检查成员，绝不插入已删除节点；新节点的初始写入只 INSERT，已有行不覆盖。
3. 坐标用无 UI 的 `NodePosition(double X,double Y)`；合法坐标有限且绝对值不超过 `1e9`，避免数值溢出。布局参数独立 record 可改：默认节点边界 200×112、同层径向间距 200、组间距 320、碰撞间距 24、最多 24 轮有限修正；不得无限迭代或 silently drop 节点。规模优化以后基于测量。
4. 视口中心为世界坐标，缩放默认 1，范围 0.15–2.5，fit padding 48；指针缩放倍率每单位 1.15。普通双轴滚动／空白拖动平移，Cmd/Ctrl+滚动指针缩放，按钮在中心缩放；可勾选鼠标滚轮直接缩放。尺寸为 0 时延后 fit；窗口 resize 只换投影尺寸，不改中心和比例。
5. `NetworkSession` 独立保存图／内存布局／已保存布局／修订号／dirty／忙状态／视口／选择，并由 Main 每工作区持有。`NetworkViewModel` 是 Content 页面适配器。异步读取、计算、保存都捕获 session 身份与版本；关闭等待所有已接受布局 IO，旧异步不能重启操作。直接写失败留待下一次真正进入重算重试，元数据刷新不能以旧存档覆盖 dirty 图。
6. Main 的布局离开确认返回 `LayoutLeavePermit(Guid WorkspaceId,long Revision,bool Discard)`；取消返回 null。选择保存先完成显式保存，若仍有较新 dirty 则继续要求解决。Permit 只在全部可取消提示完成且身份／修订仍匹配后提交，不保存才丢内存整理；第二个提示取消只撤销 Permit。没有 dirty 也应验证任务提示期间新增 dirty 不被跳过。
7. 保留 S4 `SummaryListViewModel/View` 作已有独立列表和测试兼容；正式 Network 路由在 store 实现 `INetworkStore` 时使用新 VM，只有不提供网络存储的 S0／受控替身走旧 fallback。生产不增加第二套路由、数据库或默认模拟。

## 接口契约（各任务使用同名类型）

新目录 `Mindweave.Core/Network`、`Mindweave.Application/Network`、`Mindweave.Desktop/Network`。Core 仅 `NodePosition` 与坐标校验；关系 DTO 在 Application。

```csharp
// Core.Network
public readonly record struct NodePosition(double X, double Y);
// Application.Network（全部集合 Immutable）
public sealed record NetworkConversation(Guid Id, Guid? ParentId, string Title, DateTimeOffset CreatedAt);
public sealed record NetworkNodeMetadata(Guid Id, Guid ConversationId, string DisplayName,
    string BaseTitle, DateTimeOffset CreatedAt, bool IsUnread, bool HasCandidate, bool HasLaterContent);
public sealed record NetworkNode(NetworkNodeMetadata Metadata, Guid RootConversationId);
public sealed record NetworkPathItem(Guid ConversationId, string Title);
public sealed record NetworkEdge(Guid ParentNodeId, Guid ChildNodeId,
    ImmutableArray<NetworkPathItem> Path); // endpoints included; hidden = Path.Length - 2
public sealed record NetworkGraphSnapshot(Guid WorkspaceId, string DataVersion,
    ImmutableArray<NetworkNode> Nodes, ImmutableArray<NetworkEdge> Edges);
public sealed record NetworkWorkspaceSnapshot(NetworkGraphSnapshot Graph,
    ImmutableDictionary<Guid, NodePosition> SavedPositions);
public enum LayoutWriteStatus { Applied, AlreadyExists, MembershipChanged, NodeMissing }
public sealed record LayoutWriteResult(LayoutWriteStatus Status, NodePosition? ExistingPosition = null);
public interface INetworkStore {
    Task<NetworkWorkspaceSnapshot> LoadNetworkAsync(Guid workspaceId);
    Task<LayoutWriteResult> TrySaveInitialNodePositionAsync(Guid workspaceId, Guid nodeId, NodePosition position);
    Task<LayoutWriteResult> SaveLayoutAsync(Guid workspaceId,
        ImmutableDictionary<Guid, NodePosition> positions, ImmutableArray<Guid> expectedNodeIds);
}
public static class NetworkProjection {
    public static NetworkGraphSnapshot Build(Guid workspaceId,
        ImmutableArray<NetworkConversation> conversations, ImmutableArray<NetworkNodeMetadata> nodes);
}
// Desktop.Network; no Avalonia types in this pure engine
public readonly record struct NodeExtent(double Width, double Height);
public sealed record NetworkLayoutOptions(double RadialGap = 200, double GroupGap = 320,
    double CollisionGap = 24, int CollisionPasses = 24);
public sealed class NetworkLayoutEngine {
    public ImmutableDictionary<Guid, NodePosition> Arrange(NetworkGraphSnapshot graph,
        ImmutableDictionary<Guid, NodeExtent> extents, NetworkLayoutOptions? options = null);
    public ImmutableDictionary<Guid, NodePosition> PlaceMissing(NetworkGraphSnapshot graph,
        ImmutableDictionary<Guid, NodePosition> saved, ImmutableDictionary<Guid, NodeExtent> extents,
        NetworkLayoutOptions? options = null);
}
public readonly record struct NetworkViewport(double CenterX, double CenterY, double Scale);
// NetworkViewportMath provides ToScreen/ToWorld, Fit, Pan, ZoomAt with
// NodePosition for points and double width/height; no DB access.
```

`NetworkSession` 的公开基本接口由 Task 3 固定并记入报告，至少：构造 `(Guid workspaceId, INetworkStore store, NetworkLayoutEngine? engine = null)`，`ReloadAsync(bool fitAll = false)`、`ArrangeAsync()`、`SaveAsync()`（bool 成功）、`Discard()`、`WaitForPendingOperationsAsync()`、`Quiesce()`；可读 `Graph/Positions/SavedPositions/Revision/IsDirty/IsSaving/ErrorMessage/Viewport/SelectedNodeId`，`Viewport`／选择可改但不置 dirty。Task 3 可用可注入异步布局委托覆盖 stale 竞态，生产默认 Task.Run 纯引擎；不为测试新增服务框架。

### Task 1: 元数据关系投影与布局事务

**Files:** Create `src/Mindweave.Core/Network/NodePosition.cs`, `src/Mindweave.Application/Network/NetworkContracts.cs`, `NetworkProjection.cs`, `INetworkStore.cs`, `src/Mindweave.Infrastructure/Storage/NetworkSchema.cs`, `SqliteWorkspaceStore.Network.cs`; Modify `DatabaseSchema.cs`（只追加7）; Test `tests/Mindweave.Tests/NetworkProjectionTests.cs`, `NetworkStorageTests.cs`，及确实受当前 schema 数字影响的 `StorageTests.cs`／`SummaryMigrationTests.cs`。

**Interfaces:** 消费已有 conversation／branch_source／summary_node／current read metadata；产出上节全部 Core／Application 接口与 `SqliteWorkspaceStore : INetworkStore`。独立读取事务不调用逐节点 body reader；用批量 SQL 读节点显示名、基础标题、时间、current read/candidate/OwnHighWaterSequence 后续终态消息。图仅从真实 parent 关系构造，每节点向上找最近总结祖先，路径保留所有中间会话和端点，另算最终根群组。非法跨工作区／循环／缺父关系明确拒绝，不猜边。

- [ ] 写关系测试：A(总结)→B(无总结)→C(总结) 只有 A→C，path 为 A/B/C；无总结根 R 下兄弟同群不加假节点；两根隔离；输入重排指纹／输出稳定；重名、已读更新不改节点身份；缺父／循环／外工作区目标拒绝。

```csharp
var graph = NetworkProjection.Build(workspace, conversations, metadata);
var edge = Assert.Single(graph.Edges);
Assert.Equal(new[] { a, b, c }, edge.Path.Select(p => p.ConversationId));
Assert.Equal(2, graph.Nodes.Length); // B never becomes a blank node
```

- [ ] `dotnet test Mindweave.slnx -c Release --no-restore --filter 'FullyQualifiedName~NetworkProjectionTests|FullyQualifiedName~NetworkStorageTests'` 记录 RED（新 API 未实现或行为未满足）。
- [ ] 实现纯 projection 与 schema7 `node_layout(workspace_id,summary_node_id,x,y)` composite PK/FK 到 `summary_node(id,workspace_id) ON DELETE CASCADE`；finite/bounds 双重校验。`LoadNetworkAsync` 同一只读事务查询全部 metadata、关系及坐标，无正文。`SaveLayoutAsync` writer 内核对真实节点集合、positions keys/expected 无重复且一致、逐行 upsert 一次事务；不吞 IO 错误。初始单节点写遇存在返回其旧位置，不覆盖。
- [ ] 用真实临时 SQLite 测试：v6→7 backup／原业务行保留；新旧软件拒绝新版写；单节点初始写不动其他整理；全图第2行注入 trigger 失败回滚全图；最后版本删除同事务移除 layout；迟到初始写／旧集合保存不能复活；跨工作区节点与非法坐标拒绝；读取前后 read/body 全行不变。RED→GREEN 后运行上条 focused＋相关迁移／删除测试，最后本任务完整 Release 一次。
- [ ] 自审仅追加 SQL，记录 schema6 hash与查询不读正文证据；只提交本任务源／测试，commit `feat: persist summary network projections and node layouts`。报告接口、测试输出和 SHA，不修改 TRACK。

### Task 2: 确定性布局与视口数学

**Files:** Create `src/Mindweave.Desktop/Network/NetworkLayoutEngine.cs`, `NetworkLayoutOptions.cs`, `NetworkViewportMath.cs`; Test `tests/Mindweave.Tests/NetworkLayoutTests.cs`, `NetworkViewportTests.cs`。

**Interfaces:** 消费 Task1 DTO／NodePosition，产出上节 engine/options/viewport 精确接口。`NetworkViewportMath` 签名由本任务写定并报告，后续复用同一变换。布局不依赖 Avalonia、数据库、AI；不复制原型的整控件。

- [ ] 写数值行为测试：打乱节点／边／字典输入100%同坐标；多根分隔／子树大小扇区；大中文标题 extent 参与边界；PlaceMissing 每个旧坐标逐值不动、新点在已保存最近来源邻域找到无碰撞空位；同输入重复恢复同图；空图、单点、500节点深链／宽树有界、有限不丢点；错误参数／NaN拒绝。

```csharp
var engine = new NetworkLayoutEngine();
var first = engine.PlaceMissing(graph, saved, extents);
var second = engine.PlaceMissing(reorderedGraph, saved, extents);
Assert.Equal(first.OrderBy(p => p.Key), second.OrderBy(p => p.Key));
foreach (var pair in saved) Assert.Equal(pair.Value, first[pair.Key]);
```

- [ ] 同任务focused `--filter 'FullyQualifiedName~NetworkLayoutTests|FullyQualifiedName~NetworkViewportTests'` 记录 RED。
- [ ] Arrange：按稳定根和稳定子节点顺序组织森林，按子树数分配径向扇区（无总结根仅分组），组边界留 gap，有限碰撞修正考虑 extent；PlaceMissing 只移动缺坐标节点，来源近邻有限搜索后确定性外圈 fallback。不要每次全重排已有坐标。所有点有限且界内；规模测试报告耗时但不写依赖具体机器的脆弱毫秒断言。
- [ ] 视口实现共享 world/screen 可逆变换、Fit 使用所有节点和标题边界、Pan 按当前缩放换算、ZoomAt 保持指针世界点不动并钳制缩放；宽高0延迟fit，resize不改state。测试点往返误差<1e-9、任意 pointer不漂移、fit边界在padding内（缩放极限允许明确超出）、ShowAll不改positions、NaN/无穷不传播。
- [ ] focused GREEN＋任务完整 Release 一次，自审、只提交本任务源／测试 `feat: add deterministic network layout and viewport math`，报告公开签名与参数，不修改 TRACK。

### Task 3: 工作区网络会话、异步快照与独立保存

**Files:** Create `src/Mindweave.Desktop/Network/NetworkSession.cs`（可将 pending IO 辅助放同目录专责文件）、`src/Mindweave.Desktop/ViewModels/NetworkViewModel.cs`, `NetworkNodeViewModel.cs`; Test `tests/Mindweave.Tests/NetworkSessionTests.cs`, `NetworkViewModelTests.cs`。

**Interfaces:** 消费 Task1 `INetworkStore`、Task2 engine/math；产出接口契约的 session 和 VM。VM `(NetworkSession session, Func<Guid,Task> openSummary)`；`Session`、`Nodes`、`SelectedNode`、`Search`、`OpenSummaryCommand/ArrangeCommand/SaveLayoutCommand/ShowAllCommand/ZoomInCommand/ZoomOutCommand/FindNextCommand`，状态文字／非颜色标记。具体 Node VM 属性在报告列出供 Task5 使用。选择只读 metadata，不调用任何读标记／正文 API。

- [ ] 用可控 async store／engine 写 RED：Arrange提交旧snapshot后Reload新数据，旧结果不覆盖；重入Reload顺序反转；新节点初始保存只写新节点，自动写失败无dirty／无error／不AI gate；dirty期间新增保存只参照SavedPositions；删除后内存、选择和保存不复活；ExplicitSave期间再次Arrange后旧成功仍dirty；保存失败保留内存旧库／固定提示；重复点击busy只1提交。

```csharp
var saving = session.SaveAsync();
await controlledStore.SaveStarted.Task;
await session.ArrangeAsync(); // user can continue interacting
controlledStore.ReleaseSave.TrySetResult();
await saving;
Assert.True(session.IsDirty); // older snapshot cannot clear a newer revision
```

- [ ] focused `--filter 'FullyQualifiedName~NetworkSessionTests|FullyQualifiedName~NetworkViewModelTests'` 记录 RED；实现 session async版本／pending账本，确保后台工作完成回到原同步上下文才变观察属性。Reload 同指纹元数据不重排、不丢dirty；新节点排位基于保存map，失败记录下次入口重试；显式 Save 捕获 valid全部坐标/revision，MembershipChanged 保持dirty并刷新剔除删除。Discard回旧保存map＋缺点确定性位置，不删除业务内容、不清别工作区。
- [ ] VM 搜索同时匹配 display/base title，保留全图只突出匹配，FindNext循环定位；单击更换选择并显示完整来源／创建时间／路径元信息；Open才调用传入路由，未读与候选分别文字可辨。尺寸／fit请求属于session，resize保持state；Quiesce禁止新工作，Wait排空所有已接受IO再允许dispose；取消关闭后的resume路径需支持 Task4。
- [ ] focused GREEN＋本任务完整 Release 一次。自审，提交 `feat: manage network layout snapshots per workspace`，报告精确 session/VM/nodes/viewport API及关闭契约；不写 Main、views、TRACK。

### Task 4: Main 导航、两阶段离开与退出顺序

**Files:** Create `src/Mindweave.Desktop/ViewModels/MainViewModel.Network.cs`, `src/Mindweave.Desktop/Network/LayoutLeavePermit.cs`; Modify `MainViewModel.cs`, `MainViewModel.Summaries.cs`, `RootViewModel.cs`, `Views/MainView.axaml`（布局确认overlay）；Test `tests/Mindweave.Tests/NetworkNavigationTests.cs`, `NetworkCloseTests.cs`，及生产实际切换类型的 S4 `SummaryDesktopTests.cs`／`SummaryVersionDesktopTests.Edges.cs`／`.Recovery.cs`。

**Interfaces:** 消费 Task3 session/VM。Main 持有每工作区session，Network路由创建适配器、summary refresh更新活动网络；全部 workspace真实切换重进fit，正文后退保留，菜单变化不丢layout、不提示。`ConfirmLayoutLeaveAsync()` 返回 `Task<LayoutLeavePermit?>`，`TryCommitLayoutLeave(LayoutLeavePermit permit)` 返回 bool，取消撤销pending不丢布局；Main提供三项具名命令给overlay。Root调同一确认接口，所有现有生成确认／失败恢复语义保留。

- [ ] Headless与真实临时 SQLite RED：打开network fit、pan/zoom/select→summary→GoBack 重建保留；菜单探索→network重进fit但dirty保存；切换工作区／新建workspace脏布局Cancel0变更、Save失败不离开、Discard不动正文；选择／搜索本身不读。
- [ ] `dotnet test Mindweave.slnx -c Release --no-restore --filter 'FullyQualifiedName~NetworkNavigationTests|FullyQualifiedName~NetworkCloseTests'` 记录 RED。
- [ ] Main拆网络partial，保持唯一ContentStack与原changes序列；离开确认命令不能排在持有changes的等待之后造成死锁。layout IO错误自己处理，绝不调用MarkPersistenceFailure。仅根路由有networkstore用新页，其余受控替身兼容现有fallback。Summary节点删除后回network重读、不保留不存在选择。Main关闭先封新network动作并排空已接受IO，失败返回工作台恢复允许操作。
- [ ] Root两阶段：布局Permit→已有任务提示→最终身份/修订再校验→提交Discard→PrepareClose/Shutdown。第二提示取消必须保留dirty与任务；保存布局后取消仍保留已完成保存；提示之间发生新dirty须重新确认。不可在Prompt等待前停止AI／丢内存，不能用旧confirmed绕过新布局；重复close不重复prompt、关闭失败Return不复活旧AI。

```csharp
var closing = root.CloseAsync();
await WaitForLayoutPrompt(main);
main.DiscardLayoutAndContinueCommand.Execute(null);
await closing; // now only the task confirmation is visible
root.CancelCloseCommand.Execute(null);
Assert.True(session.IsDirty);
Assert.False(transport.WasStopped);
```

- [ ] 额外真实生命周期测试：save进行中继续交互／workspace切换permit陈旧；关闭排空直接写，store不会先dispose；layout失败不使AI gate失败；所有现有 S4读草稿、候选、删除恢复测试通过。聚焦 GREEN、一次完整 Release，自审提交 `feat: integrate network navigation and cancellable layout leave`，报告Root顺序和待Task5绑定接口，不改TRACK。

### Task 5: 原生混合网络控件、键盘与交互验证入口

**Files:** Create `src/Mindweave.Desktop/Views/NetworkView.axaml`, `NetworkView.axaml.cs`, `src/Mindweave.Desktop/Controls/NetworkCanvas.cs`（容器与输入）、`NetworkEdges.cs`（背景连线绘制）及节点轻量控件文件如需；Modify `ViewLocator.cs`；Test `tests/Mindweave.Tests/NetworkInteractionTests.cs`。可修改 Task3 VM 仅补本任务控件需要的桥接事件／命令，保持其持久化契约。

**Interfaces:** 消费 Task3公开VM/node属性与Task2同一world/screen变换；注册 `NetworkViewModel => NetworkView`。Nodes普通Button/轻量Control，Tab／方向键选择、Enter显式打开；circle+短标题，选中描边、‘未读’、‘候选’分别文字/形状，来源路径元数据可选择复制。连线仅真实edge，hiddenCount>0虚线+‘经N个未总结会话’，可在选中元信息查完整path，不能只靠颜色。

- [ ] Headless RED验证实际控件树：每节点普通control的AutomationName／tab可达，single只metadata（reading表全行不变），double或Open调用Summary路由；Save有软盘图标、tooltip与Name‘保存布局’，clean disabled、dirtydot、saving busy；CmdS/CtrlS仅网络动作可达时触发一次，输入正文编辑不截走保存快捷键。
- [ ] focused `--filter FullyQualifiedName~NetworkInteractionTests` RED；实现控制树，background/edges自绘，节点与连线用同一变换；PointerCapture仅空白pan（不把节点点击变拖动），两轴wheel平移／modifier或可选directWheelzoom／buttons，放开／capturelost停止pan；resize不改camera。控件Attach/Detach订阅成对，重建不泄漏session或丢viewport，fit等到实际可用size。
- [ ] 工具栏窄窗换行，search/FindNext、ShowAll、Arrange、zoom和比例、Save/error/retry；无节点给探索总结入口说明，不显示假节点。选择详情与路径控制宽度，标题长内容截短但完整metadata可读。所有UI文案不暴露后台revision／hash等实现细节。
- [ ] 真控件输入测试pointer zoom保持、blank pan、modifier/no modifier、ShowAll不脏、Arrange变脏、disabledsave不触发、search显示所有节点只突出匹配、doubleopens、不读metadata、emptyresize、detach重建、500nodes树build/render报告。focused GREEN后完整 Release＋build各一次，固定最终源；提交 `feat: render accessible summary network with navigation controls`。原生Mouse／性能与Windows另行验收，测试不冒充实机。

## 控制器收尾检查（任务完成后顺序执行）

- [ ] 收集每任务真实TDD与一次完整测试输出；按技能独立任务审查，Important/Critical闭环后再下一任务；全阶段一次整包审查，发现统一修复波＋一次限定复审。
- [ ] 冻结最终源码后复用最新完整Release/build结果；只有源码变化、失败或未解决疑点才重跑。Python夹具未改时不无谓重复；若新增fixture，验证其自己的用例。
- [ ] 独立合成S5库、loopback或受控生成，包含两个根／无总结中间／中文长名／未读候选。核验原S1–S4库文件hash不变；原库不升级。保存→正常关闭重开，坐标相同／viewport fit；sourcechat保留；故障trigger仅新库，布局失败dirty+可继续AI；删除同时cascade且不复活。需要不可恢复native删除当次再取授权。
- [ ] Mac实际新包观察选择／打开／返回／搜索／fit／整理／保存／离开三选／任务二次取消／关闭重开。自动化鼠标能力不足时先完成可用AX/键盘部分，具体无法证实点告知用户；不反复要求解锁或重复已验收事实。
- [ ] 构建Mac／Windows专用开发候选、manifest+SHA+ZIP CRC，保留旧包与全部数据。Windows无设备保持未实机；不借另一轨道机器。新增真实付费无必要，不申请也不消费S4余量。
- [ ] 更新 `TRACK.md` 当前状态／下一步与根README现行使用说明，创建 `docs/implementation/s5-verification.md`、现有 `docs/reviews/README.md` 链接最终审查；所有重要Ruling按顺序公开及判断错的代价。保留本计划私有工作区证据，不删；不推送、不合并、不发布、不声称课程完成，继续已授权S6。

## 计划自审

| 规范范围 | 覆盖位置 |
| --- | --- |
| 仅保存节点／真实来源／隐藏中间路径／metadata非已读 | Task1、Task3、Task5 |
| 单行初始位置／全图事务／删除不复活／迁移 | Task1、Task3、Task4 |
| 径向稳定／标题边界／已保存参照／stale拒绝 | Task2、Task3 |
| 视口全部入口规则／重建与resize／搜索／panzoom | Task2–Task5 |
| 显式保存busy和revision／失败隔离／离开三选 | Task3、Task4、Task5 |
| 可取消提示先于Discard/Stop／关闭排空 | Task4 |
| 轻量普通节点／自绘边／快捷键和a11y | Task5 |
| Mac／Windows／原库资产／无付费边界 | 控制器收尾 |

接口在本计划唯一定义；具体属性实现可细化并在任务交接报告中固定，后续任务不得静默改名。无待用户产品决定；若实现证明共享规范缺失／冲突，先记录TRACK，需改main规范时再请求用户决定。
