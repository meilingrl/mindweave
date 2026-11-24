# Agent platform: questions for the user to decide

Basis: [requirements](../../requirements.md) §10.4 (v0.16) and [architecture](../../architecture.md) §15. The drafts are written according to the “recommendation” of each item below. If the user chooses otherwise, the corresponding paragraphs change to match. Under AGENTS.md, changes to the requirements and architecture are merged only after the user confirms them.

The first four items affect the implementation of phase 1; please look at them first. The rest only need a decision before the corresponding phase starts.

**Decided:** Q1–Q7 as recommended (user 2026-11-17; phase 1 is implemented on this basis); Q8–Q11 as recommended (user 2026-11-17, before phase 2 started). Q12: the first search service is **Tavily** (user 2026-11-17). Q14 is implemented in phase 3 as recommended (citations in summaries are text only), **waiting for the user's confirmation**; phase 3 also has new decisions W1–W16, see [web-search.md](web-search.md). Q15 is implemented in phase 4 as recommended (remote servers that require sign-in are not supported), accepted by the user 2026-11-19; phase 4 also has new decisions M1–M18 (accepted by the user 2026-11-19), see [mcp.md](mcp.md). Q11 has been implemented in phase 4 as “one call only”. Q13 and Q16 are decided before the corresponding phase starts.

## Before phase 1

| # | Question | Options | Recommendation |
| --- | --- | --- | --- |
| Q1 | How long must a long paste be to become an attachment? | By character count; by estimated tokens; make it a setting | **About 2,000 estimated tokens** (about 2,000 Chinese characters or 6,000 English characters), a fixed constant, not a setting. Estimated tokens treat Chinese and English alike; a paste shorter than this does not hurt reading when it stays in the input box. Adjust after trial use |
| Q2 | Which files does phase 1 support? | Plain text only; add PDF; also add DOCX | **Plain-text types + PDFs with a text layer + DOCX**. PDF and DOCX need an open-source extraction library (for example PdfPig, Apache-2.0 license), referenced only in Infrastructure; no recognition of scanned documents |
| Q3 | What about existing very long text messages (pasted before phase 1)? | Leave them; turn them into attachments during migration; treat them as resources temporarily during planning | **Leave them**. Sent messages cannot be rewritten, and a migration that rewrites them would conflict with this rule. These messages still follow the current rules (when too long, the app suggests branching from them); new pastes no longer produce such messages |
| Q4 | Does the backup before deleting a conversation include attachment originals? | Include the originals that will be deleted; do not include them; never delete originals automatically | **Include the originals that will be deleted**. Otherwise, after restoring from the backup, attachments would have only metadata left. Only the originals inside this deletion's scope are copied, so the backup does not become very large |

## Can be decided during phase 1

| # | Question | Options | Recommendation |
| --- | --- | --- | --- |
| Q5 | Can the user select text in the attachment viewer to create a branch? | Already in phase 1; add in phase 2; not at all | **Not in phase 1; consider it in phase 2 together with tool results**. In requirements §6.2 the branch source is an AI answer; branching from material the user attached is a new kind of source and needs a “resource anchor”. Phase 1 first provides selection and copying |
| Q6 | Are the size limits for attachments and drafts (50 MB per file, 20 MB per image, 10 attachments per question) appropriate? | Adopt; larger; smaller | **Adopt**, all as adjustable constants. The data directory grows with attachments; the statistics page may later show the space attachments use |
| Q7 | Should the app generate descriptions of images for models that cannot see images (using another model that can)? | No; an optional “image description model” | **Not in phase 1**. This needs a second model configuration, and the settings UI tied to “one active configuration in the first release” would need a separate design |

## Phases 2–5

| # | Question | Options | Recommendation |
| --- | --- | --- | --- |
| Q8 | What are the numbered markers in answers called? | 引用 (quote); 来源 (source); 出处 (citation) | **出处** (citation). “来源” is already used for anchors, lineage and “返回来源” (Back to source); “引用” is reserved for D7, questions that quote several places; 出处 refers only to the material an answer is based on |
| Q9 | Are the default run limits (8 model requests, 16 tool calls, 5 minutes, 200,000 estimated tokens in total) right? | Adopt; more conservative; wider | **Adopt**, changeable in 设置 › 工具 (Settings › Tools). When a limit is reached, ask the model to answer from the results it already has, instead of failing outright |
| Q10 | Does fetching a URL that has not appeared in search results, user messages or already fetched pages need approval first? | Needs approval; allow directly; not allowed | **Needs approval**. This is the main defense against the model encoding conversation content into a URL to send it out; normal searching and reading are not affected |
| Q11 | Does one approval of a tool with side effects cover only one call, or can it allow “all calls in this run”? | One call only; optionally this run; optionally always allow | **Phase 4 starts with one call only**. After use, see whether it is too annoying, then add “本次运行内允许这个工具” (Allow this tool for this run); no “always allow” |
| Q12 | Which search service is connected first? | The user picks one; make several selectable | **Connect one first**, with only one adapter; the choice depends on which service the user has a key and quota for. Several selectable services wait until there is a second need |
| Q13 | Should an embedding service be introduced for semantic retrieval? | No; introduce it in phase 2; make it optional | **No**; use local FTS5 keyword retrieval. Embeddings need an extra service and cost, and not every provider offers them; evaluate again when retrieval quality is not good enough |
| Q14 | Do citations in summaries stay clickable links? | Keep them; text only | **Text only** (phase 3). Summary versions can be edited and regenerated, so maintaining links needs separate rules; add them later if needed |
| Q15 | Are MCP remote servers that require sign-in (OAuth) supported? | Supported in phase 4; later | **Later**. Phase 4 supports local commands and URLs that need no sign-in or use a fixed key |
| Q16 | Chart format for phase 5? | Mermaid + tables; add a chart description format | **Do Markdown tables and Mermaid first**; compare chart description formats separately before implementation (they must render locally in Avalonia without running code) |

## Alignment with parallel sessions

The following details are fixed in the drafts, for the session implementing phase 1 storage and the session exposing model capabilities to check against; in a conflict, merge order and the user's decision win:

- Tables `resource`, `resource_derivative`, `resource_chunk`, `resource_chunk_fts`, `draft_attachment`; `message_block.resource_id`; `ai_request.purpose`. Original file path `resources/<sha256 前 2 位>/<sha256>` (the first 2 characters of the sha256), temporary directory `resources/tmp/`.
- Content block type strings: `attachment` (phase 1), `tool_call`, `tool_result` (phase 2), `citation` (phase 3), `artifact` (phase 5).
- Long-paste threshold 2,000 estimated tokens; each image counts as 1,600 estimated tokens and is scaled before sending so its long side is at most 2,048 pixels.
- The only capability phase 1 uses is the existing `ImageInput`; phase 2 uses `ToolCalls`; phase 5 adds `AudioInput`.
