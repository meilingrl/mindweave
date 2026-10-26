# S2 分支与返回 · 验证记录

日期：2026-10-26，轨道 `track/codex-steady`。本地代码、完整独立审查与 macOS 本地合成服务核心走查已完成，Windows 与真实模型背景理解仍待验收；计划见 [S2 计划](s2-branch-plan.md)，当前状态见 [TRACK](../../TRACK.md)。以下按实际执行范围记录，不将自动化、原生、供应商与 Windows 混同。

## 基线与隔离

- S1 最终代码 `78f3fc1`，最终原生／DeepSeek 验收记录提交 `0629322`。S2 计划提交 `f576b24`。
- S2 开始前控制器重新执行完整 Release：129 通过、2 显式跳过、0 失败；结果仍属于 Avalonia 11 的 S1 基线。
- 产品开发复用 `/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-steady`。不在 main 写产品代码，不修改共享需求／架构，不借用激进轨代码，不推送。
- S1 原生包及 `artifacts/s1-native` 当前保存专用供应商验收数据与用户授权的凭据引用。S2 自动化不得写该库或读取其真实凭据；原生走查另用 `artifacts/s2-native` 与公开合成 key。

## 工程决定

LiveMarkdown 2.5.1 的实际 NuGet manifest 声明 Avalonia >=12.0.0，因此正式桌面／Headless 固定为共享小样验证过的 Avalonia 12.1.4。其 Headless.XUnit 需要 xunit.v3 3.2.2；测试入口兼容工作必须保留既有 ProcessProbe 子进程行为，不增加跳过来规避迁移。Task 1 提交 `07c3551` 已完成构建与自动化回归，修复后独立复审 Approved；正式产品原生检查结果见下方最终代码走查。

来源锚点仍是源文区块＋块内显示位置及完整性校验，不承诺通用 Markdown 原文字符映射。阅读位置独立保存。分支原子提交直接来源与固定祖先消息身份，实际生成预算与接受复用同一上下文组装规则。

## 验证进度

| 验证层 | 当前状态 |
| --- | --- |
| Markdown／选区适配、依赖升级 | Task 1 最终 `ea247a9`，14 项来源／视口测试通过，完整 Release 143 通过／2 原有跳过；独立复审 Approved |
| 分支事务／祖先上下文／阅读存储 | Task 2 `d03c147` 已实现，完整 Release 181 通过／2 原有跳过；独立审查 Approved |
| 桌面分支／返回／阅读闭环 | Task 3 `a6b77ad` 已实现，完整 Release 198 通过／2 原有跳过、夹具 5 通过；独立审查 Approved |
| S2 macOS 原生 | 最终 `492b80e`；用户完成真实鼠标 A→B→C，控制器核对返回、重入、阅读／草稿重开及最窄窗口流式追加 |
| S2 真实供应商 | 本阶段未调用；S1 原授权剩余 1 次生成，尚不足以覆盖本阶段长会话质量验收 |
| Windows 11 原生 | 尚未执行；用户暂时无设备，最终代码 win-x64 测试包已交叉构建并核对归档 |

S1 已用 7/8 次授权生成、1/1 次模型查询。本阶段实施不增加真实调用，先用明确标识的本地协议夹具完成可重复的布局／来源／上下文验证。

## Task 1 自动化证据

实现者在 `07c3551` 对应代码上执行 `dotnet test Mindweave.slnx -c Release --logger 'console;verbosity=normal'`：142 项中 140 通过、2 原有显式跳过、0 失败；构建 0 警告／0 错误。控制器已读取原始结果尾部与详细报告，最终全阶段验证将另行重跑。11 项新增用例包含真实 Headless＋Skia 鼠标选区、重复词／跨格式跨段、严格信封校验、原文 hash、错误恢复无部分高亮、图片惰性表现、更新复用旧选区、布局前连续快照与阅读恢复。三项 S1 子进程探针在新测试入口下实际通过。

`OffsetWithinBlock` 明确定义为非负有限的块内像素偏移，重排后钳制到当前块高度；不承诺跨宽度的精确字符行位置。恢复来源的选区校验与阅读定位是独立 API。

Task 1 独立审查发现一项 Important：下方可见消息更新时可能被移到视口顶部，多个消息的重排恢复会竞争。修复 `ea247a9` 经 3 项真实多消息 Headless 红绿回归，原问题已由限定复审关闭；完整测试 143 通过、2 原有跳过，构建零警告／零错误。保留一项 Minor：滚动时各消息重复全局区块扫描，交最终审查结合长文规模判断。

Task 2 `d03c147` 独立审查 Approved，无 Critical／Important。保留一项渲染／存储 payload 上限不一致的 Minor，将在 Task 3 接口集成时统一为既有存储的 1 MiB UTF-8 并补多字节边界测试；不是额外付费行为。原子性以真实 SQL ABORT／ROLLBACK 结果为证，不冒充分支途中杀进程实测。

原生准备：S1 原生进程 36752 已经正常关闭并确认退出；旧应用包另存 `artifacts/macos-s1/Mindweave Codex.app` 作为使用中的对照，DLL 哈希仍与 S1 验收记录一致。S1 数据与真实凭据未用于 S2 实施。S2 应用包将在审查后从最终代码重建，不能用旧包或 Headless 结果替代。

## 控制器最终本地复查（代码 a6b77ad）

重新执行 `dotnet test Mindweave.slnx -c Release --no-restore --nologo`：**198 通过、2 原有显式跳过、0 失败，共 200**。`dotnet build Mindweave.slnx -c Release --no-restore --nologo`：**0 警告、0 错误**。`python3 tests/fixtures/test_chat_sse_fixture.py`：**5 通过**。共享 requirements.md／architecture.md 与 main 的 diff 为空。三项任务独立审查均 Approved；整体 S2 审查与原生结果还需下述后续补充，不能据此宣称全部 S2、Windows 或课程验收完成。

原生协议走查使用 `python3 tests/fixtures/chat_sse_fixture.py --port 18081`，配置 Base URL `http://127.0.0.1:18081/v1`、公开非访问凭据 `mindweave-loopback-only`、模型 `fixture-markdown`。用 `sh scripts/build-macos-dev-app.sh "$PWD/artifacts/s2-native"` 构建专用启动器；30 段合成正文用于选区和滚动，不能冒充真实模型接续背景能力。

## 最终代码、完整审查与 macOS 走查

最终产品代码为 `492b80ee017eb3a03c3ab38c623d1dcc25cc8fa9`。控制器在此代码上重新执行完整 Release：**198 通过、2 原有显式跳过、0 失败，共 200**；重建 macOS 包 **0 警告、0 错误**。两个跳过仍是未知平台凭据分支和需 opt-in 的本机合成凭据用例，不是新增跳过。Python 夹具实现与已执行的 5 项通过版本相同。完整 S2 审查及其后元数据限定复审均 **Approved**，无待修 Critical／Important；[原始报告与复审](../reviews/codex-s2-20261026.md)已纳入正式记录。

本轮应用包使用 `artifacts/s2-native/codex-steady/mindweave.db`，schema v4；只连接 `127.0.0.1:18081` 的公开合成配置。没有读取 S1 真实密钥，没有新增供应商调用。合成服务不依据输入推理，因此以下实际输入身份的检查不能视为模型理解上下文。

| 场景 | 实际结果 |
| --- | --- |
| 真实选区 A→B→C | 原生鼠标工具早期连续报 noWindowsAvailable，由用户完成两次拖选和创建；控制器在原生路径／侧栏及 SQLite 核对两层关系。B 引用 A 第二次回答第 29 段“供本地滚”；C 引用 B 首次回答第 1 段“测试段落”。来源 revision 及正文 SHA-256 均匹配已保存终态 |
| 冻结范围 | B 为 1 祖先范围／2 消息身份，C 为 2 范围／4 身份。A 后来重试到第三次后，C 的实际回答任务输入为 5 身份，包含旧 A 来源，排除 A 后来的重试；全过程分支数保持 2 |
| 逐层返回 | C→B 正确高亮第 1 段“测试段落”，B→A 正确高亮旧回答第 29 段“供本地滚”，没有跳到其他重复词。B 后来再次重试后，C 返回仍落在旧 B 的正确来源 |
| 普通切换／书签 | B 来源返回偏移 227.5；滚动到 1207 并写草稿后，普通侧栏 A→B 和父来源书签重入恢复 1207 与 B 草稿，不再次应用来源意图，也不新增分支 |
| 正常关闭／重开 | 关闭前 C 阅读偏移 1140.5、未发送草稿已保存；最终代码重开后恢复路径、正文、草稿与相同偏移。重开前后任务数仍为 6，无新接受或自动重发；随后为窄窗验证显式发起 1 次本地重试，最终为 7 个 Completed 任务、0 活动任务 |
| 宽度重排与下方流式追加 | 使用原生 zoom 从 2940 像素宽恢复到 2240，再实际拖到最小 1760 像素宽（2x 截图对应逻辑宽 1470／1120／880，MinWidth=880）；来源高亮仍在第 1 段。窄窗下显式重试 B，下方新回答从生成到完成，原上方阅读偏移仍 227.5，高亮不变 |
| 原生命名与辅助功能 | 修复前菜单名默认 Avalonia Application、书签名为通用 StackPanel。`492b80e` 后实际为 Mindweave Codex，以及“打开分支会话：原生 C · 二层分支”加保存引文 HelpText；限定复审 Approved |

最终测试应用正常关闭，控制器确认 PID 53328 已退出；本地夹具也已停止。证据 scratch `.superpowers/sdd/s2-branch-plan/` 保留使用中，含重开前和最终只读数据库快照；不会复制配置或密钥到报告。

### 保留的界限

- 来源损坏后的消息级降级已由真实 Headless 控件集成测试覆盖；本轮未额外在原生测试库注入损坏。分支事务做过 SQL ABORT／ROLLBACK 和提交结果丢失测试，未单独执行分支事务中途杀进程。
- 原生流式追加与窄窗结果是上述 30 段合成样例的观察，不是 200 消息或定量长历史性能验收。S2-M1 每消息重复扫描共享视口的 O(M×B) 开销保留为 P3；既有 S1-F3 人工极端修订号溢出 P3 仍未修复。UI 快照解析路径也没有背景解析性能保证。
- Windows 11 尚无实机，用户已反馈暂时没有设备；不阻止独立的 S3 工程推进。真实供应商的多层背景理解、纠正和指代仍需另行质量验收；完整课程／发行验收未执行。

## 本地 Windows 开发测试包

从最终 `492b80e` 执行：

```sh
dotnet publish src/Mindweave.Desktop/Mindweave.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o artifacts/windows-s2/app --nologo
```

交叉发布成功，包为 `artifacts/windows-s2/Mindweave-Codex-S2-win-x64-492b80e.zip`，**77,089,090 字节、247 条目**；SHA-256：`d42968c3f47d19fa8c352e296993c36141556bb60b24b6ec0e8d9dbd390e1dba`。逐文件 manifest hash 与 ZIP CRC 已核对。包包含 self-contained app、可选 Python 本地夹具、两个启动脚本与中文操作清单，不含 DB、真实密钥或用户会话。数据入口固定为解压目录 `test-data/codex-steady`；包内 README 明确尚未 Windows 实机验收。此产物仅保留本地，没有上传或发布。

## 资产位置后续澄清

前文记录的 S1 对照 `.app` 是准备时的位置。发现重复 BundleID 后，本轮新建的 S1 对照副本已归档为 `artifacts/macos-s1/Mindweave-Codex-S1.zip`，原字节目录保留为 `Mindweave-Codex-S1.reference`；ZIP 的 92 个普通文件已逐项校验，CRC 正常，SHA-256 为 `bdf45387098b37bf8595dcc0a5adade7caf78d671ce79ecb40901bfe4e0d7a8a`。未改变系统应用注册／安全设置，S1 数据和真实凭据仍原位保留。当前 `artifacts/macos/Mindweave Codex.app` 是最终 S2 包。
