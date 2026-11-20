# Resource storage (D5 phase 1 foundation)

Basis: [evolution directions](../evolution.md) D5 "Direction formed in the 2026-11-13 discussion" and the development record `docs/history/agent-platform-discussion-20261113.md`. This document covers only the internal storage foundation: there is no interface, and the behavior the user sees does not change. The formal requirements and architecture text is drafted by another session (agent-docs); table names, column names and disk layout are already aligned with its draft. The places that still differ are listed in "Differences from the architecture draft" at the end; at merge time, the architecture confirmed by the user wins, and then this document and the code are changed.

## Concepts

- **Resource (`Resource`)**: a saved piece of material: a paste, a file, an image, and later web pages and tool output. A resource says "what it is".
- **Attachment (`AttachmentBlock`)**: a content block in a message that references a resource (a new kind added to the closed hierarchy of E2). An attachment says "when and where it entered the conversation".
- **Derived content (`ResourceDerivative`)**: content obtained from the original, one row per kind. This phase has only `text` (the extracted full text); digests, image descriptions and transcripts are added later in the same table, and chunks have a table of their own.

## Data (migration v15)

During development it temporarily used v14; the recommendation and summary link session (rec-association) merged first and took v14, so when this migration merged (2026-11-17) it moved to **v15**. The migration content is unchanged, and the test was renamed `SchemaV15MigrationTests`.

```text
resource             id, workspace_id, conversation_id?, origin, name?, media_type, byte_size, sha256,
                     trusted, source_url?, created_at
resource_derivative  id, resource_id, kind, producer, prompt_version?, text, estimated_tokens?, created_at
                     UNIQUE (resource_id, kind)
message_block        + resource_id? (REFERENCES resource ON DELETE RESTRICT)
conversation         + unique index (workspace_id, id), used by the composite foreign key of resource
```

- **Ownership.** A resource belongs to one workspace (`workspace_id`, CASCADE), and once attached in a conversation it also belongs to that conversation (`conversation_id`, CASCADE). The composite foreign key `(workspace_id, conversation_id) → conversation(workspace_id, id)` lets SQLite itself refuse ownership across workspaces. An empty `conversation_id` means not yet sent (a draft on the new exploration page can also create a resource first).
- **A resource belongs to only one conversation.** When writing a message that references a resource: the resource must be in the message's workspace; a resource without a conversation is assigned to that conversation; one that already belongs to another conversation is refused (`InvalidOperationException`, the whole write rolls back). When the same content is attached elsewhere, a new resource row is created, and the original is shared by hash and takes no extra disk space. This way, deleting a conversation or a workspace needs only cascades, with no need to look up references across conversations.
- **Deletion.** Deleting a conversation deletes the messages first (the blocks cascade with them, so `RESTRICT` no longer blocks), then the conversation; resources and derived content cascade with the conversation. Deleting a workspace works the same way, and unsent resources cascade with the workspace. The deletion SQL itself is unchanged. Deleting a single resource (used later by “转为文字” (convert to text)) is done only when no block references it.
- **`origin` and `kind` do not use CHECK enums.** Like message block kinds, they are validated by the application, so adding `web`, `tool`, `summary` or `caption` later does not require rebuilding the table. Current values: `origin ∈ {paste, file}`, `kind ∈ {text}`.
- **`name`** stores only the file name, not the path; it is NULL for a paste, which is displayed as “粘贴的文字” (pasted text).
- **Derived text is stored in SQLite** (retrieval and chunking come later, and block reads need to get it in the same snapshot); originals are not. `producer` records the extractor and its version (such as `plain-text/1`, `paste/1`). `prompt_version` and `estimated_tokens` are kept for model-generated derived content and the context budget; in this phase they are NULL.
- **`trusted`**: 1 for a paste or file the user chose themselves; later, web pages and tool output are 0 and are handled per D2.
- **Block rows.** An attachment block has `kind = 'attachment'`, `text` = the resource's display name (places that read only block text, such as sidebar excerpts, also see the name), and `resource_id` pointing to the resource; `data` is not used. On write only `resource_id` counts; the block's other fields are taken from the resource on read.

## Originals (blob)

```text
<data directory>/resources/<first two hash characters>/<full hash>     original, no extension, never changed after it is written
<data directory>/resources/tmp/<random name>                temporary file being written (tmp is not two hexadecimal digits, so it cannot be confused with a group directory)
```

- **Content-addressed.** The hash is the SHA-256 of the original's bytes, 64 lowercase hexadecimal characters; the `ContentHash` type and the CHECK on the table accept only this form, and the path is built from it, so it cannot leave the directory.
- **Atomic writes.** Write a temporary file, `Flush(true)` it to disk, then rename it to the final location; if a file with the same name and the same size is already there, it is duplicate content and the existing file is used (deduplication). A write failure (disk full, no permission) is a `StorageWriteException`, handled the same way as a database write failure.
- **Write the original first, then commit the database row.** If the row commit fails, it leaves only an unreferenced original, which cleanup deletes.
- **Cleanup (sweep).** An original is deleted when the current database **and** every kept database copy (backups in the backup directory, and `mindweave.db.replaced-*` left behind by a restore) no longer reference its hash. Reason: a full database backup is made before deleting a workspace or cascade-deleting a conversation; if the original disappeared right with the deletion, the backup could not restore the attachment.
  - A cleanup runs once after each successful deletion of a conversation, a workspace or an unsent resource (best effort; a failure is only logged and does not affect the deletion result);
  - After a backup is deleted or rotated, the originals only it referenced are deleted at the next cleanup; cleanup at startup is **not wired in** (see "Not in this phase");
  - When the database is not open, or one copy cannot be read, nothing is deleted in that round;
  - Each cleanup also deletes temporary files older than one hour; files in the directory that this store did not write are not touched.
- **Concurrency with creation.** Creation "pins" the hash before writing the original and releases it after commit; any hash pinned or released after a cleanup starts is not deleted in that round, and the check and the deletion happen under the same lock. The single-instance lock ensures there is only one process.

## Backup and restore

A backup is still only a `VACUUM INTO` copy of the database and does not copy originals: originals are immutable and shared by hash, and are kept as long as any copy references them, so after restoring any backup the originals it references are there, and the restore flow needs no change. The cost is that originals referenced by a backup take up space until the backup is deleted, which is consistent with the current state that "full database backups before deletion are never cleaned up automatically"; the storage page may show this usage later.

## Turning content blocks into text

`ContentBlocks` has two ways of turning blocks into text; when a message has only text blocks, both are the same as before:

| Use | Method | Text resource | Image |
| --- | --- | --- | --- |
| Context assembly (`Message.Text`) | `TextOf` | `<attachment name="…" type="…">` full text `</attachment>` | `<attachment name="…" type="image/png" omitted="image" />` |
| Titles, excerpts, interface display | `BriefOf` | `[附件：名称]` | `[附件：名称]` |

- The name attribute escapes `& " < >`; `</attachment` in the body (case-insensitive) is written as `<\/attachment`, so resource content cannot end its own section early.
- Title generation now uses `BriefOf`, so a whole document is not sent off to make a title.
- Whether to send image originals to the model is decided by the model capability `ImageInput` (E4), which belongs to the composer step; in this phase the context has only a placeholder.

## Application layer

`ResourceService` (`IResources`):

- `CreateFromPasteAsync(workspace, conversation?, text)`: the original is UTF-8 bytes, `text/plain`, and the derived `text` is the original text (line breaks normalized to `\n`).
- `CreateFromFileAsync(workspace, conversation?, path)`: decided by extension. Text types (`.txt`, `.md`, common source code and configuration formats) are decoded to text: UTF-8 / UTF-16 with a BOM, otherwise strict UTF-8, otherwise GB18030; containing NUL, failing to decode or having too many control characters means `NotReadable`. Images (PNG, JPEG, GIF, WebP) store only the original, and the type is taken from the file header (with a wrong extension it is still stored as its real type; if it is not an image it is refused).
- Other types return `Unsupported`. **Seam:** `ITextExtractor` (`Producer`, `CanExtract(media type)`, `Extract(bytes, media type)`). PDF and Office each add one implementation later and register it in `ApplicationServices`; storage and blocks do not change (the PDF and DOCX extensions must also be added to `FileTypes`).
- Limits: originals 32 MiB (`ResourceLimits`; the draft says 50 MB, see the differences); empty content is `Empty`; a file that cannot be opened is `FileUnavailable`.
- `GetAsync`, `OpenOriginalAsync`, `DeleteUnattachedAsync`; `ResourceService.BlockFor` gives the block that references a resource.

## Not in this phase

The composer's "+", long pastes automatically becoming attachments and “转为文字” (convert to text), attachments in drafts (the architecture draft has a `draft_attachment` table); sending images to the model; chunking and retrieval (`resource_chunk`, FTS5); web and tool resources; resources in the export package (E5); showing the originals' usage on the storage page; cleanup at startup; expiry cleanup of unsent resources (to be decided together with the composer).

> **Parts replaced from 2026-11-17 by migration v16 and [attachments](attachments.md):** the provenance mark (`provenance` replaces `trusted`), `origin = image` for images, the 50 MB / 20 MB limits, chunking and retrieval, draft attachments, cleanup at startup; cleanup no longer keeps originals because a backup references them, and instead copies the originals into the backup before deletion (Q4). The table below is the comparison as it stood for v15, kept as history.

## Differences from the architecture draft (2026-11-17, for comparison at merge time)

The agent-docs draft is in the shared note `agent-docs-fixed-details.md`. Already aligned: table names `resource`, `resource_derivative`; column names `name`, `byte_size`, `sha256`, `source_url`, `producer`, `prompt_version`, `estimated_tokens`; `message_block.resource_id` (RESTRICT); block kind `attachment` with `text` as the display name; the originals directory layout; pastes displayed as “粘贴的文字”; the GB18030 fallback. Still different:

| Item | This branch | Draft | Reason |
| --- | --- | --- | --- |
| Trust | `trusted` 0/1 | `provenance` (user / workspace / external) | The task description asked for a trust mark; a three-value provenance can be added as a column when needed, and `trusted` derived from it |
| `origin` values | `paste`, `file` | Also `image` | An image is a media type, not an origin; the origin of an image file is `file` |
| `workspace_id` | CASCADE | RESTRICT, deleted explicitly when deleting a workspace | CASCADE needs no change to the deletion code; the behavior is the same |
| Block `data` | Not used | `{resource_id}` | There is already a foreign key column; the JSON would duplicate it |
| Chunks in `resource_derivative` | None (separate table) | Same | — |
| Original size limit | 32 MiB | Files 50 MB, images 20 MB | Only a constant to change; waiting for requirements to decide |
| When cleanup runs | After deletion | After deletion and at startup | Cleanup at startup is not wired in |

## Tests

`ResourceStoreTests` (storage, deduplication, reading files by type and encoding, block reads and writes and ownership rules, reading in the same snapshot, cascade and original cleanup on conversation deletion, the restore round trip of the backup made before workspace deletion, cleanup after deleting a backup, cleanup concurrent with creation, temporary files and unreadable copies, no cleanup while the database is closed), `SchemaV15MigrationTests` (upgrading v14 data, constraints and cascades, rollback on failure), `ContentBlockTests` (turning into text, escaping, hash format).
