# 状态

当前状态、下一步、需要用户的事和待决定事项。保持简短：阶段经过写进[阶段报告](docs/design/stage-reports.md)，决定写进 [decisions.md](docs/design/decisions.md)。2026-11-16 之前的完整记录在 [docs/history/track-claude-auto.md](docs/history/track-claude-auto.md)。

## 当前状态（2026-11-17）

- **开发线合并为一条。** Claude 轨道并入 `main`，Codex 轨道停在标签 `archive/codex-steady-final`；两条轨道分支与工作目录已删除（本地与 GitHub），只剩 `main`。Codex 工作目录里不在 git 中的过程记录（`.superpowers/`，802 MB）移到 `../mindweave-archive/codex-steady-superpowers`，构建产物按你的决定删除；约定见 [AGENTS.md](AGENTS.md)。课程技术要求经用户 2026-11-16 认定已满足（三层导航按功能、ViewModel 按依赖边界判断，不要求与参照项目同名或同工程），本版本作为课程提交版本。
- **功能。** 首版 S0–S7、需求 v0.13 的首版后增量，以及 2026-11-17 合并的一批：方向 1 的阅读从新内容第一句开始、输入框旁的上下文圆环、使用统计的每日用量日历、从推荐开始的会话总结后自动建立“来自推荐”的内容关联（需求 v0.14，迁移 v14）、首次引导（需求 v0.15）；方向 2 的模型能力（设置 › 模型连接 › 高级设置）、资源存储（迁移 v15）与智能体平台的需求 v0.16 §10.4、架构 §15（待你决定的问题见 [agent-platform-questions.md](docs/design/agent-platform-questions.md)）；随后合并图标方向 A（圆角线条，图标由 `Themes/IconStrokes.xml` 经 `scripts/icons/outline-icons.cs` 生成，圆角统一取自 `Themes/Radii.axaml`）与智能体平台第 1 期“资源与附件”（迁移 v16：输入框“+”、长粘贴成为附件、PDF／DOCX／图片、按上下文容量发送全文或摘要加相关部分，见 [attachments.md](docs/design/attachments.md)）。第三方声明现附 Apache-2.0 与 MIT 的标准全文。课程提交版本是标签 `course-submission`。数据库 v16。
- **发布与通道。** Release 构建默认用通道 `release`，界面不显示通道名；其余构建用 `dev`；日常版本仍用 `claude-auto`。源码包只含产品，并检查不出现开发线名称。见 [运行说明](docs/design/run-guide.md) 与 AGENTS.md“发布与通道”。
- **测试。** macOS 全量通过（Core 115、Architecture 9、Infrastructure 606＋22 跳过、Desktop 四个分片 558 通过、82 跳过；2026-11-17）。以下 CI 与 Windows 结果仍是 2026-11-16 的。`main` 上的 CI（Linux＋Windows）与 stress 通过（08ead83）。Windows 11 实机（08ead83，2026-11-16）：全量 1,126 通过、0 失败、97 跳过；Release 发布版本首次启动正常，设置页底部只显示“Mindweave 0.1.0”，数据在 `%LOCALAPPDATA%\Mindweave\release`，正常关闭。

## 下一步

方向 2 第 2 期（运行循环与工具调用，先做“检索附件”和“检索本工作区总结网络”两个本地工具），开始前看 [agent-platform-questions.md](docs/design/agent-platform-questions.md) 的 Q8–Q11；第 1 期在真实窗口与 Windows 上试用（文件选择框、从 Finder 拖入、从别的应用粘贴尚未手动试过）。其他候选：Windows 11 实机全量与发布版本试用（课程演示平台）；课程报告材料；[低／信息级待办](docs/reviews/claude-auto-health-20261112/low-triage.md)；中英双语准备；“显示思考内容”。

## 需要你

| 事项 | 原因 | 阻塞什么 |
| --- | --- | --- |
| 在真实窗口里看一眼图标大小（方向 A 比原来显得小，与样例一致；要大一点只需改样式里的尺寸） | 界面观感只能由你判断 | 不阻塞 |
| 日常版本升到 v16 前先 `scripts/daily.sh trial`，再 `publish --accept-upgrade` | 会升级你的真实数据 | 日常版本更新 |
| 真实服务检查（付费，可选）：DeepSeek 图片＋工具、Kimi K3 base64 图片、GPT-6 工具调用、DashScope 流式工具 | 只在你给的额度内 | 第 1–2 期的能力预设 |
| 把程序坞上旧的 Mindweave 图标移除，改钉 `~/Applications/Mindweave.app`（一次） | 旧图标被固定在 11-13 的 v11 版本，打不开已是 v13 的数据；新的应用位置不变，以后更新不用再钉 | 从程序坞打开日常版本 |

## 待决定

- （已修 2026-11-16）上下文压缩可能丢掉附带对象与条件的禁令（Codex 轨道的真实 DeepSeek 记录）：压缩提示词要求每条要求或禁止连同对象与条件一起写、未决的决定算未解决问题；真实服务测试加了限定对象的禁令与未决事项，窗口从 7,500 提到 20,000（过小的窗口只给摘要 2,048 输出，推理模型会被截断）；DeepSeek 连续 3 次通过。
- **未解决问题与待办的明细**仍在历史记录的“未解决问题”与“待决定”两节；分拣表 [low-triage.md](docs/reviews/claude-auto-health-20261112/low-triage.md) 是待办的唯一清单，修了一条就在它的状态格写提交。
- 回答的输出上限能否在设置里改（2026-11-13）。
- 统计图表“输出”颜色对比度低于 3:1：暂缓，不阻塞（你 2026-11-16）。
- 已定（你 2026-11-16）：删除会话或工作区时用量随之消失，保持现状；压缩时超过窗口的单条长消息不再单独处理，由长文本／文件导入设计取代（已写入需求 v0.16 §10.4）；窗口约 8k 的本地模型不支持。
- 删除前的整库备份从不自动清理；仓库里有个人路径与邮箱，公开仓库前须清理（健康检查 storage-7、repo-scripts-9）。

## 运行时资源

- 本机：日常数据 `~/Library/Application Support/Mindweave/claude-auto`，只经 `scripts/daily.sh`；日常版本从 `~/Applications/Mindweave.app` 打开（`publish`／`rollback` 只换其中内容，程序坞图标不失效）；见 [日常版本](docs/design/daily-build.md)。任务工作树运行应用时设 `MINDWEAVE_DATA_DIR`。
- Windows 实机：`scripts/windows.sh test|publish|sync|exec`（主机 `win-home`，经本机用户态 Tailscale 实例的 SOCKS `127.0.0.1:7898`，密钥登录）；目录 `E:\mindweave-claude-auto`（历史名称，可用 `MINDWEAVE_WIN_DIR` 另指）。`publish` 默认用通道 `dev`（`MINDWEAVE_WIN_CHANNEL=release` 构建演示用的发布版本）；2026-11-16 前的试用数据在通道 `claude-auto-dev`，要接着用就设 `MINDWEAVE_WIN_CHANNEL=claude-auto-dev`。
- 与 Windows 上的 Claude 用信箱传递任务与报告：`E:\mindweave-mailbox\`，协议见 [docs/mailbox.md](docs/mailbox.md)。
- 已授权：推送 `origin`；CI 可用（私有仓库只跑 Linux＋Windows）。

第一条命令：`scripts/test.sh full`。
