# 状态

当前状态、下一步、需要用户的事和待决定事项。保持简短：阶段经过写进[阶段报告](docs/design/stage-reports.md)，决定写进 [decisions.md](docs/design/decisions.md)。2026-11-16 之前的完整记录在 [docs/history/track-claude-auto.md](docs/history/track-claude-auto.md)。

## 当前状态（2026-11-20）

- **开发线合并为一条。** Claude 轨道并入 `main`，Codex 轨道停在标签 `archive/codex-steady-final`；两条轨道分支与工作目录已删除（本地与 GitHub），只剩 `main`。Codex 工作目录里不在 git 中的过程记录（`.superpowers/`，802 MB）移到 `../mindweave-archive/codex-steady-superpowers`，构建产物按你的决定删除；约定见 [AGENTS.md](AGENTS.md)。课程技术要求经用户 2026-11-16 认定已满足（三层导航按功能、ViewModel 按依赖边界判断，不要求与参照项目同名或同工程），本版本作为课程提交版本。
- **功能。** 首版 S0–S7、需求 v0.13 的首版后增量，以及 2026-11-17 合并的一批：方向 1 的阅读从新内容第一句开始、输入框旁的上下文圆环、使用统计的每日用量日历、从推荐开始的会话总结后自动建立“来自推荐”的内容关联（需求 v0.14，迁移 v14）、首次引导（需求 v0.15）；方向 2 的模型能力（设置 › 模型连接 › 高级设置）、资源存储（迁移 v15）与智能体平台的需求 v0.16 §10.4、架构 §15（待你决定的问题见 [agent-platform-questions.md](docs/design/agent-platform-questions.md)）；随后合并图标方向 A（圆角线条，图标由 `Themes/IconStrokes.xml` 经 `scripts/icons/outline-icons.cs` 生成，圆角统一取自 `Themes/Radii.axaml`）与智能体平台第 1 期“资源与附件”（迁移 v16：输入框“+”、长粘贴成为附件、PDF／DOCX／图片、按上下文容量发送全文或摘要加相关部分，见 [attachments.md](docs/design/attachments.md)）。第三方声明现附 Apache-2.0 与 MIT 的标准全文。2026-11-17 又合并第 2 期“运行与工具调用”（迁移 v17、需求 v0.17 §10.4.4，决定 R1–R10 见 [runs-and-tools.md](docs/design/runs-and-tools.md)）：模型可调用“检索附件”“检索总结网络”两个本地工具，回答上方有可展开的活动说明，设置 › 工具可关闭工具并调整上限。随后合并第 3 期“网络搜索与取网页”（需求 v0.18 §10.4.5，无迁移，决定 W1–W16 见 [web-search.md](docs/design/web-search.md)）：Tavily 搜索、读取网页（拦截本机与私有地址；2026-11-19 修订 W8：有系统代理时经代理读取，代理的 fake-IP 应答 198.18.x.x 不再算本地，已在本机 Clash Verge 下实测 Wikipedia、arXiv、GitHub、政府网、新浪可读，百度百科对程序访问返回安全验证页）、编号出处与出处列表、模型自造网址须批准，输入框旁的地球图标切换 自动／开／关。2026-11-19 合并第 4 期“第三方工具（MCP）”（需求 v0.19 §10.4.6，无迁移，决定 M1–M18 与 Q15 你已接受，见 [mcp.md](docs/design/mcp.md)）：设置 › 工具 › MCP 服务器添加本地命令或网址，工具逐个启用，有副作用的调用每次批准，服务器随应用退出而结束（你在 macOS 上用 npx 服务器试过）。同日合并“生成中的指示”（无迁移）：回答与总结生成时，进度条与静止的三个点换成三点“编织”动作，等待编辫、思考连结、压缩收紧、正文流入时为引线，阶段之间滑动过渡，见 [generating-indicator.md](docs/design/generating-indicator.md)；你看过动图与截图后同意合并，尚未在真实模型下试用。2026-11-20 合并运行循环的改进 L1–L11（需求 v0.20，无迁移，见 [agent-loop.md](docs/design/agent-loop.md) 第 8 节）：运行说明与额度行、偶然错误重试一次、中间轮被截断时直接作答、为运行留 25% 上下文、只读调用并发、重复调用不再执行、按引用情况淘汰、“开”档检索词带上下文、较早材料的索引、附件与总结也编号进出处；四步的真实评测见 [loop-steps.md](docs/reviews/run-eval-20261119/loop-steps.md)，你决定不再单独审阅，在日常使用中验证。课程提交版本是标签 `course-submission`。数据库 v17。
- **发布与通道。** Release 构建默认用通道 `release`，界面不显示通道名；其余构建用 `dev`；日常版本仍用 `claude-auto`。源码包只含产品，并检查不出现开发线名称。见 [运行说明](docs/design/run-guide.md) 与 AGENTS.md“发布与通道”。
- **日常版本。** 2026-11-17 发布 c806fbd（schema v16），你的数据在下次启动时从 v13 升级；发布前备份在 `~/Documents/Mindweave-daily-backups/`。
- **测试。** macOS 全量通过（Core 115、Architecture 10、Infrastructure 941＋23 跳过、Desktop 四个分片 606 通过、82 跳过；2026-11-20）。`main` 上的 CI（Linux＋Windows）自 2026-11-17 起一直失败，2026-11-20 修复后重新通过（a2ebf62）：Windows 上的 MCP 路径与进程、仍打开的数据库文件、中文回退字体使回答变短，以及慢机器上的时序；以后合并须等 CI 通过才算完成。Windows 11 实机 2026-11-20 不在线，未跑全量。stress 仍是 08ead83 的结果。Windows 11 实机（08ead83，2026-11-16）：全量 1,126 通过、0 失败、97 跳过；Release 发布版本首次启动正常，设置页底部只显示“Mindweave 0.1.0”，数据在 `%LOCALAPPDATA%\Mindweave\release`，正常关闭。

## 下一步

第 2–4 期与运行循环的改进（L1–L11）在日常使用中验证；以后可另开任务继续优化智能体，起点是 [agent-loop.md](docs/design/agent-loop.md) 第 8 节的待办（附件摘要在推理模型上被截断、随提问发送的附件的出处写法）与 `scripts/real-api.sh eval`。第 1 期已由你在 macOS 试用版本（367bcca）上逐项试过并通过（2026-11-17：“+”菜单、文件与图片、长粘贴与撤回、拖入、从 Finder 粘贴、发送与查看页）；Windows 上尚未试用。其他候选：Windows 11 实机全量与发布版本试用（课程演示平台）；课程报告材料；[低／信息级待办](docs/reviews/claude-auto-health-20261112/low-triage.md)；产品双语（待讨论）；“显示思考内容”。

**文档改用英文（2026-11-20 你批准的语言方案，规则见 AGENTS.md“Language and writing”）。** 已完成：AGENTS.md、README 与 17 份现行设计文档。待 `task/reasoning-and-status` 合并后再译：需求、架构、`runs-and-tools.md`、`generating-indicator.md`、`reasoning-and-status.md` 与本文件；译完之前这些文件仍用中文改。

## 需要你

| 事项 | 原因 | 阻塞什么 |
| --- | --- | --- |
| 在真实窗口里看一眼图标大小（方向 A 比原来显得小，与样例一致；要大一点只需改样式里的尺寸） | 界面观感只能由你判断 | 不阻塞 |
| 真实服务检查（付费，可选）：DeepSeek 图片＋工具、Kimi K3 base64 图片、GPT-6 工具调用、DashScope 流式工具 | 只在你给的额度内 | 第 1–2 期的能力预设 |
| 把程序坞上旧的 Mindweave 图标移除，改钉 `~/Applications/Mindweave.app`（一次） | 旧图标被固定在 11-13 的 v11 版本，打不开已是 v13 的数据；新的应用位置不变，以后更新不用再钉 | 从程序坞打开日常版本 |

## 待决定

- （已修 2026-11-16）上下文压缩可能丢掉附带对象与条件的禁令（Codex 轨道的真实 DeepSeek 记录）：压缩提示词要求每条要求或禁止连同对象与条件一起写、未决的决定算未解决问题；真实服务测试加了限定对象的禁令与未决事项，窗口从 7,500 提到 20,000（过小的窗口只给摘要 2,048 输出，推理模型会被截断）；DeepSeek 连续 3 次通过。
- **未解决问题与待办的明细**仍在历史记录的“未解决问题”与“待决定”两节；分拣表 [low-triage.md](docs/reviews/claude-auto-health-20261112/low-triage.md) 是待办的唯一清单，修了一条就在它的状态格写提交。
- 回答的输出上限能否在设置里改（2026-11-13）。
- 统计图表“输出”颜色对比度低于 3:1：暂缓，不阻塞（你 2026-11-16）。
- 已定（你 2026-11-16）：删除会话或工作区时用量随之消失，保持现状；压缩时超过窗口的单条长消息不再单独处理，由长文本／文件导入设计取代（已写入需求 v0.16 §10.4）；窗口约 8k 的本地模型不支持。
- 删除前的整库备份从不自动清理（健康检查 storage-7）。仓库现有文件里的个人路径与中转地址已清理（2026-11-20）；旧提交的作者邮箱保留，不改写历史，以后用 noreply 邮箱（D-025）。

## 运行时资源

- 本机：日常数据 `~/Library/Application Support/Mindweave/claude-auto`，只经 `scripts/daily.sh`；日常版本从 `~/Applications/Mindweave.app` 打开（`publish`／`rollback` 只换其中内容，程序坞图标不失效）；见 [日常版本](docs/design/daily-build.md)。任务工作树运行应用时设 `MINDWEAVE_DATA_DIR`。
- Windows 实机：`scripts/windows.sh test|publish|sync|exec`（主机 `win-home`，经本机用户态 Tailscale 实例的 SOCKS `127.0.0.1:7898`，密钥登录）；目录 `E:\mindweave-claude-auto`（历史名称，可用 `MINDWEAVE_WIN_DIR` 另指）。`publish` 默认用通道 `dev`（`MINDWEAVE_WIN_CHANNEL=release` 构建演示用的发布版本）；2026-11-16 前的试用数据在通道 `claude-auto-dev`，要接着用就设 `MINDWEAVE_WIN_CHANNEL=claude-auto-dev`。
- 与 Windows 上的 Claude 用信箱传递任务与报告：`E:\mindweave-mailbox\`，协议见 [docs/mailbox.md](docs/mailbox.md)。
- 已授权：推送 `origin`；CI 可用（私有仓库只跑 Linux＋Windows）。

第一条命令：`scripts/test.sh full`。
