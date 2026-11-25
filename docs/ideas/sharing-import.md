# Sharing, step 2: import and continue (design note)

**Status:** working draft, in discussion with the owner since 2026-11-25. Not adopted. It is the short note that [sharing.md](../design/sharing.md) §8 asks for before step 2's code: it settles what that design left open for import. Where it differs from sharing.md, it says so, and sharing.md is corrected when this note is adopted. Decisions are the owner's unless marked as the writer's call.

## Findings in the code (2026-11-25)

- A branch must point at a source answer and a quoted sentence (`branch_source`: `source_message_id`, `quote` and the anchor are required). A structure-only share has neither.
- Messages are numbered per conversation (`message.seq`, unique in the conversation). Branch context (`branch_context_range.max_seq`) and summary coverage (`summary_version.to_seq`) refer to those numbers, so the exchange document carries the stored numbers and import keeps them.
- An attachment block must point at a stored resource; a citation must point at a resource and a passage in it. A share carries an attachment's name only, and a web citation's title and URL only.
- An answer normally has a generation task that records the model. An imported answer has none.
- A conversation's identity is unique across workspaces.

## Decisions

### I1 A structure-only import arrives as real, empty conversations (2026-11-25)

- Each dot of a structure-only share arrives as a conversation with its title and no messages. Branch lines are kept: a branch that came from an outline has no source sentence.
- Structure-only and with-content imports are then one mechanism: conversations, some with messages and some without. The timeline, the spatial view and the sidebar tree need no second kind of dot.
- When a share with content of the same conversations is imported later, the empty conversations gain their messages and the branches gain their source sentences (sharing.md S4: only stay the same or grow).
- Asking in an empty conversation makes it the user's own conversation on that topic.
- Cost: a migration that lets a branch exist without a source sentence, and every place that shows "where this left from" handles its absence. Empty conversations are shown as not explored yet.

Considered: a separate outline drawn as its own kind of dot (new storage, drawing and layout in both views; recommended nodes cannot be reused because they hang off a summary); an outline page (a page for one scenario, with the structure outside the Mindweave page).

This replaces the suggestion in sharing.md §7 that an outline be shown like recommended nodes.

### I2 A shared file only adds (2026-11-25)

- Importing a shared file never changes what is already here: not a conversation's title, not which summary version is current, not whether a conversation is archived.
- A summary version the app does not have arrives as a further saved version beside the user's own, never as the current one.
- The rule goes by the kind of file, not by who made it: a share only adds, the user's own share included. "The newer one wins" (sharing.md S4) holds only for device copies, in step 3.
- The stored time of a title's last change is therefore not needed in step 2; it moves to step 3's migration. This corrects sharing.md §6.6 point 7.
- Filling an empty conversation (I1) is adding: its messages and source sentences arrive; a title the user gave it stays.

## Open

2. What the recipient sees in place of what did not travel: attachments, citations, tool steps.
3. Trust: imported text as context for the recipient's model.
4. Entry points, the summary before writing, and removing an import.
