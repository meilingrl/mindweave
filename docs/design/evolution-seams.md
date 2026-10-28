# 演进接缝的落地设计

依据 `main` 的[演进方向与工程指引](../evolution.md)。本轨道 2026-10-26 起把 E1–E6 作为实验引入。本文记录各接缝在本轨道的实际形态、成本与发现；产品行为仍以需求 v0.11 为准，接缝不改变任何用户可见行为。

| 接缝 | 状态 | 落点 |
| --- | --- | --- |
| E1 视图槽位化导航状态 | **已落地**（S2，迁移 v4） | Desktop `ViewerSlot`、`PageContext`、`NavigationRequest.Slot`；表 `reading_position`、`draft` 以槽位键为主键之一；见 [S2 设计](s2-branching.md#e1-视图槽位) |
| E2 分块消息内容 | **已落地**（迁移 v3） | Core `ContentBlock`；表 `message_block` |
| E3 请求追踪与源头脱敏 | **已落地**（迁移 v3） | Application `Diagnostics/`；表 `ai_request`；客户端与文件日志脱敏 |
| E4 模型能力描述 | **已落地**（迁移 v3） | Core `ModelCapabilities`；`model_config` 与 `generation_task` 的 `capabilities` 列 |
| E5 版本化交换格式 | **只有规格**，首版之后实现（D-021） | 本文 §E5 |
| E6 性能基线与架构测试 | **已落地** | `tests/…/Performance/`、`scripts/perf.sh`、`SeamTests` |

## E2 分块消息内容

- **模型。** `Message.Blocks` 是有序的 `ContentBlock` 序列，`ContentBlock` 为封闭层次（构造函数 `private protected`），目前只有 `TextBlock(Markdown)`。`Message.Text` 把文本块以空行连接，用于上下文组装、标题与界面显示；其他块类型加入时各自定义文本化方式。
- **存储。** `message_block(message_id, ordinal, kind, text, data)`，主键 `(message_id, ordinal)`，`ON DELETE CASCADE`（块是消息的纯从属行，符合级联规则）。`kind` 不用 CHECK 枚举，由应用校验，未知类型读取时抛 `InvalidDataException`，避免每加一种块就重建表。`data` 留给非文本块的结构化 JSON。
- **写入。** 检查点与终态写入在同一事务中先做守卫更新消息行，生效后再整体替换块（删除＋插入）。守卫失败时块不变。
- **锚点（S2 使用）。** 选区来源定位为（块序号，块内偏移），首版块序号恒为 0。
- **成本。** 改动集中在存储层与约 30 处 `Body` 引用；迁移 v3 把旧正文拷入 0 号文本块后 `DROP COLUMN body`。
- **发现：读取必须在同一快照内。** 消息行与块由两条语句读取。原先 `ReadAsync` 不开事务，两条语句之间若有提交，读者会拿到“已完成的消息行＋旧的空块”（全量测试在并发负载下偶发暴露，3/4 次运行有不同用例失败）。处理：`SqliteDatabase.ReadAsync` 统一在一个读事务中执行，所有多语句读取天然一致；回归测试在读取中途提交写入，验证读者仍看到旧值（去掉修复即失败）。

## E3 请求追踪与源头脱敏

- **追踪。** 每次 HTTP 请求一行 `ai_request`：开始与结束时间、首个响应耗时、首段文字耗时、结果（completed／truncated／stopped／failed）、HTTP 状态、供应商请求 ID（`x-request-id` 或 `request-id`）、错误分类、脱敏后的错误摘要、输出字符数。**不存任何内容与密钥。**一个任务可有多行，为 D5 的多轮工具循环预留。
- **采集。** 协议适配器先发 `ChatStreamEvent.ResponseStarted(status, requestId)`；`GenerationCoordinator` 用内部 `RequestTrace` 观察事件流，结束后写库。追踪写入是尽力而为：失败只记日志，不影响回答，也不改变存储健康状态。
- **脱敏位置。** `Redaction.Scrub` 在三处数据产生处调用：协议适配器抛出的异常（已知密钥＋凭据形态）、追踪记录的错误摘要、文件日志的每一行（含异常文本，作为最后一道防线）。迁移 v3 对已存在的 `error_detail` 做一次性清洗。
- **规则。** 已知密钥按原文替换（不少于 8 字符，避免误伤普通文本）；另外屏蔽 `Bearer` 令牌、常见厂商前缀（`sk-`、`pk-`、`rk-`、`AIza`）与 `api_key`／`token`／`secret`／`password`／`authorization` 加分隔符的形式。
- **发现：** 原客户端测试使用 4 字符的假密钥 `sk-x`，低于已知密钥的最短长度，脱敏测试无法发挥作用；已改为接近真实长度、且不具凭据形态的密钥，专门验证“按已知密钥替换”这一路径。
- **未做（等 D2）。** 用户可见的请求详情界面与诊断包导出；密钥与内容的类型级区分（目前密钥只以 `string` 在请求路径内流动，由架构测试限制读取位置）。

## E4 模型能力描述

- `ModelCapabilities(Streaming, ToolCalls, ImageInput, ReasoningOutput, PromptCaching, ContextWindowTokens)`，`DefaultsFor(protocol)` 给出协议默认值（Chat Completions：仅流式）。
- 存于 `model_config.capabilities`（JSON，snake_case）；`NULL` 读作协议默认值，因此旧行无需回填。任务接受时随快照冻结到 `generation_task.capabilities`。
- 保存设置时：模型不变则保留已声明能力，换模型则回到协议默认值。首版界面不暴露能力编辑。
- 架构测试禁止 Core 与 Application 源码出现供应商或模型名，功能只能按能力判断。

## E5 版本化交换格式（规格，首版之后实现）

导出包是一个目录（或其 zip），与 SQLite 表结构解耦：

```text
mindweave-export/
  manifest.json          格式名、格式版本、导出时间、应用版本、包含的工作区
  workspaces/<id>/
    workspace.json       名称、创建时间
    conversations/<id>.json
    conversations/<id>/<message-id>.md   每条消息的文本块，便于直接阅读
```

- `manifest.json`：`{"format": "mindweave-export", "version": 1, …}`。读取方遇到更高主版本时拒绝导入并说明原因；同一主版本内只增加可选字段。
- 会话 JSON 保存稳定 ID、标题与来源、消息顺序、角色、状态、重试关系，以及按块列出的内容（文本块引用同目录的 `.md` 文件，其他块内联结构化数据）。分支、来源锚点与总结在对应阶段加入，锚点使用（消息 ID，块序号，块内偏移）。
- **不导出：** 凭据引用、模型快照中的地址与凭据、`ai_request` 诊断记录、运行时状态（生成任务、阅读位置、草稿）。
- 导入时重新生成本地 ID 还是保留，由 S6 根据“导入到已有工作区”的需求决定。
- 外部工具（其他聊天应用的导出）的导入实现为到本格式的映射，不直接写库。
- 约束从现在开始生效：任何对外输出都不得直接序列化存储层记录。

## E6 性能基线与架构测试

- **数据生成器** `PerfSeed`：通过真实存储接口写入，数据经过与生产相同的守卫与表结构；内容只由序号决定，可重复。形态 `PerfShape(会话数, 长会话轮数, 回答字数)`；回答是含标题、列表、表格与代码块的 Markdown。分支树在 S2 引入分支后加入。
- **基准**：侧栏列表（预算 200 ms）与打开长会话（预算 300 ms），各取预热后 5 次运行的中位数。完整规模（1,000 个会话、200 轮、每条回答 20,000 字）需显式运行 `scripts/perf.sh`；常规测试跑小规模冒烟版，只验证生成器与读取路径可用，不检查耗时。
- **架构测试（`SeamTests`）**：只有请求路径（生成协调器、模型设置服务）与凭据存储实现可以引用 `ICredentialStore`；Core 与 Application 不得出现供应商名。
- 首次完整运行（2026-10-27，macOS Release）：生成 598 ms，列表 29.7 ms，打开长会话 25.8 ms（约 4 MB 正文）。存储读取远在预算内；预算保持不变，用来发现数量级退化。主要风险仍在渲染层（超长 Markdown 的排版与虚拟化），需要在 S2 引入 LiveMarkdown 后加界面基准。

## 交给 S2（E1 与迁移 v4）

S2 会话负责 E1 与 S2 的全部内容，迁移编号为 v4。可直接使用的名称与约定：

- 内容：`Message.Blocks`、`ContentBlocks.FromText`／`TextOf`、`TextBlock.Markdown`；锚点按（块序号，块内偏移）。
- 存储：`message_block` 已存在；新表引用 `message(id)` 时，跨实体引用用 `RESTRICT`，纯从属行才用 `CASCADE`。
- 读取：`ReadAsync` 已在单一快照内执行，多语句读取无需另开事务。
- 性能：分支树加入 `PerfSeed` 的形态，并为“打开深分支”加预算。
