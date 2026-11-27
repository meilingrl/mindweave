# Data relationships

Based on architecture §4.1 and §4.2. Following the development route, only the identities and boundaries that affect later table structure are settled first; specific fields become tables in the migration of the stage that needs them. Not all fields and indexes are frozen in advance.

## Stable identity

- An entity's primary key is a UUIDv7 (time-ordered; dependent rows use their owner's key, a composite key or a row number), stored in the database as lowercase hyphenated text. It is readable and consistent across tables, and its insert locality is better than random UUIDs.
- Branch sources, ancestor ranges and task inputs reference only stable IDs, never sequence numbers within a conversation or screen positions. Order within a conversation is guaranteed separately by a `(conversation_id, seq)` unique constraint.
- All times are stored as UTC Unix milliseconds, as integers.

## Branch source dependencies and frozen ancestor ranges

- `branch_source`: one per child conversation (the primary key is the child conversation's ID), with foreign keys to the parent conversation and the source message. The source message must belong to the parent conversation and be in a final state. A foreign key cannot express this, so it is checked inside the transaction that creates the branch. A branch that came from a structure-only share, and the user's own branch of an empty received conversation, have a parent and no source message; a continuation has a source message and no quoted sentence; a source from a file can have a sentence without a place or without an anchor (v19).
- `branch_context_range`: one row per ancestor conversation, recording the upper bound (by `seq`) of the message range that may be used. When C is created, all of B's rows are copied and a row for B itself is added. So later appends to an ancestor do not change the background, and queries need no recursion. An upper bound of -1 says that nothing of the ancestor is used; the row stays, because the tree, archiving and the deletion checks read it (v19).
- References may only point along the ancestor chain (the §4.2 invariant). So "has no child conversation" means "no one else references this conversation's messages", and the deletion check only looks at this conversation's own relationships.

## Version ownership

- Answer retry/regenerate: the new message records `supersedes_id`, only for the last answer in the conversation; the context takes "the latest attempt for each question".
- Summaries: `summary_node` is unique per source conversation and holds `current_version_id`/`pending_version_id`; version rows belong to the node. Deleting the last version deletes the node (summarizing again creates a new node).
- A context summary belongs to the conversation of the messages it covers, not to the requester.

## Transaction boundaries (tests are added one by one as each stage implements them)

| Transaction | Stage |
| --- | --- |
| Migration: structure change + data conversion + `user_version` commit together; roll back if the foreign key check fails | S0 |
| Create a workspace; record the last page | S0 |
| Send: user message + answer placeholder + generation task | S1 |
| Message final state (text, status, revision number) | S1 |
| Create a branch: child conversation + branch source + ancestor ranges | S2 |
| Adopt/discard/set as current/delete a version; the node is deleted with its last version | S4 |
| Delete a conversation, or a closed set of conversations with their summaries; delete a workspace; archive and restore | S6, item 3 |
| Import a shared file: the new workspace if one was chosen, the import, its conversations with their branch rows and ranges, their messages, sources, summaries and links | v19 |
| Ask in a received conversation: the user's own branch of it with its first question, and the draft's attachments | v19 |
| Save the places of explorations that are new or changed, when the network loads | v18 |

## Deletion boundaries

Messages are never deleted on their own. A conversation is deleted with all its descendants and their summary nodes as one closed set (architecture §4.2, §9.1): the scope is worked out again inside the transaction, which refuses when it changed or a user task is unfinished. When the set has branches, summaries or attachments, a backup is made first. A workspace is deleted with everything in it, after a backup.

Foreign keys are always on for the write connection. `RESTRICT` is kept on the core chain (conversation to workspace, message to conversation, branch source and ranges to the parent, summary node to conversation, task to conversation, message block to resource), so a missed check fails. Rows that only describe something else (exploration places, imports, drafts, reading, requests, steps, recommendations, links, suggestions, resources) go with it (`CASCADE`), and a reference that may outlive its target is set to null or stored as an identity without a key. `summary_node` and `summary_version` point at each other with deferred keys.

## Tables by migration

| Migration | Adds |
| --- | --- |
| v1 | `workspace`, `workspace_location`, `app_state` |
| v2 | `conversation`, `message`, `service_connection`, `model_config`, `generation_task` |
| v3 | `message_block` (replaces the message body), `ai_request`, capabilities |
| v4 | `branch_source`, `branch_context_range`, `reading_position`, `draft` |
| v5 | `context_summary` |
| v6 | `summary_node`, `summary_version`, `summary_read_state`, `summary_edit_draft` |
| v7 | the short name of a version; `node_layout` (not read or written since 0.6.0) |
| v8 | `conversation_archive` |
| v9, v10 | estimated tokens of a request; requests without a task |
| v11 | `recommendation` |
| v12 | indexes |
| v13 | `summary_link`, `link_suggestion` |
| v14 | `starting_recommendation` |
| v15 | `resource`, `resource_derivative`; resources in blocks |
| v16 | `resource_chunk` with its search table, `draft_attachment`, provenance, request purpose |
| v17 | `run_step`; the tools and limits of a task |
| v18 | `exploration_place` |
| v19 | `import`, `import_conversation`; `branch_source` and `branch_context_range` rebuilt for branches without a source |
