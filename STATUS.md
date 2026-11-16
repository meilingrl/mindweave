# 状态

当前状态、下一步、需要用户的事和待决定事项。保持简短：阶段经过写进[阶段报告](docs/design/stage-reports.md)，决定写进 [decisions.md](docs/design/decisions.md)。2026-11-16 之前的完整记录在 [docs/history/track-claude-auto.md](docs/history/track-claude-auto.md)。

## 当前状态（2026-11-16）

- **开发线合并为一条。** Claude 轨道并入 `main`，Codex 轨道停在标签 `archive/codex-steady-final`；两条轨道分支与工作目录已删除（本地与 GitHub），只剩 `main`。Codex 工作目录里不在 git 中的过程记录（`.superpowers/`，802 MB）移到 `../mindweave-archive/codex-steady-superpowers`，构建产物按你的决定删除；约定见 [AGENTS.md](AGENTS.md)。课程技术要求经用户 2026-11-16 认定已满足（三层导航按功能、ViewModel 按依赖边界判断，不要求与参照项目同名或同工程），本版本作为课程提交版本。
- **功能。** 首版 S0–S7 与需求 v0.13 的首版后增量（推荐问题与推荐节点、用户建立与 AI 建议的内容关联）已合并。数据库 v13。
- **发布与通道。** Release 构建默认用通道 `release`，界面不显示通道名；其余构建用 `dev`；日常版本仍用 `claude-auto`。源码包只含产品，并检查不出现开发线名称。见 [运行说明](docs/design/run-guide.md) 与 AGENTS.md“发布与通道”。
- **测试。** macOS 全量通过（Core 99、Architecture 9、Infrastructure 549＋22 跳过、Desktop 四个分片 468 通过、76 跳过；2026-11-16）。`main` 上的 CI（Linux＋Windows）与 stress 通过（08ead83）。Windows 实机全量与发布版本检查已经信箱发出（2026-11-16，08ead83），结果待收。

## 下一步

用户在日常使用中，之后按反馈定下一阶段。候选：Windows 11 实机全量与发布版本试用（课程演示平台）；课程报告材料；[低／信息级待办](docs/reviews/claude-auto-health-20261112/low-triage.md)；中英双语准备；“显示思考内容”。

## 需要你

| 事项 | 原因 | 阻塞什么 |
| --- | --- | --- |
| 看第 7、8 项（推荐、内容关联）的截图或实机 | 界面观感只能由你判断 | 不阻塞 |
| 日常版本升到 v13 前先 `scripts/daily.sh trial`，再 `publish --accept-upgrade` | 会升级你的真实数据 | 日常版本更新 |

## 待决定

- **上下文压缩会丢掉附带对象与条件的禁令**（2026-11-16，Codex 轨道的真实 DeepSeek 记录：摘要保留了“它是备用电池”，漏了“不得整夜充电”，最终回答也漏了）。本仓库的压缩提示词 `ContextCompressor.cs:57-65` 没有要求把要求或禁令与其对象、条件一起保留，真实服务测试也没有覆盖。修法：提示词加一条，真实测试加一条限定对象的禁令与一个未决事项；验证需几次付费调用。
- **未解决问题与待办的明细**仍在历史记录的“未解决问题”与“待决定”两节；分拣表 [low-triage.md](docs/reviews/claude-auto-health-20261112/low-triage.md) 是待办的唯一清单，修了一条就在它的状态格写提交。
- 回答的输出上限能否在设置里改（2026-11-13）。
- 统计图表“输出”颜色对比度低于 3:1：暂缓，不阻塞（你 2026-11-16）。
- 已定（你 2026-11-16）：删除会话或工作区时用量随之消失，保持现状；压缩时超过窗口的单条长消息不再单独处理，由今后的长文本／文件导入设计取代（你会写进需求）；窗口约 8k 的本地模型不支持。
- 删除前的整库备份从不自动清理；仓库里有个人路径与邮箱，公开仓库前须清理（健康检查 storage-7、repo-scripts-9）。

## 运行时资源

- 本机：日常数据 `~/Library/Application Support/Mindweave/claude-auto`，只经 `scripts/daily.sh`；见 [日常版本](docs/design/daily-build.md)。任务工作树运行应用时设 `MINDWEAVE_DATA_DIR`。
- Windows 实机：`scripts/windows.sh test|publish|sync|exec`（主机 `win-home`，经本机用户态 Tailscale 实例的 SOCKS `127.0.0.1:7898`，密钥登录）；目录 `E:\mindweave-claude-auto`（历史名称，可用 `MINDWEAVE_WIN_DIR` 另指）。`publish` 默认用通道 `dev`（`MINDWEAVE_WIN_CHANNEL=release` 构建演示用的发布版本）；2026-11-16 前的试用数据在通道 `claude-auto-dev`，要接着用就设 `MINDWEAVE_WIN_CHANNEL=claude-auto-dev`。
- 与 Windows 上的 Claude 用信箱传递任务与报告：`E:\mindweave-mailbox\`，协议见 [docs/mailbox.md](docs/mailbox.md)。
- 已授权：推送 `origin`；CI 可用（私有仓库只跑 Linux＋Windows）。

第一条命令：`scripts/test.sh full`。
