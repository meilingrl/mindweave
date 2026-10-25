# S0 骨架与存储

验收依据（交接 §5）：课程三层导航路径走通；临时库迁移失败原库不变；第二次启动回到已有窗口。

## 工程

| 工程 | 内容 | 允许的依赖 |
| --- | --- | --- |
| `Mindweave.Core` | 实体与规则（`WorkspaceId`、`Workspace`、名称规范化） | 无 |
| `Mindweave.Application` | 用例与契约：启动协调、工作区服务、存储契约、`IClock` | Core、`Microsoft.Extensions.Logging.Abstractions` |
| `Mindweave.Infrastructure` | SQLite、迁移、备份、恢复、单实例、数据目录、文件日志 | Application、Core、`Microsoft.Data.Sqlite` |
| `Mindweave.Desktop` | Avalonia 视图、ViewModel、导航服务、ViewLocator、组装 | 全部 |

`tests/Mindweave.Architecture.Tests` 用程序集引用断言上述方向，Core／Application 不得引用 Avalonia、SQLite 或供应商 SDK。

## 数据目录与轨道隔离

- 目录：macOS `~/Library/Application Support/Mindweave/<channel>/`；Windows `%LOCALAPPDATA%\Mindweave\<channel>\`；Linux `$XDG_DATA_HOME/Mindweave/<channel>/`。
- `<channel>` 由构建属性 `MindweaveChannel` 写入程序集元数据，本轨道为 `claude-auto`。单实例标识、凭据命名空间（S1）均派生自同一值，因此两条轨道的正式程序不会共享数据或互相激活。
- `MINDWEAVE_DATA_DIR` 可整体覆盖目录，用于测试与手动试用；覆盖后单实例标识按目录派生，仍互不干扰。
- 目录内容：`mindweave.db`（及 WAL）、`instance.lock`、`backups/`、`logs/`。

## 单实例

- 占用：对 `instance.lock` 以 `FileShare.None` 打开并持有到退出。锁是按数据目录的，正好对应“同一数据库只有一个写入实例”；进程被杀时操作系统释放锁，无需清理陈旧文件。
- 激活：持有者在命名管道 `mindweave-<目录哈希>` 上监听；第二个进程占用失败后连接管道发送激活请求并退出。持有者刚启动、管道尚未监听时，第二个进程在 5 秒内重试连接。
- 持有者无响应（锁被占但管道连不上）：第二个进程不打开数据库，提示“已有实例无响应”后退出，不出现第二个写入者。

## SQLite

- 驱动 `Microsoft.Data.Sqlite`（自带 e_sqlite3），不用 EF。
- `PRAGMA application_id = 0x4D575631`（“MWV1”）识别本应用的库；`user_version` 为结构版本，独立于软件版本。
- 新库创建时设 `journal_mode=WAL`（持久化在文件头）；每个连接设 `foreign_keys=ON`、`synchronous=FULL`、`busy_timeout=5000`。核心内容优先于写入吞吐；本应用写入频率低。
- 连接模型：一个写连接由 `SqliteWriteQueue` 独占，所有写入经单消费者通道串行执行，各自在一个事务内；读取用短生命周期只读连接（`Mode=ReadOnly`），WAL 下不阻塞写入。检查点合并（同一回答的旧检查点被新检查点取代）在 S1 加到队列上。

### 启动分类

取得单实例占用后、进入工作台与启动 AI 之前：

| 情况 | 结果 |
| --- | --- |
| 文件不存在 | 在临时文件建库并整体改名为 `mindweave.db`，不留半成品 |
| `application_id` 不符且非空库 | 拒绝：不是 Mindweave 数据，不写入 |
| `quick_check` 失败 | 拒绝：数据库损坏，提供恢复页 |
| `user_version` 高于程序 | 拒绝写入：数据来自更新版本 |
| 低于程序 | 备份 → 迁移 |
| 相等 | 打开 |

### 备份与迁移

- 备份：`VACUUM INTO` 写入 `backups/` 下的临时名，再以只读方式打开校验 `quick_check`、`application_id`、`user_version`，通过后改为正式名 `mindweave-v{版本}-{UTC 时间}.db`。任何一步失败即不迁移。保留最近 5 份迁移备份。
- 迁移：全部待执行迁移在**一个**事务中完成。按 SQLite 推荐流程：事务外关闭 `foreign_keys`，事务内执行各步与 `PRAGMA foreign_key_check`，有违例则回滚；成功后连同 `user_version` 一起提交，再开启 `foreign_keys`。因此任一步失败或进程被杀，库仍停在原版本。
- 失败不自动恢复、不建空库。Root 显示恢复页：错误说明、重试、打开数据位置、退出；存在备份时可明确选择恢复。
- 恢复：关闭连接，把当前库（及 WAL／SHM）改名保留为 `mindweave.db.replaced-{UTC 时间}`，复制备份为新库，再走一遍启动分类。

## 导航

- **Root**：`RootViewModel.Current` 在启动中、恢复页、首次引导（新建工作区）、工作台之间切换；`IRootNavigator`。
- **Menu**：工作台主区——探索、总结网络、设置；`IMenuNavigator`。
- **Content**：主区内的详情推栈与后退；`IContentNavigator`。
- 统一请求 `NavigationRequest(目标主区, 目标内容, 意图)`，内容以 `ContentRoute`（类型＋稳定 ID）表示，不传控件或 ViewModel。
- 每个工作区一份运行期状态 `WorkspaceRuntime`：当前主区、各主区内容栈。由工作台 ViewModel 持有，切换主区或工作区不丢失；只持久化最后有效页面（工作区与主区，S1 起含会话）。
- ViewLocator 使用显式注册表（ViewModel 类型 → View 工厂），不按类名反射，便于裁剪发布；测试断言每个页面 ViewModel 都有注册的视图。

### 跨主区后退（S0 范围）

S0 的“后退”只在当前主区自己的内容栈内生效，主区主页不会被弹出；切换主区或工作区后，各自的栈原样保留。跨主区跳转（网络 → 总结 → 来源会话）在 S4／S5 出现，届时按架构 MW-C10 补充结果表，并以 `NavigationIntent` 扩展意图。

## 验证方式

| 场景 | 方式 |
| --- | --- |
| 新库创建、重开、升级前备份、迁移失败整体回滚、外键违例回滚、备份失败不迁移、新版本拒写、他人库与损坏文件不改动、空文件初始化、恢复保留被替换文件、备份保留 5 份 | `Mindweave.Infrastructure.Tests`，临时目录真实 SQLite |
| 迁移进行中被强制终止 → 仍为旧版本；已提交写入在强制终止后保留 | 子进程 `Mindweave.TestHost` + `Kill` |
| 单实例：同目录互斥、激活请求、持有者无响应、跨进程持有与杀进程后释放 | 同上 |
| Root → Menu → Content、后退、工作区切换保留各自状态、重启只恢复最后页面 | `Mindweave.Desktop.Tests`，无界面 Avalonia + 真实组装 |
| 视图注册完整、真实控件输入与点击 | 同上，含渲染截图（设 `MINDWEAVE_SCREENSHOT_DIR`） |
| 依赖方向 | `Mindweave.Architecture.Tests` |
