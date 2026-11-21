# Runs and tools (agent platform phase 2)

Basis: [requirements](../requirements.md) §10.4.4 and the phase 2 acceptance in §11 (v0.17), [architecture](../architecture.md) §15.3–15.7 and §15.10–15.12, [questions to decide](agent-platform-questions.md) (the user passed Q8–Q11 as recommended on 2026-11-17). This document records implementation-level choices; product behavior follows the requirements and architecture.

## 1. New decisions in this phase (user confirmed on 2026-11-17)

Places that the requirements and architecture leave open, where this phase must pick one. All were picked as "simple, reversible, and not changing accepted behavior"; if the user picks something else, the related code changes with it.

| # | Decision | Reason | Other options |
| --- | --- | --- | --- |
| R1 | The answer body is one `text` block at ordinal 0, and the text of each of the model's replies is appended to it in order; tool blocks come after the body. The UI draws the activity text above the body | Anchors, sidebar excerpts and checkpoints all locate by block ordinal; with the body fixed at 0, old code and old answers are not affected. The original §15.3 text "blocks are arranged in the order they happen" changes to this | Interleave in the order they happen (anchors and rendering would both have to be rewritten per block) |
| R2 | In phase 2 the answer body has no numbered citations; summaries found by “检索总结网络” (search summary network) are listed in the step details, and clicking one opens it | Q8 says citations are done in this phase only when local tools need to cite; the step details can already "jump to the matching summary"; numbered citations are designed together with phase 3 web search | Add the `citation` block and [n] parsing in this phase |
| R3 | “检索总结网络” scores the titles and bodies of the current versions in this workspace against the query in memory (the same tokenization as attachments: words and adjacent CJK character pairs); no FTS5 index is built | A workspace has tens to hundreds of summaries, and scoring takes milliseconds; an index would have to stay in sync wherever summaries are generated, edited, deleted or switch versions, which needs a migration and triggers | An FTS5 table with triggers (do it when summaries number in the thousands) |
| R4 | Every successful tool result is stored as a resource (`origin = 'tool'`), not only large ones; when the part given to the model exceeds about 2,000 estimated tokens, only a preview plus a number is given | The step details must be able to open every result; afterwards a result can still be found by “检索附件” (search attachments); resources are deleted with the conversation, with no separate cleanup | Store only results over the threshold |
| R5 | A tool is offered only when it has something to search: “检索附件” is offered only when the conversation chain has resources with text, and “检索总结网络” only when the workspace has saved summaries; when neither is offered, the answer is exactly the same as in phase 1 | An empty tool only costs more tokens and tempts the model into a wasted search | Always offer both tools |
| R6 | Meaning of the limits: at most 8 model requests, and the one after any limit is reached offers no tools; it counts toward the request count, and the duration gets 1 extra minute; calls in one reply beyond the tool call limit are not executed and are returned as “未执行：达到工具调用上限” (not executed: tool call limit reached) | Q9 says "when a limit is reached, ask the model to answer one last time"; counting this one within the 8 makes the limit the user sees the real limit | The 9th is the last one |
| R7 | Phase 2 does not change attachment planning: §15.4's "when the model can call tools, attachments over a quarter of the available space send only the digest and a handle" is postponed | The phase 1 way of sending has just passed the user's trial; changing to "digest only" would make answers that call no tools worse, and whether it is worth it depends on real use | Change it in this phase |
| R8 | The last (tool-free) request keeps the run's tool calls and results, does not send `tools`, and adds one system note: “已达到本次运行的上限，请根据已有结果直接回答，不要再调用工具” (the limit of this run has been reached; answer directly from the results so far and do not call tools again) | Only sending `tool_choice: "auto"` works everywhere (model-capabilities §5), so `"none"` cannot be used; dropping the tool results would waste the searches | Send `tools` with `tool_choice: "none"` |
| R9 | Adjustable ranges of the limits in 设置 › 工具 (Settings › Tools): 2–32 requests, 1–64 tool calls, 1–30 minutes, 20,000–2,000,000 estimated tokens; stored in preferences (`tools.enabled`, `tools.limits`), no migration needed | Prevents entering 0 or a huge value by mistake; preferences already have keys of the same kind | No ranges |
| R10 | Reasoning is kept and sent back (`reasoning_content`) only in the memory of the same run, and is never written to the database | DeepSeek, Kimi and others require it to be sent back as is in the tool loop; not saving reasoning is the current rule (2026-11-06). Since 2026-11-20 it is shown live while the answer is in progress, collapsed by default, and still not saved (requirements v0.21, [Reasoning and run status](reasoning-and-status.md)) | Store it in the steps |

## 2. Storage (migration v17)

| Change | Notes |
| --- | --- |
| `run_step` | The columns of §15.3: primary key (`task_id`, `ordinal`), `kind` (`model` / `tool`), `state`, `ai_request_id`, `call_id`, `tool`, `risk`, `approval`, `target`, `result_resource_id` (`ON DELETE SET NULL`), `started_at`, `finished_at`, `error_kind`, `error_detail`; plus `input` (a short form of a tool step's arguments, such as the query, for the step details and statistics). Deleted with the task |
| `generation_task` | Adds `tools` (JSON: the frozen tool names and definition hash; empty means no tools), `run_limits` (JSON: a snapshot of the four limits), `run_limit_reached` (the limit reached, or empty) |
| `resource` | Adds `produced_by_task`, `produced_by_step` (which step produced a tool result; identifiers only, with no foreign keys: resources are deleted with the conversation, and so are tasks) |
| Content blocks | `tool_call`: `text` is a one-line readable description (“检索附件：蒸汽机” (search attachments: steam engine)), `data` is `{call_id, tool, arguments, risk}`; `tool_result`: `text` is the result preview, `data` is `{call_id, status, detail}`, `resource_id` is the result resource (empty on error) |

## 3. The run loop

| Where | Content |
| --- | --- |
| `ConversationService.RunPlanAsync` | Decides, when a question is accepted, whether it is a run: the snapshot declares `ToolCalls`, `IToolSettings` is on, and `ToolRegistry.OfferAsync` offers at least one tool (R5). The scope tools can reach, `ToolScope`: attachments with text and tool results in the frozen chain (the frozen scopes of the ancestors, and this conversation). Tools and limits are frozen into the task (`generation_task.tools`, `run_limits`) and into `AnswerWork.Run` |
| `AnswerRunner.Run.cs` | Loops in the task's own execution slot; each model request and each tool execution is a step: a `running` row is written when it starts, and when it ends the row is written together with the answer's blocks (`IRunStore.FinishStepAsync`, with the same guard as checkpoints) |
| `LiveAnswer` | The body is still one string (R1); adds tool blocks, the tool currently running and the limit reached; `Blocks` is "body block + tool blocks" |
| `RunServices` | Writes step storage and tool result resources (the original first and the row after, with the hash pinned in between) |
| Recovery | `RecoverUnfinishedAsync`, in one transaction, changes `running` steps to `stopped` (to `unknown` for those with side effects), and `awaiting_approval` to `stopped` |

- **Limits** (checked before each request): if any one holds (requests sent have reached "limit − 1", tool calls have reached the limit, the duration is up, or the accumulated estimated tokens have reached the limit), this request is the last one: no tools, the R8 note is added at the end of the system turn, and `run_limit_reached` records the reason. When the available capacity cannot hold the next round, the earliest tool results are first shrunk to one line; if it still does not fit, it is also handled as reaching a limit.
- **Stop**: cancels the model request or tool in progress; the step in progress is recorded as `stopped`, and completed steps and the body are kept. When the stream of a model request does not end normally (no `Completed`), the tool calls in it are not executed (their arguments may be truncated).
- **Errors**: a tool that does not exist, arguments that are not a JSON object, arguments that do not match (`ToolInputException`), a timeout, and an exception thrown by the tool are all returned to the model as “出错：……” (error: …) and count as tool calls; after the same tool errors 3 times in a row, it is no longer offered in this run. When saving a tool result fails (storage unavailable), the answer fails, the same as other storage failures.
- **Results given to the model**: a result of at most about 2,000 estimated tokens is sent whole; otherwise its beginning is sent, with “完整结果是资源 r，可用 search_attachments 的 resource 与 part 读取” (the full result is resource r; it can be read with the resource and part of search_attachments).

## 4. Protocol adaptation (Chat Completions)

`ChatCompletionsClient` (implemented by a subtask, commit aadca49):

- With tools, the request body adds `tools` and `tool_choice: "auto"`; OpenRouter addresses also add `provider.require_parameters: true`, and Zhipu (`bigmodel.cn`, `z.ai`) also adds `tool_stream: true`. Without tools, the request body is exactly the same as before.
- Tool calls in an assistant turn are sent as `tool_calls` (when the text is empty, `content` is null; invalid arguments are sent as `"{}"`), and `reasoning_content` is added when there is reasoning; tool results are sent with `role: "tool"` and `tool_call_id`.
- Stream parsing: the fragments of `delta.tool_calls` are joined by `index` (the first non-empty ID wins; name and argument fragments are appended in order; relays that resend the whole name are not joined twice), and one `ToolCalls` event is emitted on `finish_reason` or `[DONE]` (before `Completed`). When there is no ID, `call_<n>` is generated, and duplicate IDs get a suffix. Empty arguments are treated as `{}`, and arguments that are not a JSON object are marked invalid. When `finish_reason` is `length` or `content_filter`, no tool calls are emitted. For a non-streamed answer that comes as one JSON body, `message.tool_calls` is read the same way.
- The reasoning text is handed to the run only in the `ToolCalls` event; `ReasoningDelta` still reports only the length.

## 5. UI

- Above the answer body there is one line, “检索了 2 次附件 · 检索了 1 次总结网络” (searched attachments 2 times · searched the summary network 1 time) (while running, “正在检索附件…” (searching attachments…)); clicking it expands the steps: the tool and its input, the result (“找到 3 段” (found 3 passages) or the error), time taken and state, “查看结果” (view result) (opens the tool result resource in the attachment viewer page) and the summaries found (click to open). When a limit is reached, a line below the answer explains it. New icon `IconToolSearch`; styles in `Styles/Runs.axaml`.
- 设置 › 工具: the use-tools switch and the four limits (`NumericUpDown`, ranges in R9); changes are saved at once and apply to later questions; “恢复默认” (restore defaults). When the model does not declare tool calls, the whole section is grayed out and states the reason and where to set it.
- Usage statistics: a new “工具” (Tools) section: the number of answers that used tools in the selected period, the average number of model requests per run, and the number of calls per tool (from `run_step`).

## 6. Tests

`SchemaV17MigrationTests` (upgrading v16 data, constraints, cascades, rollback on failure); the tool call part of `ChatCompletionsClientTests` (request body, joining streamed fragments, ID generation, invalid arguments, truncation, non-streamed); `RunFlowTests` (every row of the phase 2 acceptance reachable at the service layer: searching attachments and reading by number, searching the summary network, capability not declared or switch off, nothing to search, request limit, tool call limit, invalid arguments and nonexistent tools, no longer offered after consecutive errors, timeout, stop during a model request, stop during tool execution, restart during a run, retry, later questions do not contain tool calls, two runs at once with a third queued, limits frozen at acceptance, statistics); `RunUiTests` (activity text and steps, view result, limit note, 设置 › 工具 grayed out and available, save and restore defaults, statistics page; outputs light and dark screenshots when `MINDWEAVE_SCREENSHOT_DIR` is set).

Not covered: the duration limit and the token limit have only unit-level decision code and no integration tests (the minimum duration is 1 minute, and tests should not wait that long); tool calls with real services (see below).

## 7. To be checked with real services (paid, your decision)

1. DeepSeek: whether sending back `reasoning_content` in the tool loop is accepted (the documentation says 400 when it is missing); whether a last request without tools, but with tool calls in its history, is accepted.
2. Kimi K3: the same sending back of reasoning; whether parallel calls carry `index`.
3. Zhipu GLM: whether `tool_stream: true` is accepted and gives incremental fragments; whether `api.z.ai` and `open.bigmodel.cn` behave the same.
4. Alibaba Cloud Bailian: tool calls with `stream=true` (old documentation says this is not possible); whether fragments repeat the ID or name, or omit `index`.
5. OpenRouter: whether `require_parameters` makes models that do not support tools report a clear error; Claude's reasoning through OpenRouter comes in the `reasoning` field and is currently not sent back.
6. OpenAI (gpt-4.1 / gpt-5 series): `content: null` in assistant turns, and parallel tool calls; a last request without `tools` but with tool history (R8).
7. Any service: whether the model uses the `resource` / `part` of “检索附件” to read longer results, and whether the 2,000-token result limit is right.
