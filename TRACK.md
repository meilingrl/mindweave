# 激进轨 · track/claude-auto

Claude 自主开发，目的是比稳健轨更早走到关键阶段，提前暴露隐藏问题、积累经验。不是课程提交版本，但按产品级质量要求实现。起点 `baseline-v0.11`；分支约定见 [AGENTS.md](AGENTS.md)。

- 按[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)的 S0–S6 依赖关系推进，可以自行做实现决定，不必逐步等用户确认。
- 需要用户才能完成的事（API 密钥、Windows 实机、界面观感确认）集中提出，不因此停住其他工作。
- 宣称某阶段完成前，对照需求 §11 的相关场景说明哪些实际跑过、哪些没有验证。

共同推进与互审方式见[开发路线](docs/development-routes.md)。

## 协作约定（2026-10-25 用户同意）

- **本文件是进度的唯一入口**，对话记录不是。每次有意义的提交都同步更新“当前状态”与“接续点”。
- **阶段报告**：每个阶段结束时写明怎样体验（一条命令）、实际跑过哪些 §11 场景及方式、哪些未验证。
- **停下时的四种标签**：`检查点`（干净状态，说“继续”即可）、`需要你：X`、`待决定：Y`、`外部故障`（附原始错误与建议）。绕开阻塞继续做不依赖它的工作，全部被阻塞才停。
- **接续**：任何新会话只需读本文件“接续点”，不依赖对话历史。
- 不在本轨道修改 `docs/requirements.md`、`docs/architecture.md`；冲突记入“待决定”。轨道内的详细设计放在 [docs/design/](docs/design/)。

## 当前状态

- 阶段：**S1 真实聊天 · 实现完成，待真实服务确认**（2026-10-26）。S0 已完成。
- 测试：111 项通过（另 5 项为需显式开启的真实服务／系统凭据测试）——macOS 本机与 Windows 11 实机；CI 见 GitHub Actions。

阶段报告见 [docs/design/stage-reports.md](docs/design/stage-reports.md)。

## 接续点

- 下一步：S2 分支与返回 —— 引入 LiveMarkdown 渲染与来源适配层（选区锚点、校验、恢复）、迁移 v3（分支来源、祖先冻结范围、阅读位置、草稿）、创建分支事务、返回来源与普通切换、阅读位置与草稿保存。先读架构 §3.3、§5 与 `prototypes/livemarkdown-chat`。
- S1 收尾项（不阻塞 S2）：拿到你的真实服务配置后跑 `scripts/real-api.sh test`，把结果写进阶段报告。
- 第一条命令：`dotnet test Mindweave.slnx`。

## 需要你

| 事项 | 原因 | 阻塞什么 | 在此之前我做什么 |
| --- | --- | --- | --- |
| **真实 API 配置：未完成（2026-10-26）** | 终端里的 `scripts/real-api.sh configure` 停在 `Model ID:` 提示，钥匙串中还没有密钥。请在该终端继续输入模型 ID，再在钥匙串提示处粘贴密钥。之后我用 `scripts/real-api.sh test` 复用；需要验证多家服务时用 `configure <名字>` 各存一份 | S1 第一条验收（真实长回答） | 已继续 S2 |
| Windows 凭据管理器实机确认（约 1 分钟） | SSH 密钥登录的会话没有凭据存储（Win32 错误 1312），自动化无法验证；需要在笔记本桌面上的 PowerShell 里运行：`cd E:\mindweave-claude-auto\src-snapshot; $env:MINDWEAVE_CREDENTIAL_TESTS='1'; dotnet test tests\Mindweave.Infrastructure.Tests -c Release --filter FullyQualifiedName~PlatformCredentialStoreTests`（或之后在应用设置里保存一次密钥） | 仅 Windows 密钥保存的实机证据 | macOS 钥匙串已实测通过 |
| （可选）Windows 上的 Scoop git 损坏 | `E:\Scoop\shims\git.exe` 无法启动 `git.exe`；不影响本轨道（我用快照同步，不需要 git），未擅自修复 | 无 | — |

Windows 实机：`scripts/windows.sh test|publish|sync|exec`（主机 `win-home`，Tailscale，密钥登录；目录 `E:\mindweave-claude-auto`，与稳健轨隔离）。

已授权（2026-10-25）：推送 `origin/track/claude-auto`；CI 可用（仓库私有时只跑 Linux＋Windows，必要时你可改为公开仓库以免费使用分钟数）。

## 演进接缝 E1–E6（2026-10-26 用户决定本轨道采纳为实验）

依据 `main` 的 [演进方向与工程指引](docs/evolution.md)：S2 起引入前置接缝，不实现 D1–D6 功能本身；每个接缝记录实际成本与发现。

| 接缝 | 计划落点 | 状态 |
| --- | --- | --- |
| E1 视图槽位化导航状态 | S2：阅读位置、草稿、返回来源、内容栈归属视图槽位；首版一个槽位 | 计划中 |
| E2 分块消息内容 | S2 迁移 v3：消息正文改为有序内容块（首版只有文本块）；锚点为“块 ＋ 块内位置” | 计划中 |
| E3 请求追踪与源头脱敏 | S2：每次请求记录 ID、快照、时序、协议状态、脱敏错误摘要；密钥与用户内容用类型区分，日志拒绝原样输出 | 计划中 |
| E4 模型能力描述 | S2：模型配置声明能力，功能按能力判断 | 计划中 |
| E5 版本化交换格式 | S6 导出时落地；S2 起不让任何外部格式依赖表结构 | 计划中 |
| E6 性能基线与架构测试 | S2 起：大规模测试数据生成器与基准；脱敏规则加入架构测试 | 计划中 |

## 待决定

（暂无）

## 发现

及时记下值得稳健轨提前知道的问题：需求或架构的缺口、依赖库限制、平台差异、比预想难的地方。每条几行即可，写清现象和依据。

- **2026-10-25 · .NET 的 `Guid.CreateVersion7()` 在同一毫秒内不单调。** 同一毫秒创建的两个工作区按 ID 排序时顺序随机，导致重启后列表顺序偶发颠倒（测试间歇失败暴露）。依据：.NET 实现只保证毫秒级时间前缀，其余为随机位。处理：列表按 `created_at, rowid` 排序；不要把 UUIDv7 当作同毫秒内的插入顺序。
- **2026-10-25 · 同一对象以两个服务类型注册时，DI 容器会对它调用两次 `DisposeAsync`。** `SqliteDatabase` 同时注册为自身与 `IDatabaseLifecycle`，第二次释放访问已释放的信号量而抛异常。处理：释放与关闭做成幂等，并加了测试。
- **2026-10-25 · Avalonia 编译绑定可以绑定到方法组而不报错。** `IsVisible="{Binding GoBackCommand.CanExecute}"` 能编译，但绑定的是方法而非布尔值，按钮永远可见；只有真实控件测试能发现。处理：暴露布尔属性 `CanGoBack`。
- **2026-10-25 · Windows 首次 CI 暴露 macOS 掩盖的句柄泄漏。** 打开非数据库文件时 `Open()` 成功、第一条 PRAGMA 才失败，连接未释放；macOS 不强制文件锁所以测试通过，Windows 上文件一直被占用。处理：打开与配置失败时立即释放，测试加“之后能移走文件”断言。教训：涉及文件句柄的测试必须在 Windows 上跑。
- **2026-10-25 · Windows 上进程被杀后文件锁不会立即可用。** 进程已报告退出，`FileShare.None` 的锁仍短暂占用。处理：第二个实例在“已有实例无响应”时再尝试占用一次（崩溃后马上重开的真实场景）；测试改为限时重试。
- **2026-10-26 · 经 SSH 密钥登录的 Windows 会话无法使用凭据管理器**（`CredWrite/CredRead` 返回 1312 `ERROR_NO_SUCH_LOGON_SESSION`）：没有密码登录就没有可用的凭据保护密钥。产品按设计报“凭据服务不可用”而不降级明文；但自动化测试无法在 SSH 下验证 Windows 凭据，只能在桌面会话中运行。
- **2026-10-26 · 回答“已终态”与“生成槽释放”之间有短暂窗口。** 终态先写库，槽稍后释放；此时立即重试会被误判为“回答进行中”（测试 1/5 偶发暴露）。处理：判断是否进行中改看实时回答状态而非槽占用；25 次连续运行无失败。
- **2026-10-26 · 两台机器 SDK 特性带不同**（macOS 10.0.402，Windows 10.0.103），原 `global.json` 固定 10.0.402 + `latestPatch` 会让 Windows 无法构建。处理：改为 10.0.100 + `latestFeature`（锁定 .NET 10 主版本，不锁特性带）；CI 用最新 10.0.x。
- **2026-10-26 · 从 macOS 打包源码给 Windows 时，`tar` 会夹带 `._*` AppleDouble 文件**，被 MSBuild 当作 `.cs` 编译而报“二进制文件”。处理：`COPYFILE_DISABLE=1` 与 `--no-mac-metadata`。
- **2026-10-25 · 迁移整体单事务时，`foreign_keys` 必须在事务外关闭、提交后再开。** SQLite 在事务内忽略对 `foreign_keys` 的修改；按官方流程在事务内用 `PRAGMA foreign_key_check` 兜底，违例回滚（已有测试）。
