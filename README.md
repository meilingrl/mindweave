# Mindweave

面向个人日常好奇与非专业专题学习的 AI 对话探索工具。

本工作目录为 **Codex 稳健轨**，已按确认的需求 v0.11 开始正式开发。当前实现范围、验证与下一步见 [TRACK.md](TRACK.md)，S3 的实现验证见[上下文验证记录](docs/implementation/s3-verification.md)，S2 的分支验证见[分支验证记录](docs/implementation/s2-verification.md)，S1 的真实服务记录见[聊天验证记录](docs/implementation/s1-verification.md)，S0 的数据关系见[基础实施记录](docs/implementation/s0-foundation.md)。共同需求与架构仍以 `main` 为准；它们记录共享设计，不代表各轨道的实时实现状态。

**仓库与分支：** [GitHub 私有仓库](https://github.com/meilingrl/mindweave)。`main` 是共享基线；`track/codex-steady`（稳健轨，课程提交版本）与 `track/claude-auto`（激进轨）从 `baseline-v0.11` 起分别开发，各自在 `../mindweave.worktrees/` 下有工作目录。分支约定见 [AGENTS.md](AGENTS.md)，各轨道状态见该分支的 `TRACK.md`。

## 建议阅读顺序

| 入口 | 用途与范围 |
| --- | --- |
| 1. [产品需求与首版范围](docs/requirements.md) | 现行产品行为、核心流程、首版／后续边界与验收；回答“做什么” |
| 2. [架构与技术方案](docs/architecture.md) | 现行工程职责、导航、数据与生命周期、网络保存、验证状态及实施待办；回答“如何实现” |
| 3. [开发路线](docs/development-routes.md) | 两条路线的目标、推进方式、互审与验证边界 |
| [稳健轨状态](TRACK.md) | 本轨道的当前阶段、验证入口与下一步 |
| [设计沿革与历史记录](docs/history/design-history.md) | 旧路线比较、阶段停止点、原型验收与复审决定溯源；按需查阅，不作为现行待办 |
| [详细设计就绪审查](docs/reviews/claude-design-readiness-20261024-172436/README.md) | 2026-10-24 独立审查：问题分级、v1–v4 版本、用户决定记录及[详细设计交接](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md)；v4 已写回现行需求与架构，交接不是开发授权 |
| [独立设计复审](docs/reviews/review-20261023-023314-sgt/README.md) | 继续分层导航：冻结的五轮报告、版本与D1–D8讨论来源；后续已确认调整以现行需求和架构为准 |

审查材料的迁移说明与旧路径解释见 [审查入口](docs/reviews/README.md)。正式产品代码在开发分支按需进入 `src/`，测试进入 `tests/`，构建与打包脚本进入 `scripts/`；原型独立保留。

需求与架构按职责维护，避免各自复制完整细则；历史记录保留当时结论，不以旧“待定”覆盖后续决定。发现冲突时回查明确决定，不自行扩大范围。

## 运行稳健轨

需要 .NET SDK 10.0.402（允许更新补丁版本）。从本工作目录运行：

```sh
dotnet run --project src/Mindweave.Desktop
dotnet test Mindweave.slnx -c Release
```

默认数据位于系统本地应用数据目录的 `Mindweave/codex-steady/` 下。需要独立测试数据时，运行 `dotnet run --project src/Mindweave.Desktop -- --data-root /absolute/test/root`，程序会继续在该根目录下创建 `codex-steady/` 子目录。同一数据目录重复启动会唤回原窗口。

macOS 可执行 `sh scripts/build-macos-dev-app.sh "$PWD/artifacts/s3-native"`，生成 `artifacts/macos/Mindweave Codex.app`，双击即可体验。请先正常退出该应用，再重建；脚本检测到它仍在运行时会拒绝替换。脚本按本机架构从全新目录发布，准备完成后才替换应用，并将原包保留为 `artifacts/macos-previous/` 下带时间戳的 `.reference` 目录。这个开发启动器依赖本机已安装的 .NET，参数指定专用测试数据根目录；不传参数时沿用 `artifacts/s0-native/`，它不是独立发行包。

当前桌面代码提供主题、会话、原生 Markdown 正文、流式发送／停止、末尾重试、草稿和最近会话恢复。先在设置填写兼容 Chat Completions 的服务、密钥、模型与预算，再新建会话明确点击发送。DeepSeek 地址与 `deepseek-flash` 只是可编辑的初始建议，保存不会自动发现模型或测试生成；模型目录须显式读取。密钥保存在本轨道的系统凭据命名空间，已有会话无密钥也可阅读。自动标题会单独发送请求并可能计费。正式桌面与 Headless 固定 Avalonia 12.1.4，Markdown 使用 LiveMarkdown.Avalonia 2.5.1，测试使用 xUnit v3 3.2.2；图片以不加载资源的提示文字显示，链接不自动打开。已接通选区分支、来源返回和独立阅读位置；已实现超预算时的上下文压缩、源会话摘要复用、失败保留和历史提示；知识总结与网络在后续阶段。S3 自动化与整阶段独立审查通过，macOS 合成核心路径已走查，原生余项／真实模型质量／Windows及完整课程验收见 [S3 验证](docs/implementation/s3-verification.md)。

已保存的 AI 回答中选中文字，点击该回答的“创建分支会话”进入子会话；父回答下的书签用于再次进入已有分支。“返回来源”定位原句，普通侧栏切换和后退恢复目标会话自己的最近阅读位置。选区不能确认时明确拒绝；来源无法精确恢复时仍可查看来源消息和保存引用。

本地协议走查可使用[测试专用 SSE 夹具](tests/fixtures/chat_sse_fixture.py)，S3 的启动、合成配置、测试问题和 Windows 测试包见 [上下文验证记录](docs/implementation/s3-verification.md)，S2 来源走查见 [分支验证记录](docs/implementation/s2-verification.md)，S1 历史见 [聊天验证记录](docs/implementation/s1-verification.md)。夹具不是真实模型，产品没有模拟回答回退。

构建输出、开发启动器和专用测试数据均在忽略目录中；源码与资源在四个 `src/Mindweave.*` 工程，测试单独放在 `tests/Mindweave.Tests`。当前检查与平台限制见 [S3 验证记录](docs/implementation/s3-verification.md)，S0 的历史原生检查见 [S0 记录](docs/implementation/s0-verification.md)。

## 验证资产

以下只列入口，不展开内部清单；使用方法、版本及证据边界以各自README为准。

| 入口 | 用途 |
| --- | --- |
| [Avalonia聊天原型](prototypes/avalonia-chat/README.md) | 聊天视觉、选区分支、返回来源、模拟流式和恢复参考 |
| [LiveMarkdown组件小样](prototypes/livemarkdown-chat/README.md) | 跨段选区、来源校验、重排和追加的独立验证 |
| [API兼容探针](prototypes/responses-compat/README.md) | Responses、Chat Completions及Messages的小规模协议验证记录；不存密钥 |
| [网络交互样例](prototypes/network-layout/README.md) | 本轮验证结束，保留布局浏览、拖动牵引、悬停吸引作为交互参考 |

原型通过不等于正式产品、全部平台或课程验收通过，也不证明延迟回顾价值。暂不继续扩展样例，不默认沿样例直接开发产品。
