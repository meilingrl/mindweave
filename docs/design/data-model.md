# Data relationships

Based on architecture §4.1 and §4.2. Following the development route, only the identities and boundaries that affect later table structure are settled first; specific fields become tables in the migration of the stage that needs them. Not all fields and indexes are frozen in advance.

## Stable identity

- Every entity's primary key is a UUIDv7 (time-ordered), stored in the database as lowercase hyphenated text. It is readable and consistent across tables, and its insert locality is better than random UUIDs.
- Branch sources, ancestor ranges and task inputs reference only stable IDs, never sequence numbers within a conversation or screen positions. Order within a conversation is guaranteed separately by a `(conversation_id, seq)` unique constraint.
- All times are stored as UTC Unix milliseconds, as integers.

## Branch source dependencies and frozen ancestor ranges

- `branch_source`: one per child conversation (the primary key is the child conversation's ID), with foreign keys to the parent conversation and the source message. The source message must belong to the parent conversation and be in a final state. A foreign key cannot express this, so it is checked inside the transaction that creates the branch.
- `branch_context_range`: one row per ancestor conversation, recording the upper bound (by `seq`) of the message range that may be used. When C is created, all of B's rows are copied and a row for B itself is added. So later appends to an ancestor do not change the background, and queries need no recursion.
- References may only point along the ancestor chain (the §4.2 invariant). So "has no child conversation" means "no one else references this conversation's messages", and the deletion check only looks at this conversation's own relationships.

## Version ownership

- Answer retry/regenerate: the new message records `supersedes_message_id`, only for the last answer in the conversation; the context takes "the latest attempt for each question".
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
| Adopt/discard/set as current/delete a version; the node is deleted with its last version, and its layout with it | S4 |
| Delete a conversation with no dependents; archive and restore | S6 |
| Save layout (revision number prevents rollback) | S5 |

## Deletion boundaries

Messages are never deleted on their own. Deleting a conversation is allowed only when it has no child conversations, no summary node and no user task; its other dependent rows are deleted in the same transaction. Foreign keys are always on for the write connection. `ON DELETE` is used only for purely dependent rows (reading position, drafts, layout); references across entities are always `RESTRICT`, so a missed check fails at the database layer instead of silently cascading.
