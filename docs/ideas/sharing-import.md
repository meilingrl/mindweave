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

### I3 The remainder (2026-11-25)

The owner accepted these together:

- **Addresses in imported text need approval before the model reads them.** A URL in a question the user typed is read without asking (requirements §10.4.5). A URL in an imported question or answer was written by someone else, so it counts like one the model wrote itself: the approval card appears.
- **Empty conversations show in the sidebar**, muted, as not explored yet. They are not hidden.
- **The tool count line travels with an imported answer.** The steps themselves do not exist on the recipient's side.
- **There is no special way to undo an import.** What arrived is deleted like anything else.
- **Stand-ins, the flow in the app, the failure cases and the building order** as in §3 to §8 below.

## 1. What import does

1. Read the data block of the shared file. The page is never run.
2. Check it: format name and version, the kind, well-formed identities, sizes within the app's own limits.
3. Compare every conversation, message and summary version with the store, by identity (§2).
4. Show what will happen, and let the user choose the workspace.
5. Write everything in one transaction.
6. Record where each conversation came from.

## 2. The rules of comparison

They carry out sharing.md S4 and I2. A share only adds.

**Conversations.**

| The file's conversation | What happens |
| --- | --- |
| Not in the store | It is created in the chosen workspace with its identity, name and start time. Its parent is the file's parent |
| In the store, in the chosen workspace | It is kept as it is. It may gain messages, a source sentence and a summary version (below) |
| In the store, in another workspace | It is reported as already here, with that workspace's name. Nothing of it is written, and its branches in the file are not imported either, since they would have no parent in the chosen workspace |

A name from the file is stored as a title the user did not write (the automatic kind), so the recipient's later renaming works as for any conversation. A conversation the file leaves unnamed arrives without a title.

**Messages.** The exchange document carries each message's stored number in its conversation.

- A message whose identity the store has is left as it is.
- A message the store lacks is added at its own number.
- **Two continuations.** If that number is held here by a different message, the conversation was continued differently on the two sides. Then nothing is added to the stored conversation. The file's version of it arrives as a separate conversation: the messages both sides share, then the file's own, with new identities derived from the file's identities and the stored conversation's, so that a second import finds them already here. It has the same parent as the original, says "another continuation" in its details, and carries the file's branches that left from the file's own messages.
- Which answer replaced which travels with the messages, so an imported conversation never shows two standing answers.

**Branch sources.**

- A branch whose conversation is new gets its source from the file: the source message, the quoted sentence, the position, and the stored anchor when the file has one. Without an anchor the app finds the quoted sentence in the answer when it is first shown.
- A structure-only file has no sources: the branch is stored with its parent only (I1).
- A stored branch without a source gains one when a later file has it. A stored source is never replaced.
- The frozen context of a branch (which of its ancestors' messages it may use) is rebuilt from the sources: up to the source message in the parent, and the parent's own ranges above it. A branch without a source has no context from its ancestors.

**Summaries.**

- A conversation without a summary gains the file's version as its current one.
- A conversation with a summary gains a version it lacks as a further saved version; the current one does not change (I2).
- A version the store has is left as it is. Everything arrives unread.

**Summary links** between two conversations are added when both have summaries here and no link joins them yet; notes come with a share with content.

## 3. What stands in for what did not travel

| In the original | On the recipient's side |
| --- | --- |
| An attachment | Its name, marked "not included in the share". It cannot be opened; the model is told only that an attachment was there |
| A web citation | Number, title and URL. It offers "open in browser" as today, and has no saved page to view |
| A citation of the sharer's own material | Plain text: "an attachment", "a summary", with the summary's title when it is in the share |
| Tool steps | The one count line |
| The model that wrote an answer | Nothing; the answer's details say it was imported |

## 4. Trust

- Imported text is drawn like any answer: no image is loaded, and HTML inside it is shown as text.
- When the user continues asking, imported messages are sent to the model as the conversation's history. That is what continuing means, and it is the same exposure as pasting someone's text into a question.
- The one new rule is in I3: a URL from an imported message is not "the user's own" for the web fetch approval. Tools with side effects already ask every time (requirements §10.4.6).
- Imported text is never read as a change to settings, tools or servers, as with any conversation content.

## 5. In the app

- **Entry:** dragging the file onto the window, or "Import…" in the workspace menu, through the app-modal file panel (D-040).
- **The dialog:** the share's title and kind; the workspace to import into, the current one or a new one named after the share; the counts of what is new, what gains messages, what is already here, what arrives as another continuation, and what is skipped because it lives in another workspace; Import and Cancel. When everything is already here, it says so and offers only Close.
- **After:** the app opens the first imported root conversation.
- **The mark:** a conversation's details say which shared file it came from and when it was imported. There is no list of imports.
- **Empty conversations** are muted in the sidebar and hollow in the Mindweave page, with "not explored yet" in their details. Opening one shows the input box and nothing else; a branch without a source shows no "return to where this left from".
- The dialog, the muted rows and the hollow dots wait for the owner's confirmation of their look.

## 6. Storage (one migration)

- `branch_source` allows a branch with a parent and no source message, quote or anchor. This rebuilds the table, so it is tried on a copy of the owner's data first and the owner decides.
- A record of each import (the share's title and kind, when it was imported) and, per conversation, which import brought it and whether it is another continuation of which conversation.
- Stand-ins for attachments and citations that have no stored material behind them. Whether these need columns or fit the blocks' own data is the builder's to settle.
- No stored time of a title's change (I2).

## 7. Failure cases, beyond sharing.md §9

| Case | What happens |
| --- | --- |
| An answer is being generated in a conversation that would gain messages | The dialog asks the user to finish or stop it first |
| A device copy before step 3 exists | Refused, saying a newer Mindweave is needed |
| Impossible contents: a malformed identity, a quote or a message over the app's limits, a number used twice in one conversation, a parent that is not in the file or the store | Refused as damaged; nothing changes |
| The user's own share, imported where it was made | Everything is reported as already here |
| A branch in the file whose parent is here only in another workspace | Skipped with its parent, and counted in the dialog |
| The chosen workspace is deleted while the dialog is open | The import is refused and says so |

## 8. Increments

- **2a: structure-only import.** The migration; empty conversations and branches without a source in the store, the sidebar, the Mindweave page and the conversation page; the entry points and the dialog; the import record; refusal of every other kind of file.
- **2b: import with content.** Messages at their numbers, sources, summaries and links; the stand-ins; filling empty conversations; another continuation; the approval rule for imported URLs; continuing to ask.

Each merge states how to try it, what was run and what was not verified.

## 9. Acceptance scenarios

2a:

1. A structure-only share imported into a new workspace: every dot is there with its title and its branch lines, in both views and in the sidebar, muted; no conversation has a message.
2. Asking in an empty branch conversation gives an ordinary answer; the conversation is no longer muted; it has no "return to where this left from".
3. The same file again: everything is already here, and nothing changes.
4. A damaged file, a file of another kind, a file in a newer format and a device copy are each refused with nothing changed.
5. The migration on a copy of the owner's data: every existing branch keeps its source, and the app's tests for returning to a source pass.

2b:

6. A share with content imported into a new workspace: questions, answers, source sentences and summaries are there; returning from a branch goes to its sentence; asking a further question continues from the imported messages.
7. A structure-only import, then the same share with content: the empty conversations gain their messages and sources; a title the user changed in between stays.
8. A newer share of an exploration imported earlier adds only the new conversations and messages.
9. The recipient asks in an imported conversation, then imports a newer share in which the sharer also continued it: the recipient's conversation is unchanged, and the sharer's continuation arrives once as a separate conversation under the same parent; importing again adds nothing.
10. A share whose conversation the recipient has renamed and summarized: the title and the current summary stay; the file's summary is a further saved version.
11. An imported answer contains a URL; the user asks the model to read it: the approval card appears.
12. An imported question with an attachment shows the name as not included; a web citation offers only "open in browser".
13. A file whose conversation lives in another workspace: it and its branches are skipped, and the dialog names the workspace.

## 10. Left to the builder

- The derivation of identities for another continuation, as long as a second import finds them.
- How the stand-ins are stored, and how a citation without stored material is modelled in Core.
- Whether the import record is one table or two.
- The muted and hollow looks, to be shown to the owner.
- Limits on file size and counts, set from the app's existing limits.

## 11. Text that changes when this note is adopted

| Where | Change |
| --- | --- |
| [sharing.md](../design/sharing.md) §6.6 point 7, §7 | "The newer one wins" and the time of a title's change belong to step 3 (I2); an outline is empty conversations, not directions like recommended nodes (I1) |
| Requirements §10.7, Import | Point 8 and its acceptance row: a structure-only share arrives as empty conversations the user can ask in; a shared file never changes a title or the current summary; the approval rule for imported URLs |
| Architecture §16.4, §16.5 | No separate outline; single-valued fields change only with device copies; the migration of §6 here |
| The exchange document | Each message's stored number and each summary version's coverage (asked of the 1b build on 2026-11-25) |
