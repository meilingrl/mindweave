# 推理过程与运行状态

依据：用户 2026-11-20 的计划第 1 步（推理过程的呈现、运行状态的说明，与已有的编织动作一起）与决定（推理内容只在生成回答时实时显示，默认折叠；从不保存，不进入分支、总结与分享；状态的措辞与动作的对应由实现者定）。需求 §10.4.4（v0.21），[生成中的指示](generating-indicator.md)，[运行与工具](runs-and-tools.md) R10，[运行循环的改进](agent-loop.md)。

## 1. 推理内容

**读法。** “只在生成回答时实时显示”理解为：回答进行中（从发送到结束，包括正文流入时、两次工具调用之间）都可以展开查看；回答结束（完成、停止、出错、中断）时，折叠区连同内容一起消失。没有采用“正文一开始就消失”的读法：运行中模型在两次工具调用之间还会再思考，正文开始之后仍有新的推理。

| 方面 | 做法 |
| --- | --- |
| 位置 | 回答顶部、活动说明之上，一行“思考过程”加展开箭头；只在已收到推理内容时出现 |
| 默认 | 收起。展开后是一块安静的区域（次要文字色，最高约 200 px，可滚动，跟随最新内容）；展开状态只在这条回答进行期间有效 |
| 多轮 | 运行中各次模型请求的推理按顺序接在一起，空行隔开 |
| 内存 | 只在进行中的回答（`LiveAnswer`）里；最多保留最后 2 万字，更早的部分丢掉并在开头标“…” |
| 不保存 | 不写数据库、不进入回答的块，因此不进入分支、总结、标题、上下文、导出与分享；不需要迁移。回答结束时清空 |
| 回传 | R10 不变：同一运行中随工具调用回传给服务（`reasoning_content`），与显示无关 |
| 页面切换 | 回答进行中离开再回来，仍从 `LiveAnswer` 读到已收到的推理 |

## 2. 一套状态

回答进行中，回答末尾始终有一行状态：编织的三个点、一句说明、计时。它取代原来分开的“等待文字”和“正在生成…”。正文上方的活动说明只记录做过的步骤（“搜索了 1 次 · 读了 1 个网页”），不再显示“正在…”，当前在做什么只在这一行说。

措辞规则：现在进行时“正在 + 动词 + 对象…”；对象（检索词、网页主机、工具名）放在冒号后，最多 40 字；不用比喻，便于第 2 步逐句译成英文。所有文字经 `UiText`。

| 阶段（`AnswerStage`） | 何时 | 说明 | English（第 2 步参考） | 动作 |
| --- | --- | --- | --- | --- |
| `Waiting` 等待 | 排队、连接、这一轮还没有任何内容 | 正在等待回答… | Waiting for the answer… | 编辫 `Braid` |
| `Compressing` 压缩 | 发送前压缩较早的对话 | 正在压缩较早的对话… | Compressing earlier messages… | 收紧 `Tightening` |
| `Thinking` 思考 | 这一轮收到推理、还没有正文 | 正在思考… | Thinking… | 连结 `Joining` |
| `Tool` 搜索网络 | `web_search` 执行中 | 正在搜索：{检索词}… | Searching: {query}… | 取线 `Reaching`（新） |
| `Tool` 读网页 | `fetch_page` 执行中 | 正在读取网页：{主机}… | Reading a page: {host}… | 取线 `Reaching` |
| `Tool` 检索附件 | `search_attachments` | 正在检索附件：{检索词}… | Searching attachments: {query}… | 取线 `Reaching` |
| `Tool` 检索总结 | `search_summaries` | 正在检索总结网络：{检索词}… | Searching summaries: {query}… | 取线 `Reaching` |
| `Tool` 第三方 | MCP 工具执行中 | 正在调用 {服务器} › {工具}… | Calling {server} › {tool}… | 取线 `Reaching` |
| `Approval` 等待批准 | 批准卡片出现时 | 等待你批准读取网址… ／ 等待你批准调用 {服务器} › {工具}… | Waiting for your approval to read an address… / to call {server} › {tool}… | 停候 `Holding`（新） |
| `Retrying` 重试 | L5：请求偶然出错，等待后重试，直到新请求有内容 | 请求出错，正在重试… | The request failed; retrying… | 编辫 `Braid` |
| `Finishing` 收尾 | 达到上限或中间一轮被截断后的最后一次请求，还没有内容 | 已达上限，正在根据已有结果作答… | Limit reached; answering from what was found… | 编辫 `Braid` |
| `Writing` 写回答 | 这一轮的正文正在流入 | 正在写回答… | Writing the answer… | 引线 `Thread` |
| 结束 | 达到上限时回答下方的说明不变（“达到了本次回答的……上限，根据已经取得的结果作答。”） | — | — | — |

**判定顺序**（同时成立时取前者）：等待批准 → 工具执行中 → 压缩 → 这一轮已有正文（写回答）→ 思考 → 重试 → 收尾 → 等待。“这一轮”指运行中的一次模型请求：第二轮开始时，即使上一轮写过正文，也回到等待或思考，而不是一直显示“正在写回答”。由 `LiveAnswer` 计算，界面只取 `Stage`。

总结生成的指示不变（编辫）。

## 3. 两个新动作

沿用 [生成中的指示](generating-indicator.md) 的一族动作：三个点、强调色、44×20 px，阶段之间 0.3 秒滑动过渡。

| 动作 | 描述 | 意思 |
| --- | --- | --- |
| 取线 `Reaching` | 两点停在左侧，第三点沿一条淡线向右伸出再收回，周期 1.4 秒；伸到最远时最实 | 向外取材料（搜索、读网页、调用工具） |
| 停候 `Holding` | 三点排成一行不动，一起缓慢呼吸（大小与浓淡），周期 2.4 秒 | 停下来等你 |

## 4. 代码

| 位置 | 改动 |
| --- | --- |
| `ChatStreamEvent.ReasoningDelta` | 带上推理文字（原来只有字数）；`Characters` 保留为文字长度 |
| `LiveAnswer` | `AppendReasoning`（取代 `MarkThinking`）、`AnswerStage`、快照的 `Reasoning` 与 `Stage`；`BeginTurn(final)` 开始新一轮；结束时清空推理 |
| `AnswerRunner` | 两条路径把推理文字交给 `LiveAnswer`；L5 重试前设为重试 |
| `MessageItemViewModel` | `StatusText`、`Motion`、`Reasoning`、`HasReasoning`、`IsReasoningExpanded`；`ActivityText` 只记录步骤 |
| `UiText.AnswerStatus` | 第 2 节的措辞 |
| `WeavingPaths` | `Reaching`、`Holding` |

## 5. 需求的改动

单独提交（AGENTS.md“改动需求或架构”），需求 v0.21：§10.4.4 活动说明一条改为“只记录做过的步骤，当前状态在回答末尾的状态行”，并加“推理过程”一段；架构 §15.6 与 R10 注明推理内容可实时显示、仍不保存。

## 6. 不在本次

- 回答正文里内联的 `<think>…</think>`（少数服务把推理写进正文）：现在仍留在回答正文里（标题、总结与压缩已去掉它）。是否在回答里也拆出来放进折叠区，另议。
- 英文界面（第 2 步）。
