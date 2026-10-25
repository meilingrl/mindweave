# S0 骨架与存储实施计划

> **For agentic workers:** 使用 `executing-plans` 逐项执行；每项先验证失败，再实现和复验。已确认的产品、架构与本次“开始开发”授权足以推进，无需重新审批既有设计。

**Goal:** 建立可运行、可新建和切换主题工作区的桌面骨架，支持本地保存、重新打开、单实例与失败恢复入口。

**Architecture:** Core 定义稳定工作区实体；Application 定义存储与三层导航契约；Infrastructure 持有 SQLite 写入连接、串行写入队列、在线备份和单实例通信；Desktop 集中组装并以主 ViewModel 持有唯一活动 ContentStack。只实现 S0 需要的表，其余实体按下表保持引用边界，不预建空功能。

**Tech Stack:** .NET 10（SDK 10.0.402）、Avalonia 11.3.21、CommunityToolkit.Mvvm 8.4.2、Microsoft.Data.Sqlite 10.0.13、xUnit 2.9.3。固定已安装版本，不将本轮变成依赖升级。

**Spec:** [现行需求](../requirements.md)、[现行架构](../architecture.md)、[S0–S6 交接](../reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)。本文件属于稳健轨实施记录，不改写共同需求与架构。

## 全局约束

- 纯 Avalonia 桌面；四个产品工程；Core、Application 不引用 UI 或 SQLite 驱动。
- 单个本地 SQLite 数据库；先取得单实例使用权，再检查版本、备份、迁移和进入工作台。
- 迁移前在线备份，备份失败不迁移；迁移事务同时提交结构与版本；新版结构拒绝写入。
- 不自动用备份覆盖原库，不生成空库冒充恢复成功；恢复页提供重试、打开数据位置和退出。
- `ContentStack` 是主 ViewModel 唯一活动业务栈，导航服务与 View 不另存业务栈。
- `codex-steady` 数据目录、IPC 身份和预留凭据命名空间与其他轨道隔离；凭据后端在 S1 实施，不能声称已完成。
- 本轮无付费服务调用、推送、发布；Windows 11 实机需要对应设备验证。

## 数据关系与事务边界

ID 采用独立生成的 Guid，SQLite 存规范字符串；顺序是会话内单独递增值，不把 ID 或界面顺序当来源。时间统一 UTC。正文终态固定，修订号只用于接受更新而不回退。

| 逻辑关系 | 持久化与约束 | 实施阶段 |
| --- | --- | --- |
| Workspace → Conversation → Message | 工作区与会话外键；消息 ID 稳定，会话内顺序唯一；重试显式引用上一尝试 | S0 工作区；S1 会话消息 |
| Conversation → BranchSource → Message | 子会话唯一来源；同工作区、终态已提交、无环在事务内检查；父关系只存这一处 | S2 |
| BranchContextRange → 祖先会话与消息范围 | 创建分支时继承祖先已冻结范围，再加直接父会话范围；与会话和来源同事务 | S2 |
| GenerationTask → 目标、输入与配置身份 | 输入引用已提交正文、固定消息范围及配置／凭据版本；任务与产物独立，迟到结果不能覆盖终态 | S1 起 |
| ContextSummary → 覆盖消息所在会话 | 归属被覆盖会话，复用方只引用；不归属发起压缩的子会话 | S3 |
| SummaryNode → Conversation；SummaryVersion → Node | 会话唯一节点；当前／候选指针版本必须属于节点；采用、丢弃、删除在事务内检查基线和依赖 | S4 |
| SummaryReadState、SummaryEditDraft → Version／Node | 阅读按版本；编辑草稿独立保存，显式保存生成版本 | S4 |
| NodeLayout → Workspace、SummaryNode | 工作区／节点唯一；节点删除同事务删布局；保存快照以修订号防回退 | S5 |
| ServiceConnection、ModelConfig → 凭据引用 | 库内无密钥；任务绑定接受时版本，未结束引用阻止清理旧凭据 | S1 |
| 主动归档、草稿、阅读位置、最后页面 | 归档隐藏由祖先推导；阅读／最后页面用稳定 ID；失效页面回入口 | 对应功能阶段 |

删除检查：会话先查直接子来源、总结节点、活动用户任务；跨会话引用只允许祖先链，因此无子来源时不能存在合法后代消息引用。消息不单独删除。版本删除先处理节点指针、候选、草稿与活动任务；节点删除同事务删除布局。S0 只提供新建与切换工作区，不提供删除，避免先实现不完整删除语义。

连接模型：后台单写入连接，所有写入经 Channel 串行执行并以事务提交；查询使用只读独立连接。S0 工作区创建／最近页面切换采用完成后更新 UI，不投射未提交数据。流式检查点合并和终态修订规则随 S1 消息功能实现，不提前声称已覆盖。

迁移：版本为 `PRAGMA user_version`，仅顺序向前。已有库需要升级时通过 `SqliteConnection.BackupDatabase` 生成一致性备份，备份完成才开始迁移事务；事务内执行外键与完整性检查。失败保留原库、错误和备份，Root 进入恢复页。恢复备份操作本轮不提供，避免自动替换原库。

## 导航与生命周期

Root：启动／恢复页 ↔ 工作台。Menu：探索、总结网络、设置。Content：统一 `NavigationRequest(Area, Kind, TargetId, Intent)`，以 ID 和意图传参，不传控件或 ViewModel。S0 用真实工作区和各主区空状态走通路径，具体会话与总结详情在 S1／S4 接入。

主 ViewModel 按工作区保存各 Menu 的历史快照；激活一个 Menu 时只装入唯一 `ContentStack`。历史快照是非活动运行期数据，不是第二个服务栈。重启只恢复工作区最后页面，不恢复历史；未来页面 ID 不存在时回工作区入口。

| 操作 | 目标主区的历史 | 后退行为 |
| --- | --- | --- |
| 网络 → 总结详情 | 推入网络主区 | 返回网络入口 |
| 总结详情 → 来源会话 | 推入探索主区 | 回探索主区此前页面；网络历史保留 |
| 侧栏切换主区 | 保存离开主区快照，激活目标快照 | 在目标主区内部后退 |
| 分支返回来源 | 通过 BranchSource 请求定位，独立于历史 | 后退仍按访问历史；不当作再次“返回来源” |
| 切换工作区 | 保存当前运行期历史，恢复目标工作区 | 只在目标工作区、目标主区后退 |

S0 尚无生成或待保存布局，因此退出只需停止新 UI 操作、排空已接受写入、关闭库、释放实例。布局和生成加入时按架构 §12.3 先完成所有可取消提示，再取消任务／丢弃布局。

## Task 1：轨道身份与单实例

**Files:** `src/Mindweave.Infrastructure/Runtime/AppPaths.cs`、`SingleInstance.cs`；`tests/Mindweave.Tests/SingleInstanceTests.cs`；四工程配置、解决方案。

**Interfaces:** `AppPaths.ForTrack(string track, string? dataRoot)`；`SingleInstance.TryAcquire(AppPaths)`；`NotifyExistingAsync`；`ListenAsync(Action, CancellationToken)`；`Dispose`。

- [x] 先写轨道路径隔离、第二实例不能取得占用、IPC 通知和释放后重获占用的测试。核心断言：`Assert.Null(SingleInstance.TryAcquire(paths));`，首实例监听后第二实例通知应触发既有窗口激活回调。
- [x] `dotnet test Mindweave.slnx --filter FullyQualifiedName~SingleInstanceTests`，确认缺失行为失败。
- [x] 实现持久锁文件的独占句柄与当前用户 Named Pipe；锁文件不删除以避免不同 inode 的竞争；IPC 名称哈希规范数据路径和轨道身份。
- [x] 重跑上述测试，包含真实跨进程占用测试；锁失败与权限错误不混为“已有实例”。

## Task 2：SQLite 工作区与安全启动

**Files:** `Core/Workspaces/Workspace.cs`、`Application/Storage/IWorkspaceStore.cs`（位于各工程）；`Infrastructure/Storage/SchemaMigration.cs`、`SqliteWorkspaceStore.cs`；`tests/Mindweave.Tests/StorageTests.cs`。

**Interfaces:** `SqliteWorkspaceStore.OpenAsync(AppPaths, IReadOnlyList<SchemaMigration>?)`；`IWorkspaceStore.ListAsync/CreateAsync/LoadSelectionAsync/SaveSelectionAsync`；`IAsyncDisposable`。

- [x] 测试临时真实库创建两个工作区、切换后重开恢复；并发写入不丢失；失效工作区不能写最近页面。例：重开后 `Assert.Equal(first.Id, (await store.LoadSelectionAsync())!.WorkspaceId);`。
- [x] 测试旧库升级前备份、坏迁移回滚、备份位置不可写时不升级、新结构拒写和损坏库拒绝；断言 `user_version`、原有内容、完整性与备份内容。
- [x] `dotnet test Mindweave.slnx --filter FullyQualifiedName~StorageTests`，确认未实现行为失败。
- [x] 实现单写入队列、独立只读查询、工作区／最近页面表、在线备份和全事务迁移；故障返回异常给 Root，不覆盖库。
- [x] 重跑存储测试与实例测试。迁移过程中真实杀进程由独立子进程持有事务，杀死后重开检查原版本和内容。

## Task 3：主 ViewModel 与可用桌面路径

**Files:** `Application/Navigation/NavigationContracts.cs`；`Desktop/Navigation/NavigationServices.cs`、`ViewModels/*.cs`、`Views/*.axaml`、`ViewLocator.cs`、`App.axaml.cs`、`Program.cs`；`tests/Mindweave.Tests/NavigationTests.cs`、`DesktopTests.cs`。

**Interfaces:** `IRootNavigationService.Navigate(RootDestination)`；`IMenuNavigationService.NavigateAsync(MainArea)`；`IContentNavigationService.NavigateAsync(NavigationRequest)/GoBackAsync`；`MainViewModel.InitializeAsync/CreateWorkspaceAsync/SwitchWorkspaceAsync`；由 `ViewModelBase.SetParameter(NavigationRequest)` 统一注入。

- [x] 使用真实 SQLite、真实主 ViewModel 测试首次引导、创建／切换、主区历史隔离、跨区跳转与后退、重开恢复最后主区；未保存失败保留输入与当前页。
- [x] `dotnet test Mindweave.slnx --filter FullyQualifiedName~NavigationTests`，确认缺失行为失败。
- [x] 实现 ViewLocator、三层服务与主 ViewModel；用 XAML 绑定显示工作区栏、主区入口和诚实空状态；新建工作区输入失败保留，可重试。只有启动组装处引用 Infrastructure。
- [x] 通过 Avalonia Headless 实际加载页面、命令和 ViewLocator；启动失败显示恢复页；按钮具备辅助功能名称。

## Task 4：验证与交接

**Files:** `TRACK.md`、根 `README.md`、`docs/implementation/s0-verification.md`；`tests/Mindweave.Tests/ProcessProbe.cs`、`scripts/build-macos-dev-app.sh`。

- [x] `dotnet test Mindweave.slnx -c Release` 与 `dotnet build Mindweave.slnx -c Release`；检查分层引用和工作树差异。
- [x] 使用专用临时数据在 macOS 原生窗口实际创建／切换／重开，二次启动唤回原窗口；通过测试子进程验证独占与杀进程恢复。
- [x] 按 requesting-code-review 请求独立审查，实现必要修复并重复相关验证。
- [x] 重读最新 README、TRACK 后仅更新稳健轨入口和状态，记录已实现、验证范围、未验证 Windows 及下一步 S1。不改现行需求／架构，不推送。

## 自审

本计划覆盖 S0 与最小工作区路径；S1–S6 的完整聊天、来源、版本、恢复备份交互与网络不在本轮冒充完成。逻辑实体均有归属和阶段；只冻结本轮需要的具体字段。公开接口名称在各任务保持一致，测试以真实 IO 和用户可见状态为主。

执行结果见 [S0 验证记录](s0-verification.md)。完整恢复备份交互尚未实施；本轮完成的是安全拒绝与重试入口。
