# S0 本地验证记录

日期：2026-10-26（Asia/Singapore）。分支：`track/codex-steady`，开发基线 `6645088`。本记录仅覆盖 S0 与最小工作区流程；后续聊天和来源功能不计为已实现。

## 本轮可用结果

- 四个产品工程和独立测试工程；Core／Application 不引用 Avalonia 或 SQLite 驱动。Desktop 仅在启动组装入口引用 Infrastructure。
- 首次启动新建主题，切换工作区，探索／总结网络／设置主区切换，正常关窗后重开恢复上次主题和主区。
- 主 ViewModel 持有唯一活动 `ContentStack`；非活动的工作区／主区保存导航请求快照，服务只转发请求。具体会话／总结 ID 的查找、来源跳转和最后详情页持久化随 S1／S2／S4 实施；当前只持久化工作区与主区。
- 单写入队列和独立只读连接；工作区创建与选中状态同事务提交。已有库升级前在线备份，迁移、关系检查与结构版本同事务提交；失败不替换原库。
- 重复启动同数据目录通过 Named Pipe 唤回原窗口。轨道数据目录、锁文件、IPC 和预留凭据命名空间互相隔离。平台凭据服务尚未实现。

## 环境与命令

macOS 26.6、Apple Silicon；SDK 10.0.402、运行时 10.0.13；Avalonia 11.3.21、Microsoft.Data.Sqlite 10.0.13。依赖固定为本机已有版本。没有调用 AI 服务，没有使用或保存密钥。

```sh
dotnet test Mindweave.slnx -c Release --no-restore --nologo
dotnet build Mindweave.slnx -c Release --no-restore --nologo
sh scripts/build-macos-dev-app.sh
```

Release 测试 **17 通过、0 失败、0 跳过**；构建 **0 警告、0 错误**。开发启动器还从 `/tmp` 使用脚本绝对路径构建成功，确认脚本不依赖调用者工作目录。

| 检查 | 实际覆盖与结果 |
| --- | --- |
| 4 项实例测试 | 轨道路径／身份分离；真实第二进程不能占用且通知首进程；释放后重获；监听取消不依赖调用方 UI 上下文 |
| 7 项 SQLite 集成测试 | 重开、20 个并发创建、拒绝非法选择且保留旧状态、迁移失败回滚与备份、备份失败不迁移、WAL 内容在线备份、新结构与损坏库拒绝、迁移事务中真实杀进程重开；部分场景合并在同一用例 |
| 3 项导航测试 | 首次引导和重开主区；工作区与主区历史隔离、跨区详情后退；保存失败保留主题输入和当前内容 |
| 3 项桌面测试 | 实际 XAML 绑定的输入、创建和菜单命令；损坏库进入 Root 恢复页并保留原文件、人工修复后重试；产品 MainWindow 正常关闭排空并关闭存储、拒绝新写入 |

测试采用真实 SQLite 文件和子进程；导航中模拟详情 ID 只验证导航状态，不证明聊天／总结实体存在。Headless 结果不替代原生交互检查。

## macOS 原生检查

通过 CUA 操作 `artifacts/macos/Mindweave Codex.app`，数据根目录固定为 `artifacts/s0-native/`：

1. 首次启动显示主题引导；输入并创建“火车历史 · S0 测试”。
2. 创建“蒸汽机 · S0 测试”，切回前一主题；窗口标题区域与正文跟随切换。
3. 打开总结网络主区，显示尚无总结且功能开发中的明确状态。
4. 最小化后第二次启动同数据目录；第二进程退出码 0，原窗口恢复显示，没有第二个写入实例。
5. 修复退出缺陷后点击原生关窗按钮，核查原 PID 已退出且没有残留产品进程；再次打开后恢复两个主题、所选主题及总结网络主区。

注意：CUA 对已退出应用再次请求界面可能重新启动应用，所以退出验证以原 PID 消失与进程清单为依据，不凭后续 AX 快照判断退出失败。

## 独立审查与修复

审查者只读检查 S0 代码，未运行构建或测试。发现一项 P1：App 的退出回调在 UI 线程等待取消的 IPC 监听，而监听捕获 UI 同步上下文，造成正常退出死锁。本轮原生关窗复现；新增非泵送同步上下文测试先以超时失败，再将监听／通知的全部异步等待改为 `ConfigureAwait(false)` 后通过；另外增加产品窗口关闭测试，并重复原生正常退出／重开。

审查者复核修复后未发现新增阻塞问题。脚本构建前切换到仓库目录、解析 dotnet 符号链接两项建议也已落实。

## 尚未覆盖与下一步

- Windows 11 x64 的原生启动、选区、来源恢复、IPC、正常退出和后续完整流程均待设备验证；本轮不称为 Windows 验收通过。
- S1 的真实服务、消息终态／检查点、停止、重启标为中断且不重发、凭据后端尚未实施。S2–S6 的来源、上下文、总结、网络、归档与导出同样仍待实现。
- 当前没有可供用户选择备份并恢复的交互；失败页提供错误、重试、打开数据位置与退出，始终不自动覆盖原库。恢复备份流程须在使用真实有价值数据前单独实施和故障验证。
- 迁移备份仅在升级时产生，不等于日常备份；`.partial` 是未完成备份，不能当作可恢复成功备份。
- 未验证课程页面／类数量、Windows 交付包、10MB 源码资源包和期末报告。Root 为启动状态切换的课程等价性仍按现行设计处理，未替老师确认。
- 开发启动器依赖本机 .NET，使用专用测试数据；它不是正式发行包。测试数据与开发启动器继续保留用于本轮体验，均不进 Git。

下一步按 [TRACK.md](../../TRACK.md) 接 S1，沿 [S0–S6 路线](../development-routes.md)推进。

## 实施依据

SQLite 一致性备份使用 [Microsoft.Data.Sqlite Online backup](https://learn.microsoft.com/dotnet/standard/data/sqlite/backup) 所述 `BackupDatabase`；未直接复制活动数据库文件。桌面启动和主窗口由 [Avalonia Application lifetimes](https://docs.avaloniaui.net/docs/fundamentals/application-lifetimes) 的 ClassicDesktop 生命周期组装。退出缺陷的同步上下文依据还核对了 [Avalonia 11.3.21 Application 源码](https://github.com/AvaloniaUI/Avalonia/blob/11.3.21/src/Avalonia.Controls/Application.cs)。
