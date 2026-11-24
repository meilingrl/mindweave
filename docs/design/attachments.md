# Attachments (agent platform phase 1)

Basis: [requirements](../requirements.md) §10.4.3 and the phase 1 acceptance in §11, [architecture](../architecture.md) §15.2–15.4 and §15.9–15.11, and [questions to decide](../history/design/agent-platform-questions.md) (the user passed Q1–Q7 as recommended on 2026-11-17). Implemented on top of the [resource storage foundation](resources.md) (migration v15). This document records implementation-level choices and the differences from the architecture text; product behavior follows requirements and architecture.

## Storage (migration v16)

| Change | Notes |
| --- | --- |
| `resource.provenance` | `user` / `workspace` / `external`, replacing `trusted` (old 1 → `user`, 0 → `external`); the `origin` of images changes to `image` |
| `resource_derivative` rebuilt | Uniqueness changes to (resource, kind, producer, prompt version), so that digests can be reused per prompt version; adds `blob_sha256` (scaled image) and `truncated` (extracted text over 2 million characters was truncated; the viewer page says so) |
| `resource_chunk` + `resource_chunk_fts` | Chunks of about 800 estimated tokens, cut at paragraph or sentence boundaries; FTS5 uses `unicode61`, and the application splits CJK into adjacent character pairs (“机器学习” → “机器 器学 学习”); the question is split the same way, searched with OR and ranked by bm25; when chunks are deleted, a trigger deletes the index rows with them |
| `draft_attachment` | Each resource is in at most one draft (primary key `resource_id`); a conversation's draft is keyed by (conversation, viewer slot), and on the new exploration page the conversation is empty and the slot is `new-exploration`; deleted with the resource, conversation or workspace. On send, it is moved out of the draft in the same transaction that writes the message blocks |
| `generation_task.input_resources` | JSON: each attachment's sending form and the chunks taken; the note under the answer comes from this (the same approach as `input_summary_ids`) |
| `ai_request.purpose` | `answer` / `compression` / `resource_summary`; the statistics page lists attachment digests separately as “附件摘要” (attachment digests) |

An attachment in a conversation's draft carries the conversation (`resource.conversation_id`) from the moment it is created, and is cascaded when the conversation is deleted; an attachment on the new exploration page has no conversation before it is sent. At startup (in the background), resources that are in neither a message nor a draft are deleted, and then the originals are cleaned up.

**Differences from the text of architecture §15.2** (the user accepted items 1–3 on 2026-11-17, and the architecture text was changed with the merge to match the implementation; item 4 is left to phase 2):

1. The primary key of `draft_attachment` is `resource_id` and it carries `workspace_id`, rather than (`conversation_id`, `slot_key`, `ordinal`): the new exploration page has no conversation, and when the draft text is empty there is no `draft` row to reference.
2. A task's resource inputs are recorded in `generation_task.input_resources` (a JSON column); there is no separate table.
3. `resource.workspace_id` is still the `CASCADE` of v15 (the behavior is the same as "deleted with the workspace").
4. `produced_by_task` and `produced_by_step` are left to phase 2.

## Text extraction

`ITextExtractor` returns `TextExtraction` (text, or a reason: unreadable, no text layer, password-protected, too large). Plain text is in Application; PDF (PdfPig 0.1.16, **Apache-2.0**; the MIT written in the task description is wrong), DOCX (only System.IO.Compression and XmlReader, no new dependency) and images (SkiaSharp 3.119.4, already brought in by Avalonia) are in Infrastructure. Scanned-document rule: fewer than 4 non-whitespace characters per page on average means "no extractable text". Limits: files 50 MB, images 20 MB and no more than 50 million pixels, 10 attachments per question, PDFs at most 2,000 pages, extracted text at most 2 million characters (`ResourceLimits`).

## What gets sent (§15.4)

- **Message text contains only the attachment name.** `ContentBlocks.TextOf` writes an attachment as “[附件：名称]” ("[Attachment: name]"), and the attachment content is planned separately by the resource layer, so when compression cuts chunks, no single message exceeds the window any more (replacing the "single message over the window" problem of 2026-11-16). Over-long text messages from before phase 1 are not rewritten (Q3).
- **Planning** (the resource layer of `ContextPlanner.Plan`, `ResourceLayer`): if sending everything whole fits, everything is sent whole. Otherwise, in order: attachments of earlier questions switch to their digests (earlier images are no longer sent, and a one-line note is left); earlier conversation is compressed as before; among the attachments of the current question, the smaller ones are still sent whole within the average share, and the rest are given space by size and send "digest + chunks selected by keyword relevance" (at least one chunk, in original order, marked “第 n 部分” ("part n")). If it still does not fit, it is refused (“问题与必需来源本身超限” (the question and its required sources by themselves exceed the limit)), with no silent truncation.
- **Material format** (`ResourceMaterial`): an `<attachment name type form>` section, whose first line states “用户附上的材料《名称》的全文／摘要／摘要与相关部分” ("the full text / digest / digest and relevant parts of the material 《name》 attached by the user"). The material is placed in the turn of the question it belongs to, before the question text; when that message is compressed into a context summary, the material is attached after that context summary.
- **Attachment digests** are generated in the answer task's own execution slot (`ResourceContext.SummarizeAsync`), prompt version 1, not distinguished by model, generated only once. When a resource is longer than the model can read at once, digests are first made section by section from consecutive chunks and then merged (no digest is made of digests). After generation, planning runs once more with the real sizes. Failure is the same as a compression failure: the answer fails, the question is kept, and it can be retried.
- **Images** are sent only to models that declare `ImageInput`, as an `image_url` data URL. An image whose long side exceeds 2,048 pixels, or that is not PNG / JPEG, is first scaled / transcoded once and stored as a derived form; each image counts as 1,600 estimated tokens. When the model cannot see images, it is sent as “[图片：名称，当前模型不能查看]” ("[Image: name, the current model cannot view it]"), and when the original is missing as “[图片：名称，原件缺失]” ("[Image: name, original missing]"); Q7: no descriptions are generated for models that cannot see images.
- In the input of **knowledge summaries**, an attachment appears as its name plus its digest (the first 300 characters when there is no digest yet).

## Deletion and backup (Q4)

Deleting a conversation with attachments also makes a backup first (`IsCascade` counts attachments), and the confirmation dialog states the number of attachments. `<backup>.resources/` next to the backup stores the originals that this deletion removes. Cleanup looks only at the current database (and the old database left behind by a restore), and no longer keeps originals because a backup references them, so after deletion they are no longer in the data directory's `resources/`. After restoring from a backup, the first time an original is used it is put back from the backups' `.resources/`. Deleting a backup also deletes its originals directory; the backup size on the storage page includes the originals. Pre-migration backups do not copy originals (migration does not delete originals).

## Interface

- “+” at the left of the input box: “文件…” (File…), “图片…” (Image…) (grayed out when the current model cannot see images, with the menu stating the reason and the settings location 设置 › 模型连接 › 高级设置 (Settings › Model connection › Advanced settings)); drag in files or text; paste files, images or text.
- Pasted text estimated at ≥ 2,000 tokens (Q1, constant `ResourceLimits.LongPasteTokens`) becomes a “粘贴的文字” (pasted text) attachment; “转为文字” (convert to text) on the attachment bar, or ⌘Z / Ctrl+Z right after, puts it back at the original cursor position. Manual typing is never converted.
- Attachment bar: icon or thumbnail, name, type and size (text shows about how many characters); progress while reading, and the reason on failure (unsupported, over 50 MB, scanned document, password-protected, save failed and can be retried); sending is not possible while an attachment is not ready or has failed.
- After sending, the attachment bar is shown above the question body; a question with only attachments shows no empty bubble. Clicking opens the viewer page (a dialog covering the whole window; text can be selected and “复制全部” (copy all), images are shown at their original proportions, and it says so when the original is missing or the text was truncated). Phase 1 does not create branches from the viewer page (Q5).
- Under the answer: “附件较长，只发送了与问题相关的部分” (the attachment is long; only the parts relevant to the question were sent) or “较早的附件只发送了摘要” (only digests of earlier attachments were sent); clicking “发送了哪些” (what was sent) expands which parts of each attachment were sent.
- When the conversation has images and the current model cannot see images, a line above the input box says they are sent as text instead.
- New styles are in `Styles/Attachments.axaml`, and new icons are in a marked block at the end of `Icons.axaml` (done in parallel with the icon rework; existing control styles were not changed).

## Tests

`SchemaV16MigrationTests` (upgrading v15 data, constraints, cascades, triggers, rollback on failure), `ResourceLayerTests` (planning order, refusal, images, chunks, search terms), `AttachmentFlowTests` (the service-side scenarios of the phase 1 acceptance in §11: sent whole, only relevant parts sent and recorded when over capacity, digest generated only once without increasing the statistics, refusal, images by capability, drafts across restarts, a branch sees only its ancestors' attachments, missing originals, limits, knowledge summary input), `ResourceStoreTests` (backup before deletion carries originals, retrieval after restore, deleting a backup), `DocumentExtractionTests` / `ImageProcessorTests` (PDF, DOCX, images), `AttachmentUiTests` (attachment bar, long paste and undo, cannot see images, viewer page, attachments and notes after sending, drafts across restarts; outputs light and dark screenshots when `MINDWEAVE_SCREENSHOT_DIR` is set).
