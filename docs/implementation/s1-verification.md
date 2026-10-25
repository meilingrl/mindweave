# S1 会话与聊天 · 本地验证记录

日期：2026-10-26，轨道 `track/codex-steady`。Task 4 开始于 `271a751`；本记录区分代码／Headless、协议夹具、原生与真实供应商，不视为整个 S1 已验收。

## 可用实现

- 一个应用生命周期的 SQLite store、GenerationCoordinator、ChatCompletionsTransport／禁重定向 HttpClient 和 SystemCredentialVault；Root 初始化协调器恢复 Interrupted 后进入工作台，打开设置不读取密钥、不外呼。
- Main 持有唯一活动 ContentStack，菜单服务无状态；非活动历史保存 NavigationRequest 快照。探索侧栏按当前主题列会话并标出当前项，支持新建与手改标题。自动标题完成后同步列表与未被用户改动的标题输入。
- 会话正文显示所有回答尝试及未完整状态，正文用 SelectableTextBlock；输入、发送、停止和末尾重试均有绑定。正文投影合并 SQLite 和协调器最新内存，切页／切主题不停止任务。
- 草稿 300ms 防抖，显式修订单调递增；发送前刷新并锁定输入，接受成功后重新读取墓碑修订，后续输入严格前进。保存失败输入留在窗口，切换阅读时 Main 暂存失败草稿值与修订，显式重试保存所有暂存草稿和最新阅读位置。
- 设置可填写名称、地址、遮蔽密钥、模型、两种输出 token 参数与预算；发现模型是显式目录请求，失败仍可手填。保存不发现、不生成。连接测试由用户新建测试会话明确发送，界面说明发送目的地及自动标题费用。
- 有活跃／排队任务或正在接受发送时，先显示可取消退出确认；取消不暂停、不停止。确认后暂停 Main，等待页面已开始的保存／接受操作，协调器停止队列与运行流并保存／排空，保存草稿／阅读位置，再释放协调器与 client、最后排空关库。错误保留工作台和可复制正文，提供重试保存、重试退出和返回；已取消任务不复活。修复保存后继续显示退出／返回操作。

## 迁移 v3

`WorkspaceSelection` 增加可选 `Kind` 与 `TargetId`，原二参数调用仍有效。新增 v3 为 `app_selection` 添加详情身份，默认 Overview，Conversation 必须在 Explore 且属于同一主题；外键与写入触发器维护关系。打开详情前和启动恢复时重新验证工作区归属；尚未实现的 Summary 仅是明确占位，不持久化为真实详情。

v1／v2 SQL 与 `271a751` 逐段比较不变。既有升级在线备份、事务迁移与完整性检查继续使用；v2 升级回归检查新库版本 3、备份版本 2、真实会话身份往返与跨主题拒绝。所有选择 INSERT 改为显式列；旧 v1／v2 fixture 可继续创建与保存基础选择。S0 导航测试换成真实会话，原工作区／主区历史与后退断言保留；S1 存储故障下允许继续阅读的断言按现行要求更新。

## 自动化证据

```sh
dotnet test Mindweave.slnx -c Release --no-restore
dotnet build Mindweave.slnx -c Release --no-restore
python3 tests/fixtures/test_chat_sse_fixture.py
git diff --check
```

完整 Release：**122 通过、2 跳过、0 失败，共 124**；四产品工程与测试工程构建无警告／错误。两项跳过为原有的平台不适用和原生凭据 opt-in 测试；不是本轮新增失败的豁免。夹具自测：**2 通过**。

新增 13 项 ChatDesktopTests 用真实临时 SQLite 与 Avalonia Headless：实际 MainView 新建会话按钮、ChatView 输入与发送绑定；流／停止／两会话最新内存；重开 Interrupted、草稿、有效详情及零网络；遮蔽密钥、设置失败输入保留与显式发现；失败发现后的手填保存；取消退出；生成与草稿退出写入故障／重试／返回；保存失败下可读会话；多会话未保存草稿与阅读位置显式恢复；退出等待正在保存的配置；自动标题列表／标题输入更新及手改保护；篡改跨工作区详情不恢复。

TDD 首轮 5 项编译后预期失败；再分轮用新增失败复现并修复：退出时草稿失败跳过停止、修复保存后退出操作消失、存储失败阻塞阅读、退出提前关库破坏配置保存、自动标题正文标题不同步。测试替身仅替代网络和凭据边界；数据库、迁移、XAML 和桌面 VM 为真实实现。详细 RED／GREEN 和 API 交接在本地 Task 4 报告。

## 测试专用本机 SSE 服务

```sh
python3 tests/fixtures/chat_sse_fixture.py --port 18081
# 控制器先退出旧 S0 进程，再选择执行：
sh scripts/build-macos-dev-app.sh "$PWD/artifacts/s1-native"
```

在专用测试库的设置中填写：

| 字段 | 测试值 |
| --- | --- |
| Base URL | `http://127.0.0.1:18081/v1` |
| 密钥 | `mindweave-loopback-only`（公开合成值，非真实凭据） |
| 模型 | `fixture-normal` 或 `fixture-slow` |
| 参数 | `max_tokens` 或 `max_completion_tokens` |

`fixture-normal` 快速流；`fixture-slow` 默认 40 段、每段 0.6 秒，可用 `--slow-chunks`／`--slow-delay` 调整。自动标题返回固定合成标题；所有正文显式标注本地夹具。仅绑定 loopback，不访问外网，不记录正文和 Authorization。结束走查后 Ctrl+C 停止。夹具脚本与测试保留使用中；生成数据留在忽略的 `artifacts/`，不移动或删除既有 S0 资产。生产没有假响应或明文凭据降级。

## 尚未验收

- Task 4 独立审查、macOS 新版原生窗口的发送／停止／切换／重开／退出由控制器后续执行；本任务没有操作现有原生包或重建正在使用的启动器。
- 前任务已单独完成 macOS 系统合成凭据 roundtrip；本任务没有读取或写入用户既有凭据。Windows 原生未执行。
- 用户选择 DeepSeek 官方／`deepseek-flash`；地址和模型只是未保存的可编辑初值。没有用户密钥，也没有付费调用范围授权，本轮未调用供应商。实际 Chat Completions 及自动标题 128 token 上限仍须真实服务验收，夹具不证明模型容量／兼容性／质量。
- S2 正式 Markdown、选区来源和分支，S3 自动压缩，S4 总结及网络、备份恢复交互、跨平台发行与完整课程验收不计为本轮完成。
