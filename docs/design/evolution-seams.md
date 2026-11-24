# Design of the evolution seams as built

Based on the [evolution directions and engineering guidance](../evolution.md). E1–E6 were introduced from 2026-10-26. This document records each seam's actual form, its cost and findings. Product behavior follows the requirements.

| Seam | Status | Where |
| --- | --- | --- |
| E1 Viewer-slot navigation state | **Built** (S2, migration v4) | Desktop `ViewerSlot`, `PageContext`, `NavigationRequest.Slot`; tables `reading_position` and `draft` have the slot key as part of the primary key; see the S2 design (`docs/history/design/s2-branching.md` in the repository, “E1 视图槽位” (E1 viewer slots)) |
| E2 Block-based message content | **Built** (migration v3) | Core `ContentBlock`; table `message_block` |
| E3 Request tracing and redaction at the source | **Built** (migration v3) | Application `Diagnostics/`; table `ai_request`; redaction in the client and the file log |
| E4 Model capability description | **Built** (migration v3) | Core `ModelCapabilities`; the `capabilities` column of `model_config` and `generation_task` |
| E5 Versioned exchange format | **Specification only**, implemented after the first release (D-021) | §E5 of this document |
| E6 Performance baseline and architecture tests | **Built** | `tests/…/Performance/`, `scripts/perf.sh`, `SeamTests` |

## E2 Block-based message content

- **Model.** `Message.Blocks` is an ordered sequence of `ContentBlock`. `ContentBlock` is a closed hierarchy (constructor `private protected`); it has text, attachment, tool call, tool result and citation blocks. `Message.Text` joins the text blocks with blank lines and gives each attachment as a one-line marker; context uses `ContextTextOf`, which adds an answer's citations. Each other block type defines its own text form when it is added.
- **Storage.** `message_block(message_id, ordinal, kind, text, data, resource_id)`, primary key `(message_id, ordinal)`, `ON DELETE CASCADE` (blocks are pure dependent rows of the message, which fits the cascade rule). `kind` has no CHECK enumeration; the application validates it, and reading an unknown type throws `InvalidDataException`. This avoids rebuilding the table for every new block type. `data` holds structured JSON of tool and citation blocks.
- **Writes.** Checkpoint and terminal-state writes, in the same transaction, first apply the guarded update to the message row; only once it takes effect do they replace all blocks (delete + insert). When the guard fails, the blocks do not change.
- **Anchors (used by S2).** A selection's anchor is located as (block index, offset within the block); in the first release the block index is always 0.
- **Cost.** The changes are concentrated in the storage layer and about 30 references to `Body`; migration v3 copies the old body into text block 0, then runs `DROP COLUMN body`.
- **Finding: reads must happen within one snapshot.** The message row and its blocks are read by two statements. `ReadAsync` used to run without a transaction; if a commit landed between the two statements, the reader got “a completed message row + the old empty blocks” (the full test suite exposed this intermittently under concurrent load; in 3 of 4 runs, different test cases failed). Fix: `SqliteDatabase.ReadAsync` always runs inside one read transaction, so all multi-statement reads are consistent by construction. A regression test commits a write in the middle of a read and checks that the reader still sees the old values (it fails when the fix is removed).

## E3 Request tracing and redaction at the source

- **Tracing.** One `ai_request` row per HTTP request: start and end time, time to first response, time to first text, result (completed / truncated / stopped / failed), HTTP status, provider request ID (`x-request-id` or `request-id`), error category, redacted error summary, output character count. **No content and no keys are stored.** A task can have several rows (a run makes several requests); work without a task has rows of its own for its workspace (v10). Later columns: reported and estimated tokens, purpose.
- **Collection.** The protocol adapter first emits `ChatStreamEvent.ResponseStarted(status, requestId)`; `GenerationCoordinator` observes the event stream with an internal `RequestTrace` and writes to the database at the end. Trace writes are best effort: a failure is only logged; it does not affect the answer and does not change the storage health state.
- **Where redaction happens.** `Redaction.Scrub` is called where data is produced: exceptions thrown by the protocol adapter (known keys + credential shapes), the search service and MCP servers, the error summary in trace records, and every line of the file log (including exception text, as the last line of defense). Migration v3 cleans existing `error_detail` values once.
- **Rules.** Known keys are matched by their literal text and replaced (at least 8 characters, to avoid hitting ordinary text); in addition, `Bearer` tokens, common vendor prefixes (`sk-`, `pk-`, `rk-`, `AIza`) and `api_key` / `token` / `secret` / `password` / `authorization` followed by a separator are masked.
- **Finding:** the original client tests used a 4-character fake key `sk-x`, shorter than the minimum length for known keys, so the redaction test could not do its job. It now uses a key close to real length and without a credential shape, specifically to verify the “replace by known key” path.
- **Not done (waiting for D2).** A user-visible request details UI and diagnostic bundle export; a type-level distinction between keys and content (for now keys flow only as `string` inside the request path, and architecture tests limit where they can be read).

## E4 Model capability description

- `ModelCapabilities(Streaming, ToolCalls, ImageInput, ReasoningOutput, PromptCaching, ContextWindowTokens)`; `DefaultsFor(protocol)` gives the protocol defaults (Chat Completions: streaming only).
- Stored in `model_config.capabilities` (JSON, snake_case); `NULL` reads as the protocol defaults, so old rows need no backfill. When a task is accepted, the capabilities are frozen with the snapshot into `generation_task.capabilities`.
- When settings are saved: if the model does not change, the declared capabilities are kept; if the model changes, they return to the protocol defaults. The first release's UI does not expose capability editing.
- 2026-11-17: 设置 › 模型连接 › 高级设置 (Settings › Model connection › Advanced settings) can declare “工具调用” (Tool calling) and “图片输入” (Image input); presets prefill them by service and model; features query them with `IModelSettings.ActiveModelHasAsync`. See [Model capabilities](model-capabilities.md).
- Architecture tests forbid provider or model names in Core and Application source code; features can only decide by capability.

## E5 Versioned exchange format (specification, implemented after the first release)

An export package is a directory (or a zip of it), decoupled from the SQLite table structure:

```text
mindweave-export/
  manifest.json          format name, format version, export time, app version, included workspaces
  workspaces/<id>/
    workspace.json       name, creation time
    conversations/<id>.json
    conversations/<id>/<message-id>.md   the text blocks of each message, for direct reading
```

- `manifest.json`: `{"format": "mindweave-export", "version": 1, …}`. A reader that meets a higher major version refuses to import and explains why; within the same major version, only optional fields are added.
- The conversation JSON stores stable IDs, title and branch source, message order, roles, states, retry relations, and the content listed by block (text blocks reference the `.md` files in the same directory; other blocks inline their structured data). Branches, anchors and summaries are added in the corresponding stages; anchors use (message ID, block index, offset within the block).
- **Not exported:** credential references, URLs and credentials in model snapshots, `ai_request` diagnostic records, runtime state (generation tasks, reading positions, drafts).
- Whether import generates new local IDs or keeps them is decided when E5 is implemented (D-021), based on the requirement for “importing into an existing workspace”.
- Imports from external tools (exports of other chat apps) are implemented as mappings to this format; they do not write to the database directly.
- A constraint in effect from now on: no outward output may serialize storage-layer records directly.

## E6 Performance baseline and architecture tests

- **Data generator** `PerfSeed`: writes through the real storage interfaces, so the data passes the same guards and table structure as production; content is determined only by sequence numbers and is repeatable. Shape `PerfShape(conversation count, rounds in the long conversation, answer length in characters)`; answers are Markdown with headings, lists, tables and code blocks. Branch trees and statistics have shapes too.
- **Benchmarks**: the sidebar list (budget 200 ms) and opening a long conversation (budget 300 ms), each the median of 5 runs after warm-up. The full scale (1,000 conversations, 200 rounds, 20,000 characters per answer) requires running `scripts/perf.sh` explicitly; regular tests run a small-scale smoke version that only checks that the generator and the read path work, without checking times.
- **Rendering benchmarks** (2026-11-06): displaying very long answers (about 30,000 and 100,000 characters of Markdown), opening them from the sidebar, and the cost of each update during generation, also run by `scripts/perf.sh`. Method, numbers and conclusions are in S2 (`docs/history/design/s2-branching.md` in the repository), “超长回答的渲染” (Rendering very long answers).
- **Architecture tests (`SeamTests`)**: only the request path (`GenerationRequests`, the model settings service, the search service's and the MCP servers' settings) and the credential store implementation may reference `ICredentialStore`; provider names must not appear in Core and Application.
- First full run (2026-10-27, macOS Release): generation 598 ms, list 29.7 ms, opening a long conversation 25.8 ms (about 4 MB of body text). Storage reads are far within budget; the budgets stay unchanged and serve to catch order-of-magnitude regressions. The main risk is in the rendering layer (layout and virtualization of very long Markdown), measured by the rendering benchmarks above.
