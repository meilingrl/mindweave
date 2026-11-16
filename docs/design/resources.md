# 资源存储（D5 第一期地基）

依据[演进方向](../evolution.md) D5“2026-11-13 讨论形成的方向”与开发记录 `docs/history/agent-platform-discussion-20261113.md`。本文只覆盖内部存储地基：没有界面，用户看到的行为不变。需求与架构的正式文字由另一会话（agent-docs）起草；表名、列名与磁盘布局已按它的草稿对齐，仍不同的几处列在文末“与架构草稿的差别”，合并时以用户确认的架构为准，再改本文与代码。

## 概念

- **资源（`Resource`）**：一份保存下来的材料——粘贴、文件、图片，以后还有网页、工具输出。资源说明“它是什么”。
- **附件（`AttachmentBlock`）**：消息里引用资源的一个内容块（E2 封闭层次新增的一种）。附件说明“它何时、在哪里进入对话”。
- **派生内容（`ResourceDerivative`）**：由原件得到的内容，每种一行。本期只有 `text`（提取的全文）；摘要、图片描述、转写以后按同一张表加入，分块另有表。

## 数据（迁移 v14，暂定）

迁移目录要求版本号连续，所以本分支用 **v14**。推荐与关联会话（rec-association）已占用 v14；按“先合并的一方占用”，本迁移合并时顺延为 **v15**：改 `SchemaMigrations.cs` 里的编号、指纹文件那一行（指纹随编号变，重新生成）、`SchemaV14MigrationTests`（类名与 `Version` 常量），迁移内容不变。

```text
resource             id, workspace_id, conversation_id?, origin, name?, media_type, byte_size, sha256,
                     trusted, source_url?, created_at
resource_derivative  id, resource_id, kind, producer, prompt_version?, text, estimated_tokens?, created_at
                     UNIQUE (resource_id, kind)
message_block        + resource_id?（REFERENCES resource ON DELETE RESTRICT）
conversation         + 唯一索引 (workspace_id, id)，供 resource 的组合外键使用
```

- **归属。** 资源属于一个工作区（`workspace_id`，CASCADE），附在会话里后也属于那个会话（`conversation_id`，CASCADE）。组合外键 `(workspace_id, conversation_id) → conversation(workspace_id, id)` 让 SQLite 自己拒绝跨工作区的归属。`conversation_id` 为空表示还没发送（新探索的草稿也能先建资源）。
- **一个资源只属于一个会话。** 写入引用资源的消息时：资源必须在消息所在工作区；没有会话的资源随之归入该会话；已属于别的会话则拒绝写入（`InvalidOperationException`，整笔回滚）。同一份内容附到别处时新建一行资源，原件按哈希共用，不另占磁盘。这样删除会话或工作区只需级联，不必跨会话查引用。
- **删除。** 会话删除先删消息（块随之级联，`RESTRICT` 不再拦），再删会话，资源与派生内容随会话级联；工作区删除同理，未发送的资源随工作区级联。删除 SQL 本身没有改动。单独删除一个资源（以后“转为文字”用）只在没有块引用它时进行。
- **`origin`、`kind` 不用 CHECK 枚举**，与消息块类型一样由应用校验，以后加 `web`、`tool`、`summary`、`caption` 不必重建表。现有值：`origin ∈ {paste, file}`，`kind ∈ {text}`。
- **`name`** 只存文件名，不存路径；粘贴为 NULL，显示为“粘贴的文字”。
- **派生文字放在 SQLite**（以后要做检索与分块，且块读取要在同一快照里拿到它）；原件不放。`producer` 记录提取器及其版本（如 `plain-text/1`、`paste/1`）。`prompt_version`、`estimated_tokens` 留给模型生成的派生内容与上下文预算，本期为 NULL。
- **`trusted`**：用户自己粘贴或选择的文件为 1；以后网页与工具输出为 0，按 D2 处理。
- **块行。** 附件块的 `kind = 'attachment'`、`text` = 资源的显示名称（只读块文字的地方，如侧栏摘录，也看到名称）、`resource_id` 指向资源；`data` 不用。写入时只认 `resource_id`，块里其余字段读取时从资源取。

## 原件（blob）

```text
<数据目录>/resources/<哈希前两位>/<完整哈希>     原件，无扩展名，写入后不再修改
<数据目录>/resources/tmp/<随机名>                写入中的临时文件（tmp 不是两位十六进制，不会与分组目录混淆）
```

- **按内容寻址。** 哈希为原件字节的 SHA-256，64 位小写十六进制；`ContentHash` 类型与表上的 CHECK 只接受这种形式，路径由它拼出，不会越出目录。
- **原子写入。** 写临时文件、`Flush(true)` 落盘后改名到最终位置；同名且大小相同的文件已在，就是重复内容，沿用已有文件（去重）。写入失败（磁盘满、无权限）是 `StorageWriteException`，与数据库写入失败同样处理。
- **先写原件，再提交数据库行。** 行提交失败只留下一个无人引用的原件，由清理删除。
- **清理（sweep）。** 原件被删除的条件：当前数据库**和**每一份保留的数据库副本——备份目录里的备份、恢复时留下的 `mindweave.db.replaced-*`——都不再引用它的哈希。理由：删除工作区或级联删除会话前会先做整库备份，若原件随删除立即消失，备份就恢复不出附件。
  - 删除会话、删除工作区、删除未发送资源成功后各做一次清理（尽力而为，失败只记日志，不影响删除结果）；
  - 备份被删除或轮换后，它独占的原件在下一次清理时删除；启动时的清理**未接入**（见“不在本期”）；
  - 数据库未打开、或有一份副本读不了时，这一轮什么都不删；
  - 每次清理也删除一小时以前的临时文件；目录里不是本存储写的文件不动。
- **与创建并发。** 创建在写原件前“钉住”哈希，提交后放开；清理开始后被钉住或放开过的哈希这一轮都不删，判断与删除在同一把锁内。单实例锁保证只有一个进程。

## 备份与恢复

备份仍只是 `VACUUM INTO` 的数据库副本，不复制原件：原件不可变、按哈希共用，只要有副本引用就保留，所以恢复任何一份备份后它引用的原件都在，恢复流程不需要改动。代价是被备份引用的原件在备份删除前一直占空间——与“删除前的整库备份从不自动清理”这一现状一致；存储页以后可以显示这部分占用。

## 内容块的文字化

`ContentBlocks` 有两种文字化，消息只有文字块时两者都与原来相同：

| 用途 | 方法 | 文字资源 | 图片 |
| --- | --- | --- | --- |
| 上下文组装（`Message.Text`） | `TextOf` | `<attachment name="…" type="…">` 全文 `</attachment>` | `<attachment name="…" type="image/png" omitted="image" />` |
| 标题、摘录、界面显示 | `BriefOf` | `[附件：名称]` | `[附件：名称]` |

- 名称属性转义 `& " < >`；正文里的 `</attachment`（不分大小写）写成 `<\/attachment`，资源内容不能提前结束自己的段落。
- 生成标题已改用 `BriefOf`，不把整份文档送去起标题。
- 是否把图片原件发给模型按模型能力 `ImageInput`（E4）决定，属于撰写框那一步；本期上下文里只有占位。

## 应用层

`ResourceService`（`IResources`）：

- `CreateFromPasteAsync(工作区, 会话?, 文字)`：原件为 UTF-8 字节，`text/plain`，派生 `text` 为原文（换行统一为 `\n`）。
- `CreateFromFileAsync(工作区, 会话?, 路径)`：按扩展名判定。文本类（`.txt`、`.md`、常见源代码与配置格式）解码为文字：带 BOM 的 UTF-8／UTF-16，否则严格 UTF-8，再否则 GB18030；含 NUL、解码失败或控制字符过多即 `NotReadable`。图片（PNG、JPEG、GIF、WebP）只存原件，类型以文件头为准（扩展名不对也按真实类型存，不是图片则拒绝）。
- 其他类型返回 `Unsupported`。**接缝：** `ITextExtractor`（`Producer`、`CanExtract(媒体类型)`、`Extract(字节, 媒体类型)`）。PDF 与 Office 以后各加一个实现、在 `ApplicationServices` 注册即可，存储与块不变（PDF、DOCX 的扩展名也要加进 `FileTypes`）。
- 上限：原件 32 MiB（`ResourceLimits`，草稿写 50 MB，见差别）；空内容 `Empty`；打不开的文件 `FileUnavailable`。
- `GetAsync`、`OpenOriginalAsync`、`DeleteUnattachedAsync`；`ResourceService.BlockFor` 给出引用某资源的块。

## 不在本期

撰写框的“+”、长粘贴自动变附件与“转为文字”、草稿里的附件（架构草稿写 `draft_attachment` 表）；发图片给模型；分块与检索（`resource_chunk`、FTS5）；网页与工具资源；导出包（E5）里的资源；存储页显示原件占用；启动时清理；未发送资源的过期清理（随撰写框一起定）。

## 与架构草稿的差别（2026-11-17，供合并时对照）

agent-docs 草稿见共享笔记 `agent-docs-fixed-details.md`。已对齐：表名 `resource`、`resource_derivative`，列名 `name`、`byte_size`、`sha256`、`source_url`、`producer`、`prompt_version`、`estimated_tokens`，`message_block.resource_id`（RESTRICT），块类型 `attachment` 且 `text` 为显示名称，原件目录布局，粘贴显示为“粘贴的文字”，GB18030 回退。仍不同：

| 项 | 本分支 | 草稿 | 理由 |
| --- | --- | --- | --- |
| 可信 | `trusted` 0/1 | `provenance`（user／workspace／external） | 任务说明要求可信标记；三值来源需要时可加列，`trusted` 由它推出 |
| `origin` 取值 | `paste`、`file` | 另有 `image` | 图片是媒体类型，不是来源；图片文件的 origin 为 `file` |
| `workspace_id` | CASCADE | RESTRICT，删除工作区时显式删 | CASCADE 不用改删除代码；行为相同 |
| 块的 `data` | 不用 | `{resource_id}` | 已有外键列，JSON 重复 |
| `resource_derivative` 的分块 | 无（另表） | 同 | — |
| 原件上限 | 32 MiB | 文件 50 MB、图片 20 MB | 改常量即可，待需求定 |
| 清理时机 | 删除之后 | 删除之后与启动时 | 启动时清理未接入 |

## 测试

`ResourceStoreTests`（存储、去重、按类型读文件与编码、块读写与归属规则、同一快照读取、会话删除级联与原件清理、工作区删除前备份的恢复往返、删除备份后清理、清理与创建并发、临时文件与读不了的副本、数据库关闭时不清理）、`SchemaV14MigrationTests`（v13 数据升级、约束与级联、失败回滚）、`ContentBlockTests`（文字化、转义、哈希格式）。
