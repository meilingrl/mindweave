# S6 整理与收尾

验收依据：交接 §2.8、§5 S6（归档、删除、导出、退出顺序、全量故障注入、Windows 全流程、课程交付检查）；需求 §6.3、§6.5、§8.1–8.3、§9.2–9.5、§11；架构 §4.1–4.2、§8.1–8.3、§9.1–9.2、§10.2、§12.1–12.3；MW-C02、MW-C14、MW-C18、MW-C20。界面只求可用，观感留给 S7（D-020）。

本阶段先用一次只读工作流（4 个分析代理，分别对应四块）对照需求、架构与代码列出缺口，再据此成文；代理报告的两个退出顺序缺陷已逐行核对（见“退出顺序”）。

## 拆分与并行

| 块 | 内容 | 依赖 | 主要文件 |
| --- | --- | --- | --- |
| A 退出顺序 | 统一退出状态机，修 S5 遗留的两个顺序缺陷，补草稿刷新与失败重试 | 无 | `App.axaml.cs`、`MainWindow`、`ExitPromptViewModel`、`LayoutPromptViewModel`、`AppHost`、协调器停止接口 |
| B 归档与删除 | 迁移 v8、存储、服务、侧栏“已归档”、只读归档页、删除检查 | MW-C18 已确认（D-021） | 迁移、`IConversationStore`、会话服务、侧栏、会话页、网络投影标记 |
| C 导出 | 单个已保存版本导出为 `.md` | 无 | 新导出服务、`ISystemShell` 保存对话框、原子写入、总结页按钮 |
| D 故障注入 | 真实 SQLite 写失败／忙、多表事务中杀进程、中途断流、恢复失败、界面级存储暂停 | A、B 的接口稳定后补对应用例 | 测试为主 |
| E 交付 | 源码包脚本与体积检查、运行说明、Windows 手动验收清单 | 最后 | `scripts/`、`docs/design/` |

A 与 C 互不相交，各开工作树并行；B 动协调器（按会话取消系统任务）与 A 有交集，A 合并后 B 从新提交开始。每块合并前先合入轨道最新提交并跑全量测试（AGENTS.md）。

## 退出顺序（架构 §12.3）

已核对的缺陷（S5 遗留）：

1. **“不保存”后取消，布局丢失。** 退出时布局提示选“不保存”会立刻丢弃内存布局，随后若有运行中的回答，任务提示再选“取消”——应用没退出，布局却已没了。§12.3 要求不可逆的丢弃排在所有可取消的提示之后。
2. **⌘Q／程序坞“退出”只检查运行中的回答**（`ShutdownRequested` 只问任务提示），可能不问未保存布局；关窗口走 `OnClosing`，两条路径顺序不同。用测试从两个入口驱动确认。

设计：

- 一个 `ExitCoordinator` 状态机：空闲 → 布局提示 → 任务提示 → 停止 → 刷新 →（失败｜释放）。关窗口与生命周期退出都先取消、交给它；完成后用带标记的程序化关闭放行。
- 布局“不保存”只记为“退出确定后丢弃”，到“停止”一步才执行；切换工作区（没有任务提示）仍立即丢弃。
- 任务提示计入**用户发起的回答与总结**（运行或排队），分别计数（“1 个回答、1 个总结”）。标题、同名命名不提示，直接取消；被取消的自动标题在下次回答完成时再排（重启时不外呼，§11）。
- 停止后等任务确认取消，最多 5 秒；未确认的回答保留最近检查点，下次启动标为中断。
- 刷新：消息草稿、总结编辑草稿（含 1 秒节流里的）、阅读位置、最后位置、写队列排空；之后尽力 `wal_checkpoint(TRUNCATE)`（忙则忽略）。草稿写失败重新标脏，不再静默丢弃。
- 刷新在窗口关闭**之前**完成。失败显示“部分内容未能保存”，可“重试退出”或“返回”；返回后只接受新操作，已取消的任务不复活。
- `AppHost.DisposeAsync` 保留为兜底，加 try/finally 保证释放单实例锁。

## 实现记录（A 退出顺序）

- `ExitCoordinator`（取代 `ExitPromptViewModel`，`RootViewModel.Exit`）实现空闲 → 布局提示 → 任务提示 → 停止 → 刷新 →（失败｜释放）。`MainWindow.OnClosing` 与 `App.OnShutdownRequested`（生命周期 `ShutdownRequested`）都只调用 `InterceptClose()` 并取消；完成后经 `ISystemShell.ExitApplication()` 程序化关闭，只有“释放”阶段两个钩子放行。测试从两个入口驱动：仅布局、仅回答、两者都有（布局在先；任务提示取消后布局仍未保存）；缺陷 1 先用旧代码复现失败。
- 布局提示新增退出模式：`HoldExit` 的“不保存”只把待丢弃的会话交给协调器，到“停止”一步才 `DiscardChanges`；切换工作区仍立即丢弃。
- `GenerationCoordinator`：`UnfinishedUserWork`（回答、总结分别计数，运行与排队都算）；`StopForExitAsync(等待上限)` 封闭入口、取消排队、停止运行并最多等 5 秒（`ExitOptions.StopWait`），不停重试循环；`Reopen()` 只重新开放入口；`SaveUnsavedAsync()` 报告失败终态写入是否已补存；`ShutdownAsync` 改为在其后做最终收尾（幂等）。被退出停止或取消的自动标题发 `TitleAbandoned`，会话服务据此允许下次回答完成时再排；取消排队的总结／命名补发 `SummaryTaskFinished`。
- 刷新在窗口关闭前完成：工作台让所有打开页面与被移出栈的页面保存（`IPageFlush`，总结编辑草稿含 1 秒节流中的改动，失败保持待存；`SummaryViewModel.Dispose` 不再自行发起保存），`ReadingStateService.FlushAsync` 写失败重新标脏并返回草稿是否全部写入，补存失败的回答终态，重写最后位置，`IDatabaseLifecycle.DrainAsync` 排空写队列后尽力 `wal_checkpoint(TRUNCATE)`（忙则记录并忽略）。
- 偏差：阅读位置与最后位置写失败**不**阻止退出（需求 §9.3“仅布局／页面状态失效时可降级”），只有消息草稿、总结编辑草稿与回答终态写失败才显示“部分内容未能保存”。阅读位置失败仍标脏留待下次。
- 偏差：退出检查点的忙等待临时改为 200 ms（连接默认 5 s），否则一个持有快照的读者会让退出卡 5 秒；`AppHost` 兜底停止也按同一上限（协调器已停止过则不再等待），免得不响应取消的请求让进程挂住。
- 失败界面只有“重试退出／返回”（按设计）；存储长期损坏时没有“仍然退出”，只能强制结束进程（只保证已提交检查点）。是否加第三个选项待用户决定。
- 为测试注入，`IReadingStateStore` 的注册改为 `TryAdd`。界面只做最简覆盖层（任务提示、“正在停止并保存”、失败），观感留给 S7。
- 审查后修正（对抗性审查九项，均先写出失败的测试再改）：
  - 被移出栈的总结页草稿保存失败后，工作台继续持有该页，每次退出重试都再存一次，存成功才放下；先存被移出的页、再存打开的页，同一草稿重新打开后的新输入覆盖旧的。此前“重试退出”会在草稿仍未写入时直接退出。失败提示改为“程序仍保留着这些内容，写入成功之前不会退出”（原文“保留在窗口中”对被移出的页不成立）。为注入写失败，`ISummaryStore` 注册改为 `TryAdd`。
  - 协调器的停止步骤（含丢弃布局）抛错时记录日志并进入“失败”，不再永远停在“正在停止”；丢弃布局失败只记录并继续。停止没有完成时“重试退出”会重新执行停止再保存。
  - `GenerationCoordinator` 补存失败终态写入的各次尝试（后台重试与退出的最终补存）用信号量串行，结果在持有信号量时读取；此前后台重试正写着一条已出队的写入时，退出会看到空队列而判定成功。
  - 退出提示计数排除实时状态已终结、只是尚未释放槽位的回答（与 `HasUnfinishedAnswer` 一致）。
  - 退出进行中完成的回答不再排自动标题：入口已关闭时直接放弃并清除“已请求”标记；检查之后才关闭的，入队失败时清除标记并把刚写入的任务行标为取消。返回后下一次回答完成会再排标题，不留排队中的任务行。
  - 恢复页的“退出”改经退出协调器；`SystemShell.ExitApplication` 改用 `TryShutdown`。Avalonia 12 的 `Shutdown()` 是强制退出：不触发 `ShutdownRequested`，窗口 `Closing` 的取消也被忽略，会绕过退出顺序。
  - `AppHost` 兜底停止在协调器已进入“释放”时不再等待（此前窗口已关、界面线程阻塞、单实例锁未放，还会再等最多 5 秒）；“释放”开始后二次启动的激活请求不再转给窗口（此前会对已关闭的窗口 `Show()` 而抛错）。
  - 测试：重新开放后的测试改为等待任务全部结束，不读瞬时计数（单独运行时 4/4 失败）；带读者的检查点测试限定排空在 1.5 秒内完成（原测试等 10 秒，旧的 5 秒忙等待也能通过）。
  - 未加“仍然退出”选项，仍待用户决定。
- 未验证：真实 macOS ⌘Q／程序坞退出、系统关机时的退出，以及 Windows 关窗口（无界面测试只调用钩子方法，没有经过平台生命周期；`TryShutdown` 的实际行为同样只在真实生命周期里体现）；Windows 上退出检查点与文件锁的交互；真实 HTTP 客户端在 5 秒内确认取消的情况。
- **接上导出**：退出在保存草稿之前先等待正在写文件的导出（`SummaryExportService.WhenIdleAsync`），不截断文件、不留临时文件；用例：导出写入被挂起时关窗口，应用等写完才退出。
- **复核后再修（复核代理发现的回归）**：被离开的总结页草稿保存失败后会被保留并在退出时重试，但若之后重新打开同一总结并保存了更新的草稿、或放弃了编辑，退出时旧草稿会被写回覆盖。处理：同一草稿（以总结节点为键）上较新的页面写入草稿（保存、放弃或提交编辑）后，放下旧页面保留的残留。代价：重新打开时看不到那次没存上的旧草稿（它从未写入），以你之后的操作为准。两个用例（新草稿／放弃）关掉修复时都失败。
## 归档与删除

- **迁移 v8**：`conversation_archive(conversation_id 主键 → conversation ON DELETE CASCADE, archived_at)`。一行＝你主动归档了这个会话；因祖先被归档而隐藏是读取时推导（`branch_context_range` 中任一祖先有行），从不写到后代。会话行不改。
- **归档**：作用于会话及其子树。一个写事务内：子树里有运行或排队的回答／总结则拒绝并列出；之前先取消子树的标题与命名任务（MW-C14）。只写本会话一行，子分支各自的归档状态不动；恢复父会话不会恢复单独归档的子分支。
- **可见性**：侧栏默认隐藏；侧栏底部“已归档（N）”打开归档页，可恢复、可删除。总结与网络节点照常保留，详情与列表标“来源已归档”。从总结回到已归档来源时只读打开，带归档标记与“恢复并继续”。父会话里指向已归档子分支的书签仍显示，带标记。
- **已归档会话不接受新任务**：提问、分支、重试、生成总结都改为“恢复并继续”；在写事务内强制，保证隐藏的会话里没有用户任务。已有总结的阅读、编辑、采用、删除版本照常。
- **深层恢复（MW-C18，你已确认，D-021）**：恢复一个因祖先被归档而隐藏的会话时，同时清除其路径上被归档祖先的标记；确认框先列出这些祖先，以及会因此重新出现的其他分支数。不在路径上的单独归档不受影响。
- **删除**：仅当没有子分支、没有总结、没有运行或排队的回答／总结时允许（MW-C02 不变量下这已涵盖冻结范围、经由链、引用与上下文摘要）。检查与删除在同一写事务；有依赖时一次列出全部原因，并提供归档。删除内容：该会话的全部任务记录（请求追踪随之级联）、消息（按序号倒序，因 `supersedes_id` 是 RESTRICT）、本会话作为子分支的来源记录、会话（草稿、阅读位置、上下文摘要、归档标记级联）；同一事务把指向它的“最后位置”清空。不 VACUUM。确认框写明不可撤销、未发送草稿会一起删除。
- **删除后**：工作台移除该会话所有页面；若正在看它，回到父会话或探索首页。迟到的标题结果、阅读位置写入因外键失败时忽略，不当作存储故障。

## 导出（需求 §8.3）

- 每次导出**一个已保存版本**（当前版本或页面上显示的历史版本）为 `.md`；候选版本不可导出（提示先采用），编辑草稿不包含（页面上说明）。不导出会话、网络或项目包。
- 内容：`# 版本标题`、空行、来源说明（引用块：来源会话名、覆盖到第几条消息、版本号、生成／手改、创建时间、“导出自 Mindweave”）、空行、正文原样（公式定界符不改写）。不含模型、连接、地址、内部 ID、凭据。可在导出时去掉来源说明。UTF-8 无 BOM、LF、末尾换行。
- 保存对话框：建议文件名为清理后的标题（去掉 Windows 非法字符、保留设备名、末尾点与空格，约 80 字截断，空则 `summary-v{n}`），同名覆盖由系统对话框确认（不再二次确认）。
- 原子写入：同目录临时文件 → 刷盘 → 替换；Windows 共享冲突短暂重试约 1.5 秒后报“文件正被其他程序使用”；任何失败都删除临时文件，原文件不受影响。退出时等待进行中的导出。
- 不受生成任务影响；点击时按 ID 重读，版本已被删除则提示且不写文件；不改阅读状态。
- **E5 交换包**：原计划“S6 导出时实现”，但需求 §8.3 与架构 §8.1 规定首版没有项目包。你已决定 S6 不做（D-021），只遵守 E5 的约束“对外输出不直接序列化存储记录”（导出用独立格式化器）。

## 故障注入

在已有用例（包装存储抛错、迁移失败、杀进程重启、布局保存失败）之外补：

- 真实写失败：临时库 `PRAGMA max_page_count` 造成 `SQLITE_FULL`，走真实写队列与回滚；只读文件造成 I/O 错误。
- 运行中被外部进程长期占用写锁（子进程 `BEGIN IMMEDIATE` 超过忙等待）。
- 多表事务中杀进程：创建分支、采用／删除版本、归档、删除会话、保存布局；重启后完整性与无悬空引用检查。
- 回答流中途断开（已有部分正文）；500／503 分类。
- 恢复备份失败（Windows 上目标文件被占用）不丢当前库。
- 界面级：存储暂停横幅、分支与总结入口禁用、恢复写入后自动解除。
- 新增的杀进程与锁用例先在 `stress` 工作流重复运行再算通过。

## 交付检查

- 脚本用 `git archive` 打源码包：不含 bin／obj／运行数据，检查不超过 10 MB 并列出内容；测量 Windows 自包含发布体积。
- 运行说明写在本轨道的 `docs/design/`（根 README 是 `main` 的共享文本，不在轨道上改）。
- Windows 全流程：§11 每个场景一行的手动清单，你在笔记本桌面会话里用发布的程序逐项走（凭据、输入法、文件对话框、二次启动激活只能这样验证）；自动部分用 `scripts/windows.sh test`。
- 课程报告（PDF、测试表、截图）属于稳健轨；本轨道只报告发现供参考。

## 不在本阶段

界面打磨（S7）；E5 交换包（首版之后，D-021）；工作区级联删除（需求 §6.1.1 非目标）；回收站。

## 实现记录（C 导出）

- **结构**：`Application/Export` 下有独立格式化器 `SummaryMarkdown`（不序列化存储记录，E5 的约束）、文件名清理 `ExportFileName`、`SummaryExportService`（按 ID 重读，已删除报 `VersionMissing`，候选报 `IsCandidate`），以及写入接口 `IExportFileWriter`；`Infrastructure/Export/AtomicFileWriter` 实现原子写入；`ISystemShell.PickExportFileAsync` 调系统保存对话框（`SuggestedFileName`、`.md`、`ShowOverwritePrompt=true`），测试用 `RecordingShell.NextExport` 代替。
- **格式**：`# 标题`、空行、单行引用块“来源会话：… · 覆盖到第 N 条消息 · 第 n 版（生成／手改） · 创建于 yyyy-MM-dd HH:mm · 导出自 Mindweave”、空行、正文原样；UTF-8 无 BOM、LF、末尾换行。标题与会话名中的换行改为空格（否则会跑出标题／引用块），正文只把 CRLF／CR 统一为 LF。N 沿用页面上的口径（`ToSeq + 1`），时间按本机时区。来源会话名与侧栏同一规则（标题 → 分支引用 → 首问），因 Application 不能引用 `UiText`，在导出服务里另写一份；界面测试只核对了有标题的情况（与会话页标题一致），分支引用与首问两条后备未单独测。
- **重读两次**：打开对话框前读一次取建议文件名，对话框返回后在写入步骤里再读一次并格式化；对话框打开期间版本被删除，则提示且不建文件（流程测试与界面测试各一例）。导出不写库，阅读状态不变（有测试）。
- **原子写入**：同目录临时文件 `.mindweave-export-{guid}.tmp`（固定短名，避免长文件名加后缀超限）、`FileShare.None`、`Flush(true)`、`File.Move(overwrite: true)`；共享冲突（32／33）与 `UnauthorizedAccess` 在 1.5 秒内每 100 ms 重试，之后分类为“正被使用”／“只读或无权限”／其他失败；任何失败（含取消）都删除临时文件。拿不到本地路径时退回 `OpenWriteAsync` 原地写并记日志（非原子）。
- **偏差**：文件名清理还去掉了开头的点（否则在 macOS／Linux 上成为隐藏文件）；设备名处理为在主名后加 `_`（`CON` → `CON_`、`nul.txt` → `nul_.txt`）。成功提示仍用页面原有的 `Notice`，新增 `NoticeIsError` 让成功提示不显示为错误色。
- **审查后修正（00ab220）**：任何异常都不再从导出按钮抛到界面线程（关闭的存储、对话框异常都转为提示）；来源会话名由界面层按侧栏同一规则传入（不在应用层复制 UiText 的规则）；文件名去掉重复的 `.md`、识别“设备名＋空格＋扩展名”、按 UTF-8 不超过 240 字节截断；“被占用”与“只读”改看只读属性区分，补“编辑器占用但允许读取”的 Windows 用例；重试成功用例改用长窗口，消除计时抖动。未修：沙盒位置（无本地路径）在导出被拒绝时不释放 `IStorageFile`（罕见，记为已知）；崩溃在临时文件与替换之间时，用户目录留下 `.mindweave-export-*.tmp`。
- **给 A 块的接口**：`SummaryExportService.WhenIdleAsync()`（从不抛错）与 `InProgress`。只计“重读＋写入”这一步，不计打开着的保存对话框；A 块在关闭存储前 `await` 它即可。若对话框打开时退出已完成并释放了存储，随后的导出会因存储已释放而失败——这一路径未处理，留给 A 块决定（例如退出开始后拒绝新的导出）。
- **未验证**：Windows 上的三项（目标被独占句柄占用时重试后报“正被使用”、占用在窗口内释放后成功、只读文件报“只读”）只写了测试、标为仅限 Windows，本机跳过，需在 `scripts/windows.sh test` 中跑；`File.Move` 覆盖被占用文件时 Windows 报共享冲突还是拒绝访问未实测，两种都按重试处理。真实系统保存对话框（建议文件名、覆盖确认、扩展名补全）没有在桌面会话中手动走过；无本地路径的回退只用文件流模拟过。macOS 上覆盖只读文件会成功（改名不受目标只读属性限制），与 Windows 行为不同。

## 实现记录（B 归档与删除）

- **迁移 v8**：`conversation_archive(conversation_id 主键 → conversation ON DELETE CASCADE, archived_at)`，STRICT、WITHOUT ROWID；同一迁移加索引 `branch_context_range_by_ancestor`（按祖先找子树，归档检查与恢复计数用）。迁移照旧单事务、先做校验过的备份；`SchemaV8MigrationTests` 从真实 v7 数据升级（有备份、无归档行、外键检查干净；归档行随会话级联删除）。
- **隐藏只在读取时推导**：会话自身或 `branch_context_range` 中任一祖先有归档行即隐藏（`SqliteArchive.HiddenCondition`），从不给后代写标记。侧栏列表在同一条查询里带出 `IsArchived`／`IsHidden`，祖先查找走范围表主键（`child_id` 在前）；用 `EXPLAIN QUERY PLAN` 断言为 `SEARCH … USING PRIMARY KEY (child_id=?)`、没有表扫描。没有跑完整规模基准（`scripts/perf.sh`）。
- **存储**：新接口 `IConversationArchiveStore`（`SqliteArchiveStore`）：子树、归档、恢复计划、恢复、删除、`ArchivedAmongAsync`。每个写操作在一个写队列事务里先检查再写。
  - 归档：子树（本会话＋所有范围包含它的子会话）里有排队或运行中的回答／总结（看任务行，回答另看未终态消息）则拒绝并列出这些会话；否则只写本会话一行。
  - 恢复（D-021）：清除路径上（自身＋被归档的祖先）的归档行，返回因此重新可见的会话；路径外的单独归档不动。恢复计划给出被归档的祖先与“其他重新出现的分支数”（不含自身与这些祖先）。
  - 删除：仅当无子分支、无总结节点、无未结束的回答／总结时执行，否则一次返回全部原因（`DeleteBlockers` 位标志）。删除顺序：该会话全部任务行（`ai_request` 级联）→ 本会话作为子分支的来源行 → 把消息的 `supersedes_id` 置空 → 删消息 → 把指向它的 `workspace_location.content_key` 清空 → 删会话（块、草稿、阅读位置、上下文摘要、范围、归档行级联）。用例检查重试／重新生成链删除后**整个库里每张表每个文本列**都不再出现该会话、其消息与任务的 ID（及页面键），且 `PRAGMA foreign_key_check` 干净。
- **事务内强制**：`AppendQuestionAsync`、`AddRetryAsync`、`AddTaskAsync`、分支创建都在事务内检查“会话存在且未隐藏”，抛 `ConversationArchivedException`／`ConversationMissingException`；服务映射为 `SendRejection.ConversationArchived／ConversationMissing`、`SummarizeRejection` 同名项、`BranchRejection.ConversationArchived`。用例绕过服务直接调存储，同样被拒、不留任务行。
- **服务**：`ConversationService.ArchiveAsync／PlanRestoreAsync／RestoreAsync／DeleteAsync／ArchivedAmongAsync`，新事件 `VisibilityChanged`，并对每个受影响会话（及删除时的父会话）发 `ConversationChanged`。归档前用 `GenerationCoordinator.CancelBackgroundAsync(会话集合)` 取消子树的标题与命名（排队的标为取消、运行的停止并等待结束；标题照旧发 `TitleAbandoned`，恢复后下一次回答完成时再排）；删除前同样取消本会话的。协调器的 `SaveAsync` 遇到 `ConversationMissingException` 只记日志、按“未生效”处理，不进入存储暂停。
- **界面**（只求可用，观感留给 S7）：侧栏过滤隐藏会话，底部“已归档（N）”（N＝主动归档的会话数，N＞0 才显示）打开 `ArchivedRoute`（探索分区，可作为最后页面恢复）。归档页按树列出全部隐藏会话（以主动归档的会话为根，单独归档的子分支原位标“已归档”），每行可打开、恢复、删除。会话页：头部“归档”“删除”；隐藏时只读（输入框换成说明＋“恢复并继续”，“总结此会话”换成“恢复并总结”，不能重试、不能从选区分支），说明区分“已归档”与“所在的上级会话已归档：…”。深层恢复先确认（列出祖先与重新出现的分支数）。删除先确认（不可撤销、未发送草稿一并删除）；被拒时列出全部原因并提供“归档”。删除后 `Workbench.Forget(谓词, 回退页)` 从所有工作区、槽位、分区栈中移除并释放该会话的页面；正在看它则打开父会话，根会话则回到探索首页。父会话答案下指向单独归档子分支的书签保留并标“已归档”。网络节点与列表视图带 `SourceArchived`：详情的来源行加“（来源已归档）”，列表的标记加“来源已归档”；画布与布局行不变。
- **偏差**：
  - 归档／删除放在独立接口，没有扩充 `IConversationStore`（测试里包装该接口的假存储不必跟着改）。
  - 消息删除用“先置空 `supersedes_id` 再删”，没有用 `ORDER BY seq DESC`：SQLite 默认编译不支持带 `ORDER BY` 的 `DELETE`。
  - 隐藏会话不仅拒绝回答／总结任务，**也拒绝新的标题任务**（命名任务照常，因为它属于总结节点）：否则归档取消标题后、紧接着排上的标题仍会漏进来。被拒时清除“已请求标题”标记，恢复后可再排。
  - 只有深层恢复（会清除祖先的标记）才确认；恢复自己归档的会话、以及归档本身都不确认（可逆）。
  - 被取消的命名任务原来会让该同名组的“已排队”标记一直留着（本会话内不再命名），归档会触发这种取消，因此在命名任务结束（无论是否产出）时清除该标记。
  - 分支的父会话已被删除时仍报“来源回答已不存在”（`SourceNotFound`），没有新加一个“会话不存在”的分支拒绝原因。
  - 页面键格式 `conversation:{id}` 移到 `PageLocation.ConversationKey`（应用层），因为删除事务要清空指向它的最后位置；`ConversationRoute` 改为引用它。
  - 总结页本身不显示“来源已归档”，只在网络详情与列表里标；从总结回到来源会话时会话页只读并带标记。
- **未验证**：没有在真实窗口里手动操作（只有无界面测试与截图 `08-archived`）；Windows 未跑；完整规模性能基准未跑；归档／删除事务中杀进程的故障注入留给 D 块；确认框显示后、点确认前路径上的归档状态若被别处改变，恢复按事务内重新计算的路径执行，确认文字可能与实际清除的祖先不一致（罕见，未处理）；标题与删除的竞态只在存储层构造（删除绕过服务、标题仍在运行），没有构造“取消之后才排上的标题”这一真实时序。
- **审查后修改**（以下取代上文“服务”一条里取消的先后与 `SaveAsync` 的说法）：
  - 被拒的归档／删除不再取消任何标题或命名。原来先 `CancelBackgroundAsync` 再进事务：删除因有分支被拒、或归档因子树里有总结在跑被拒时，标题已被停止，`TitleAbandoned` 清掉“已请求”标记，要等下一次回答完成才重排，用户被拒后可能再也不发问，会话就一直没有标题。现在先跑事务，只有成功才取消：归档在同一提交里把子树中**排队的**标题与命名任务行标为 `cancelled`（MW-C14 的“确认时取消、同一提交检查”落在事务内），提交后再 `CancelBackgroundAsync` 停止运行中的并清除子树的“已请求标题”标记；删除事务本来就删掉该会话的全部任务行，提交后再停止运行中的。用例：有分支的根会话删除被拒、子分支总结在跑时归档被拒，标题任务都还在 `running`／`queued`，放行后照常写入自动标题。
  - 归档前存好行、归档后才进队列的标题不再漏网：它的行已在归档事务里标为 `cancelled`，`MarkRunning` 失败即返回。`SetAutoTitleAsync` 另加“未被归档隐藏”条件，提交后、被停止前恰好完成的运行中标题也写不进隐藏会话。用例直接构造这一时序（存储层插行 → 归档 → 手动入队）。
  - 删除被拒的说明只在会话未被归档／隐藏时建议“改为归档”：`UiText.DeleteRefused(blockers, offerArchive)`，会话页传 `!IsArchived`，归档页传 `false`。
  - 删掉 `GenerationCoordinator.SaveAsync` 里的 `catch (ConversationMissingException)`：传给 `SaveAsync` 的写入（标记运行、检查点、结束回答／任务、自动标题）都不会抛它，删除后迟到的写入是改到 0 行的 `UPDATE`，请求记录的外键失败由 `RecordAsync` 吞掉；原用例去掉该分支后仍通过，测的是行为而非这个分支。
  - `IConversationArchiveStore.SubtreeAsync` 不再有调用方，删去。
  - 仍未验证：归档提交与停止运行中标题之间的竞态只靠 `SetAutoTitleAsync` 的条件兜底（该条件有存储层断言），没有确定性地构造“提交后、停止前完成”的时序。

## 实现记录（D 故障注入）

本节为 D 块第一部分（存储写失败／忙、断流、恢复失败、备份中杀进程、界面级暂停）。归档与删除相关的杀进程用例由 B 块接口稳定后再补，本次未动归档／删除代码。

- **注入方式**：都是真实故障，不经包装存储。磁盘写满用应用自己的写连接上的 `PRAGMA max_page_count`（按连接生效，经 `WriteAsync` 设置；先占满空闲页使余量精确），超过即 `SQLITE_FULL`，与真盘满相同；写锁被占用由测试另开连接 `BEGIN IMMEDIATE`；断流在 HTTP 层，自定义 `HttpMessageHandler` 的响应流读到第二块后抛 `IOException`／`HttpIOException`；恢复失败在当前库已改名后让 `-shm` 的改名目标被占（Windows 上另有独占句柄两例）；备份中杀进程用子进程，经仅供测试宿主的 `BeforeBackupForTests` 在 `VACUUM INTO` 的连接上装 SQLite 进度回调，临时文件已有内容时挂住再杀。
- **矩阵**（Faults 目录在 `tests/Mindweave.Infrastructure.Tests/Faults/`）：

| 故障 | 用例 | 结果 |
| --- | --- | --- |
| 盘满：写队列事务 | `DiskFullTests.A_write_that_does_not_fit_is_rolled_back_whole_and_reported_as_a_full_disk` | 通过：整笔回滚，`StorageWriteException(Full)`，探测写失败；放开后探测成功且不留行 |
| 盘满：追加提问 | `DiskFullTests.A_question_that_does_not_fit_leaves_nothing_and_pauses_requests_until_space_returns` | 修复后通过：拒绝为“存储不可用”、各表行数不变、完整性与外键检查干净；仍满时短问题也被拒；放开后自动解除，同一问题成功 |
| 盘满：新建会话 | `DiskFullTests.A_new_conversation_that_does_not_fit_is_refused_and_leaves_no_conversation_behind` | 修复后通过：不留会话、不外呼 |
| 盘满：总结版本保存 | `DiskFullTests.A_summary_version_that_does_not_fit_leaves_no_version_and_pauses_requests_until_space_returns` | 修复后通过：任务失败为“无法保存”、无节点无版本、进入存储暂停；放开后重新总结成功 |
| 盘满：布局保存 | `LayoutDiskFullTests`（Desktop） | 通过：整笔回滚、`SaveFailed` 且仍待保存、内存布局不变、核心内容**不**暂停；放开后再保存写入全部 150 个节点 |
| 写锁超过忙等待：单次写入 | `BusyWriterTests.A_write_waits_out_the_busy_timeout_then_fails_as_busy_and_succeeds_after_release` | 通过：200 ms 忙等待下约 1 秒报 `Busy`（驱动另按整秒命令超时重试），释放后写入成功 |
| 写锁：回答终态 | `BusyWriterTests.An_answer_finishing_while_another_program_holds_the_lock_is_kept_and_saved_after_release` | 通过：完整正文留在内存可读、库里仍是检查点、新提问被拒；释放后终态补存为完成、任务完成、自动解除 |
| 写锁：提问 | `BusyWriterTests.A_question_sent_while_the_lock_is_held_is_refused_whole_and_can_be_sent_after_release` | 修复后通过：整笔拒绝、行数不变，释放后可重发 |
| 中途断流（已有正文） | `StreamDropTests.A_body_cut_after_some_text_keeps_the_text_fails_as_network_and_offers_retry`（`IOException`、`HttpIOException` 两例） | 通过（无缺陷）：保留“蒸汽机车最早”、失败／网络、可重试，重试另存新尝试，旧尝试可读 |
| 500／503／504 分类 | `ChatCompletionsClientTests.Http_errors_are_classified` 新增三行 | 通过：均为服务端出错 |
| 恢复备份中途失败 | `RestoreFailureTests.A_restore_failing_after_the_current_file_was_moved_puts_it_back_and_reports_an_io_error` | 修复后通过：报 I/O 错误，当前库原名原样（哈希不变）、无 `.restoring` 残留，重开为原内容 |
| 恢复时库被独占（Windows） | `RestoreFailureTests.A_restore_while_another_program_holds_the_database_open_…`、`…_blocked_on_the_wal_index_…` | 仅限 Windows，本机跳过，待 `scripts/windows.sh test` 或 `stress` |
| 写备份时杀进程 | `BackupKillTests.Killing_the_process_while_the_backup_is_written_leaves_no_listed_backup_and_the_next_start_recovers` | 修复后通过：半个备份（`*.db.partial`，有内容）不被列出、库仍是原版本与原数据；下次启动正常备份并迁移，残留被清掉 |
| 界面级存储暂停 | `StoragePauseUiTests`（Desktop，真实窗口＋真实盘满） | 修复后通过：横幅显示原因，发送／重试／总结禁用、分支按钮可见但禁用并提示原因，已有与未存的回答可读、可选中，草稿保留；放开后自动恢复、终态已写入、可继续提问 |
| 杀进程：创建分支 | `TransactionKillTests.Killing_the_process_while_a_branch_is_created_leaves_no_part_of_it` | 通过（无缺陷）：从分支再建分支，会话、来源行与两条祖先范围已写未提交（≥4 行变更）；重开后各表逐行与之前相同，同一 ID 再建成功、两条范围齐全 |
| 杀进程：采用候选版本 | `TransactionKillTests.Killing_the_process_while_a_candidate_is_adopted_keeps_the_old_current_version` | 通过（无缺陷）：节点仍指向原当前版本、候选仍在；重开后采用成功 |
| 杀进程：删除版本 | `TransactionKillTests.Killing_the_process_while_versions_are_deleted_keeps_every_version_and_its_read_state` | 通过（无缺陷）：删当前版本与第一版（节点改指第二版、两版及其阅读状态级联）未提交；重开后三版与三条阅读状态都在，再删成功，当前为第二版、阅读状态剩一条 |
| 杀进程：归档子树 | `TransactionKillTests.Killing_the_process_while_a_subtree_is_archived_leaves_it_visible_and_its_queued_titles_queued` | 通过（无缺陷）：归档行与子树内排队的标题、命名两条取消未提交；重开后无归档行、两条任务仍为排队（随后启动恢复按惯例标为中断），再归档成功 |
| 杀进程：深层恢复 | `TransactionKillTests.Killing_the_process_while_a_deep_restore_runs_keeps_every_archive_mark` | 通过（无缺陷）：恢复孙会话要清除路径上两个归档标记，未提交；重开后两标记仍在，再恢复成功、三者可见 |
| 杀进程：删除会话 | `TransactionKillTests.Killing_the_process_while_a_conversation_is_deleted_keeps_all_of_it` | 通过（无缺陷）：带重试链（`supersedes_id`）、任务、请求记录、草稿、阅读位置、最后位置、归档标记的分支，删除的各步（≥8 行变更）未提交；重开后逐行相同、最后位置仍指向它，再删成功且外键干净 |
| 杀进程：保存布局 | `TransactionKillTests.Killing_the_process_while_the_layout_is_saved_keeps_the_previous_layout` | 通过（无缺陷）：40 个节点（其中 10 个已有位置，既更新又插入）；子进程页缓存设为 1，未提交的页已溢出写进 WAL（断言 WAL 变长）后被杀；重开后仍是原 10 行原位置，再保存写入全部 40 个 |

- **发现并修复的缺陷**（均先写出失败的测试，再改代码确认由失败变通过）：
  1. **提问、重试、新建会话、建分支、总结任务行写失败直接抛出**，不进入存储暂停（测试中表现为 `SqliteException` 从服务抛出）；界面上这会成为发送命令的未处理异常（按命令库的默认行为推断，未单独验证界面表现）。现在写队列把 SQLite 的存储类错误（满、忙／锁、只读、I/O、损坏）转换为 Application 层的 `StorageWriteException(Kind)`，约束冲突等请求本身的错误仍原样抛出；会话与总结服务捕获它，返回“存储不可用”并调用协调器 `ReportStorageFailure`。
  2. **存储暂停会被任意一次成功的小写入解除**：原 `SaveAsync` 成功即清除标记（还不论是否仍有未补存的终态），盘满时小更新仍可成功，导致总结版本保存失败后立刻解除；而若只把提问写失败记为暂停，没有待补存项就没有重试，暂停将永不解除。现在暂停只由后台循环解除：每个间隔先补存保留的终态，再做**探测写**（`IConversationStore.ProbeWriteAsync`：取写锁、写入约 64 KB 后回滚，不留任何内容），两者都成功且期间没有新的失败才解除；状态变化另发 `StorageStateChanged`。
  3. **总结版本写失败不进入暂停**：失败终态（小更新）能写入，按旧逻辑反而清除了标记。现在版本写失败即标记暂停。
  4. **恢复备份中途失败会让当前库“消失”**：当前库已改名为 `replaced-*`、随后某一步（Windows 上被仍映射 `-shm` 的进程、杀毒软件等占用）失败时，原代码直接报错返回；下次启动找不到 `mindweave.db` 就会新建空库——正是需求 §9.3 禁止的“用空库冒充”。现在任何一步失败都把已移动的文件按相反顺序移回，删除 `.restoring`，再报 I/O 错误；移回本身失败记 Critical 日志并写明位置。
  5. **写备份时被杀留下的 `.partial` 永不清理**（时间戳不同，下次不会被覆盖）。它本来就不会被列为备份（`ListBackups` 的正则要求以 `.db` 结尾），现在每次写备份前先删除旧的 `mindweave-v*.db.partial*`。
- **反向核对**（逐项把产品改动临时撤回，确认对应用例由通过变失败，再恢复）：写队列不再转换为 `StorageWriteException` → 盘满三例（写队列、提问、新建会话）与忙两例（单次写入、提问）失败；恢复旧的“任意成功写入即解除” → 总结版本例失败；去掉总结版本失败时的暂停标记 → 同一例失败；恢复失败时不移回 → 跨平台恢复例失败；不清理旧 `.partial` → 杀进程例失败；发送、总结、重试、分支四个入口各自不看暂停状态 → 界面例分别在对应断言处失败。断流、500／503／504 与布局盘满三项没有产品改动，直接通过。
- **新增接口与配置**：`SqliteDatabaseOptions.BusyTimeout`（默认 5 秒，测试 200 ms）同时决定 SQLite 的 `busy_timeout` 与驱动按整秒的命令超时（Microsoft.Data.Sqlite 对忙会在 `busy_timeout` 之外再重试到命令超时，只改前者等不短）；`SqliteDatabase.ProbeWriteAsync`；`ConversationService.IsStorageBlocked`／`StorageStateChanged`；会话页 `IsStorageBlocked`／`StorageNotice`、`Composer.IsStoragePaused`、`MessageItemViewModel.IsBranchPaused`。测试宿主新增 `hang-in-backup`。
- **压力复测标记**：新的杀进程与锁用例（`BusyWriterTests`、`BackupKillTests`、`RestoreFailureTests`）标为 `Category=FaultInjection`，`stress` 工作流的默认过滤改为它（该文件在轨道分支变更时会自动跑一次 Windows）。本机每个这类用例单独连跑 5 次全部通过，界面两例连跑 5 次通过。等待都有上限（忙等待 15 秒内解除、子进程 60 秒内到达挂起点、Windows 上被杀进程的句柄最多等 5 秒释放）。
- **偏差与限制**：探测要求库与磁盘都能再写约 64 KB，剩余空间更少时即使小写入还能成功也保持暂停（盘几乎满时宁可暂停）；暂停期间最多比旧逻辑多等一个重试间隔（5 秒）才解除。新建会话页的输入框没有接入暂停状态（发送仍会被拒并显示原因）；会话改名、总结页的编辑／采纳等写失败仍未统一转为暂停（改名命令异常仍可能落到界面线程），留待后续。
- **未做（后续）**：~~归档、删除会话、创建分支、采用／删除版本、保存布局等多表事务中杀进程（等 B 块接口稳定）~~（已由下面的第二部分补上）；只读文件造成的 I/O 错误；Windows 上两项独占句柄用例与全部 `FaultInjection` 用例在 `stress`／实机上的重复运行。
- **审查后修正**：(1) **探测写原来摸不到磁盘**：WAL 模式下回滚的写入既不进库文件也不进日志（审查实测文件大小与修改时间都不变），所以真实磁盘满时探测总是成功，暂停约 5 秒后被误解除；只对模拟的页数上限有效。现在探测分两步：写事务内的大写入后回滚（发现锁、只读、损坏与页数上限），再在数据目录写一个同样大小的文件并刷到磁盘后删除（发现真实的磁盘满）。新用例在数据目录不可写时探测必须失败，关掉文件探测即失败。(2) 总结版本保存失败只在确属存储拒绝写入时暂停全部请求；其他错误（约束、程序缺陷）只让该任务失败，不再显示“本地数据无法保存”，用例用触发器拒绝插入。(3) 记录失败与解除暂停在同一把锁内判定，消除“正在宣布恢复时又失败”的竞态。(4) 忙等待用例上限放宽到 4.5 秒（仍明显短于默认 5 秒）；磁盘满与断流用例加入 `FaultInjection` 类别，压力工作流会重复它们。
- **合并 B 与 D 时补上**：归档、恢复、删除的写入遇到存储拒绝（盘满、锁、只读）时原先会把异常抛到界面线程。现在两处页面（会话页、归档页）都改为提示“本地数据暂时无法保存，没有做任何改动”，事务已整体回滚。用例用拒绝写入的归档存储逐项触发五个入口，去掉处理即失败。
- **第二部分：多表事务中杀进程**（矩阵中“杀进程：”七行，`Faults/TransactionKillTests`）。
  - **注入方式**：测试先经真实服务在临时数据目录造好数据并关库，再起子进程（测试宿主新命令 `hang-before-commit <数据目录> <操作> <ID…>`）用应用自己的存储执行一次写入。`SqliteDatabase` 新增仅供测试宿主的 `BeforeCommitForTests`（生产为 null，不包装写入）：在每个写事务的语句执行完、COMMIT 之前调用；宿主在其中报告本事务已做的行变更数（`total_changes()` 差值）与 WAL 增长量，然后挂住，测试在 60 秒内等到这一行后杀掉进程，并断言变更数达到该操作的最少语句数，证明确实停在事务中途而非开始前。
  - **断言**：重开走应用的打开路径（`SqliteDatabase.OpenAsync`，Windows 上被杀进程仍映射 `-shm` 时由其 3 秒重试兜住）：状态为 Ready，`integrity_check` 为 ok、`foreign_key_check` 为空，且**每张表的每一行**与杀进程前逐行相同（不只比行数）；再跑 `StartupCoordinator`（启动恢复）到 ReadyToWork，最后用同一存储重做该操作并检查其结果与完整性。
  - **反向核对**：临时让宿主在挂住前先提交事务，七例全部在“某表变了”处失败（如布局 10 行变 40 行、归档 0 行变 1 行），恢复后全部通过。本机七例各单独连跑 5 次全部通过。
  - **结论**：七种写入各自只用一个写事务，SQLite 的 WAL 回滚使被杀时未提交的内容（包括布局例中已溢出到 WAL 的页）在重开后完全不可见；未发现产品缺陷，产品代码只加了上述测试钩子。
  - **未覆盖的相邻窗口**：采用候选、删除版本、保存编辑后，服务层在**另一个**事务里让基础标题跟随新的当前版本（`SummaryService.CheckNameAsync` → `UpdateBaseTitleAsync`，再排命名任务）。在两次提交之间被杀，当前版本已换而基础标题／显示名仍是旧的，之后只有下一次当前版本变化才会再检查。这不是半个事务，但是一处“跟随”可能丢失；本次未构造该时序、未改代码，留给后续判断是否并入同一事务或在启动时补查。Windows 上这七例待 `stress` 工作流重复。
- **第二部分审查后修正**：原先只检查子进程在提交前“至少”改了多少行，审查实测把会话删除拆成两个事务时测试照样通过（停在第一个事务里，后面的从未提交）。现在七种写入都要求停住时的改动行数**恰好**等于整个操作的行数（分支 4、采用 1、删版本 5、归档 3、深层恢复 2、删会话 25、布局 40），拆成多个事务的第一段必然更少；把删会话临时拆成两段，测试失败（“4，应为 25”）。采用与深层恢复的改动很小，被杀时还在内存里、没有写进日志，这两项只算回归保护。采用之后的基础名更新是另一个事务（`CheckNameAsync`），二者之间被杀会让显示名暂时过时，已记为后续，不是半写。
