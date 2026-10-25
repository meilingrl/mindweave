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

- 阶段：**S1 真实聊天 · 进行中**（S0 已完成，2026-10-25）。S1 后端（数据、生成协调、协议适配、凭据）已完成并有测试；界面进行中。
- 测试：49 项全部通过——macOS 本机、CI Linux 与 Windows 均为绿（首次 CI 的 Windows 运行暴露 2 个缺陷，已修复，见“发现”）。

### S0 阶段报告

**怎样体验**（独立数据目录，不碰默认位置）：

```sh
MINDWEAVE_DATA_DIR=/tmp/mw-try dotnet run --project src/Mindweave.Desktop
```

首次启动引导新建工作区 → 工作台（探索／总结网络／设置，设置 → 数据与存储 可推入详情并后退）。侧栏可新建、切换工作区。再启动一次会回到已有窗口。

**实际验证了什么**（交接 §5 S0 验收三条均有自动化测试）：

| 验收点 | 方式 | 结果 |
| --- | --- | --- |
| 课程三层导航路径 Root→Menu→Content、后退 | 无界面 Avalonia + 真实组装，含真实控件输入／点击与渲染截图 | 通过 |
| 临时库迁移失败原库不变 | 失败步骤、外键违例、备份失败、迁移中杀进程 4 种注入 | 通过，原库版本与内容不变，备份存在 |
| 第二次启动回到已有窗口 | 同进程与跨进程测试；macOS 上真实启动两次，第二个进程退出码 0、只剩一个实例 | 通过 |
| 新版本库拒写、他人库／损坏文件不改动、恢复保留被替换文件 | 临时库 | 通过 |
| 已提交写入在强制终止后保留 | 子进程杀死后重开 | 通过 |
| 依赖方向（Core／Application 不引用 UI、SQLite） | 程序集引用检查 + Desktop 源码只在组装处引用 Infrastructure | 通过 |

**用户试用（2026-10-26，macOS）**：重复启动不产生第二个实例、自动回到前台；界面“很粗糙但可用”——按路线先不做视觉打磨，记为后续待办。

**Windows 11 实机（2026-10-26，ROG Zephyrus G16，经 Tailscale SSH）**：全部 49 项测试通过；已发布自包含 `E:\mindweave-claude-auto\app\Mindweave.exe` 供手动试用。

**未验证**：Windows 上的手动交互（窗口激活、中文输入）——需要你在该机器上打开上面的 exe；退出流程中“进行中任务／未保存布局”提示要到 S1／S5 才有内容可测。课程计数口径（MW-10）未核对。

## 接续点

- 正在做：S1 界面 —— 侧栏会话列表、会话页（消息、发送／停止、重试／重新生成、改名）、设置页模型连接（地址、密钥、获取模型、测试连接）、退出时“进行中任务”提示；然后 `scripts/real-api.sh`。
- 已完成的 S1 部分见 [docs/design/s1-chat.md](docs/design/s1-chat.md)；第一条命令：`dotnet test Mindweave.slnx`。

## 需要你

| 事项 | 原因 | 阻塞什么 | 在此之前我做什么 |
| --- | --- | --- | --- |
| 真实 API 配置 | 2026-10-25 约定：我写配置脚本（`scripts/` 下），你填入服务地址、模型与密钥（存系统凭据服务，按轨道命名空间），之后长期复用；需要验证多家时我提前列出 | S1 真实服务场景 | 先用模拟流完成 S1 其余部分 |
| Windows 凭据管理器实机确认（约 1 分钟） | SSH 密钥登录的会话没有凭据存储（Win32 错误 1312），自动化无法验证；需要在笔记本桌面上的 PowerShell 里运行：`cd E:\mindweave-claude-auto\src-snapshot; $env:MINDWEAVE_CREDENTIAL_TESTS='1'; dotnet test tests\Mindweave.Infrastructure.Tests -c Release --filter FullyQualifiedName~PlatformCredentialStoreTests`（或之后在应用设置里保存一次密钥） | 仅 Windows 密钥保存的实机证据 | macOS 钥匙串已实测通过 |
| （可选）Windows 上的 Scoop git 损坏 | `E:\Scoop\shims\git.exe` 无法启动 `git.exe`；不影响本轨道（我用快照同步，不需要 git），未擅自修复 | 无 | — |

Windows 实机：`scripts/windows.sh test|publish|sync|exec`（主机 `win-home`，Tailscale，密钥登录；目录 `E:\mindweave-claude-auto`，与稳健轨隔离）。

已授权（2026-10-25）：推送 `origin/track/claude-auto`；CI 可用（仓库私有时只跑 Linux＋Windows，必要时你可改为公开仓库以免费使用分钟数）。

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
