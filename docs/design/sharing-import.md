# Sharing, step 2: import and continue (design note)

**Status:** approved by the owner on 2026-11-25 (D-041). Increment 2a (§8) is built (0.6.18); 2b is not. It is the short note that [sharing.md](sharing.md) §8 asks for before step 2's code, and it settles what that design left open for import. Where the two differ, this note holds for import; sharing.md points here. Decisions are the owner's unless marked as the writer's call.

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
- Asking in an empty conversation starts the user's own conversation as a branch of it (I4).
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

### I4 A received conversation is read-only; the user's questions attach as new conversations (2026-11-25)

An independent read of this note against the code found that asking inside a received conversation puts two people's messages under one identity. A newer file from the sharer then collides with what the recipient asked, and the "separate continuation" of sharing.md S4 leaves the conversation's branches divided between two copies. The owner's decision, in the owner's words: a conversation that arrives by import is essentially read-only, and conflicts over the same conversation are resolved by attaching new nodes.

- No message is ever added to a received conversation by the user: no question, and no retry of an answer.
- The user can still read it, branch from any sentence in it, rename it, summarize it, archive it and delete it.
- A question typed at the end of a received conversation starts the user's own conversation as a **continuation**: a branch of it that left from its last message, with no quoted sentence, and with the whole conversation as its context. Asking in an empty received conversation starts a branch of it with no context.
- A newer file from the sharer therefore always only adds. Two continuations of one conversation cannot arise from shares.
- The rule of sharing.md S4 for two continuations (the other one arrives once, marked) stays for device copies, where both sides are the same user. It is worked out in step 3's note.
- Shared back, the user's continuations and branches arrive at the sharer as branches of the sharer's own conversations.

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

**Messages.** The exchange document carries each message's stored number in its conversation. A received conversation holds only messages that came from files (I4), so a newer file can only bring more of them.

- A message whose identity the store has is left as it is.
- A message the store lacks is added at its own number.
- If that number is held here by a different message, the file does not fit what is stored. This cannot come from sharing as designed. That conversation is skipped and counted in the dialog; nothing in it changes.
- A conversation that is the user's own (not received) never gains messages from a shared file. If a file holds messages for it that the store lacks, they are skipped and counted. Step 3 handles this for device copies.

**Branch sources.**

- A branch whose conversation is new gets its source from the file: the source message, the quoted sentence, the position, and the stored anchor when the file has one. The app can use the anchor only when the file was made with the same anchor version and render profile; then returning to the source marks the sentence. Otherwise returning goes to the source answer without marking the sentence. Finding the sentence afresh in the drawn answer is not part of step 2.
- A structure-only file has no sources: the branch is stored with its parent only (I1).
- A received branch without a source gains one when a later file has it. A stored source is never replaced.
- The frozen context of a received branch (which of its ancestors' messages it may use) is built from the sources: up to the source message in the parent, and the parent's own ranges above it. A branch without a source still has its row for every ancestor, saying that it may use nothing of it, so that it counts as a branch in both views, in archiving and in the deletion checks.
- When a source arrives later, the context of that branch and of the received branches below it is set from the file. The context of the user's own conversations is never rewritten: their answers were given with what they had.

**Summaries.**

- A conversation without a summary gains the file's version as its current one.
- A conversation with a summary gains a version it lacks as a further saved version; the current one does not change (I2). This includes a received conversation the user summarized themselves.
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
- The one new rule is in I3: a URL from a received conversation is not "the user's own" for the web fetch approval, also when it reaches a run through the context of the user's continuation or branch. Since a received conversation holds only received messages (I4), the mark on the conversation is enough. Tools with side effects already ask every time (requirements §10.4.6).
- Imported text is never read as a change to settings, tools or servers, as with any conversation content.

## 5. In the app

- **Entry:** dragging the file onto the window, or "Import…" in the workspace menu, through the app-modal file panel (D-040).
- **The dialog:** the share's title and kind; the workspace to import into, the current one or a new one named after the share; the counts of what is new, what gains messages, what is already here, and what is skipped (it lives in another workspace, or does not fit what is stored); the workspace offered first is the one that already holds most of the file's conversations; Import and Cancel. When everything is already here, it says so and offers only Close.
- **After:** the app opens the first imported root conversation.
- **The mark:** a conversation's details say which shared file it came from and when it was imported. There is no list of imports.
- **A received conversation** shows its messages without the actions that would add to it (retry). Its input box says that the question continues in a conversation of the user's own; sending creates the continuation and opens it. A continuation's "return to where this left from" goes to the end of the received conversation.
- **Empty conversations** are muted in the sidebar and hollow in the Mindweave page, with "not explored yet" in their details. Opening one shows the input box and nothing else; a branch without a source shows no "return to where this left from".
- The dialog, the muted rows and the hollow dots wait for the owner's confirmation of their look.

## 6. Storage (one migration)

- `branch_source` allows a branch with a parent and no source message (an empty received branch), and one with a source message and no quoted sentence (a continuation).
- `branch_context_range` can say that a branch may use nothing of an ancestor.
- Both tables are rebuilt in the one migration, so it is tried on a copy of the owner's data first and the owner decides.
- A record of each import (the share's title and kind, when it was imported) and, per conversation, that it was received and through which import.
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

- **2a: structure-only import.** The migration; empty conversations and branches without a source in the store, the sidebar, the Mindweave page and the conversation page; asking in an empty received conversation starts a branch of the user's own; the entry points and the dialog; the import record; refusal of every other kind of file.
- **2b: import with content.** Messages at their numbers, sources, summaries and links; the stand-ins; filling empty conversations; received conversations as read-only; continuing in a conversation of the user's own; the approval rule for received URLs.

Each merge states how to try it, what was run and what was not verified.

## 9. Acceptance scenarios

2a:

1. A structure-only share imported into a new workspace: every dot is there with its title and its branch lines, in both views and in the sidebar, muted; no conversation has a message.
2. Asking in an empty received conversation starts a conversation of the user's own as its branch and gives an ordinary answer there; the received one stays empty and muted.
3. The same file again: everything is already here, and nothing changes.
4. A damaged file, a file of another kind, a file in a newer format and a device copy are each refused with nothing changed.
5. The migration on a copy of the owner's data: every existing branch keeps its source, and the app's tests for returning to a source pass.

2b:

6. A share with content imported into a new workspace: questions, answers, source sentences and summaries are there; returning from a branch goes to its source answer, and marks the sentence when the file's anchor can be used. Typing a question at the end of a received conversation starts a continuation of the user's own whose answer builds on the received messages; the received conversation is unchanged and offers no retry.
7. A structure-only import, the user asks in an empty conversation, then the same share with content arrives: the empty conversations gain their messages and sources; the user's branch is unchanged and still hangs off its conversation; a title the user changed in between stays.
8. A newer share of an exploration imported earlier adds only the new conversations and messages.
9. The recipient continues from a received conversation, then imports a newer share in which the sharer also continued it: the received conversation gains the sharer's messages; the recipient's continuation is unchanged and keeps the context it had. Shared back, the continuation arrives at the sharer as a branch of their own conversation.
10. A share whose conversation the recipient has renamed and summarized: the title and the current summary stay; the file's summary is a further saved version.
11. A received answer contains a URL; in a continuation the user asks the model to read it: the approval card appears.
12. An imported question with an attachment shows the name as not included; a web citation offers only "open in browser".
13. A file whose conversation lives in another workspace: it and its branches are skipped, and the dialog names the workspace.

## 10. Left to the builder

- How the stand-ins are stored, and how a citation without stored material is modelled in Core.
- Whether the import record is one table or two.
- The muted and hollow looks, to be shown to the owner.
- Limits on file size and counts, set from the app's existing limits.

### Details settled while building 2a (builder's calls, 2026-11-26)

1. **The import record is two tables**: `import` (the share's title and kind, when) and `import_conversation` (which conversation came through which import; being in it is what "received" means). An import goes with its last conversation, by a trigger, so a title does not outlive what it named. An import that brings nothing leaves no record.
2. **"Nothing of an ancestor" is an upper bound of -1**, not an empty one: a reader that forgets the case reads no message, instead of all of them.
3. **`branch_source` also has `source_offset`**, the place in the answer's Markdown that §2 says a source from a file keeps. Nothing writes it in 2a; it is there so that the table is rebuilt only once.
4. **Asking in an empty received conversation** stores the new conversation, its branch row, the question, the answer and its task in one transaction. Attachments waiting on the received conversation's draft move with the question. The store refuses any message for a received conversation.
5. **Muted and hollow** mean: received and without a message. A branch the user made and has not asked in yet looks as before.
6. **The limits**: 50 MB (the largest attachment), 10,000 conversations, 200 conversations from a root to its deepest branch, 2,000 characters for a name, start times from 2000 to 2100. The depth bounds the context rows and the recursion of what walks the tree; it also counts what is here already, so several files together nest no deeper than one. A share's own title has no limit, as in the share dialog; what is kept of it is one line of at most 200 characters.
7. **A name arrives on one line**, as every title in the app is, and is never cut when it came from the app. A conversation the file leaves unnamed goes by the app's one naming rule, so it reads "New conversation".
8. **The workspace offered first** when none holds anything of the file is the new one: it keeps someone else's path apart, and deleting it removes what arrived. When a workspace holds some of it, that one is first, then the current one, then a new one.
9. **"Everything is already here"** is said when every conversation of the file exists, in whatever workspace; the dialog then names the workspaces and offers only Close. A file that brings nothing only to the chosen workspace keeps its choice of workspaces, with Import unavailable.
10. **A file dropped on an input box is an attachment**, as before. Anywhere else in the window one `.html` or `.htm` file is taken; more than one file, or another kind, is not. "Import…" offers the same kinds.
11. **Links between conversations are not added by 2a.** A structure-only share has no summary to link; a link between two conversations that are both here is 2b's, with the rest of the links.
12. **The page of an empty received conversation** has no branch header either. The user's own branch of one shows the path above it, without a sentence and without the way back.

### Details settled while building 2b (builder's calls, 2026-11-26)

1. **The stand-ins are message blocks and need no migration.** An attachment that did not travel is a block of its own with its name, an answer's tool counts are a block of their own, and a citation is the usual block without a resource and without a time. Their data is in the blocks' own `data`. A build before 2b that meets one of these blocks cannot show that conversation; the same was true when citations were added (web-search.md §2).
2. **What the model gets for such an attachment** is one line saying that there was one, without its name. A citation goes as "[n] title URL", or as its kind.
3. **A new answer directly after another one is its retry**, whichever file brought which, so an answer that was replaced later arrives as replaced. In the file, the earlier attempts of a question must name the one that stands.
4. **A conversation that does not fit** is one where a number is held by another message, or where a message or a summary version of the file belongs to another conversation here. Nothing of it is written, its summary included. A new one that does not fit takes its new branches with it, since they would have no parent. A conversation of the user's own with messages that are not here is also skipped whole.
5. **A new branch whose source message is not here** (its parent was skipped) arrives with its parent only. A later file can bring the source.
6. **The record of an import names the file a conversation first came through.** A file that only fills conversations leaves no record of its own.
7. **A continuation in the exchange document** has a source without a sentence: `quote` is optional. The shared page then names the conversation it left from and leads back to that message. A received branch's stored place (`source_offset`) travels on when it is shared again.
8. **Summary links** come with either kind of share, as §2 says; a structure-only one has no notes. They are counted in the dialog.
9. **"An answer is being generated in a conversation that would gain messages" (§7) cannot happen**: only received conversations gain messages, and none is ever generated in them. A summary the user is having written there when a file brings one becomes a candidate, as for any conversation with a summary.
10. **The statistics count the user's own questions.** Received ones are left out of the questions, the active days and the milestones.
11. **A continuation is listed by its title**, under the answer it left from; when the last message is a question without an answer, under the answer before it.
12. **The limits**: 200,000 messages in a file, 2,000,000 characters for an answer or a summary, the app's own limits for questions, quotes, anchors and attachments per question, and 1,000,000 for a message's number.
13. **A NUL character in a text becomes U+FFFD**, as Markdown reads it.

## 11. Text that changed with this note

The requirements and architecture rows were written as their own commit and confirmed by the owner on 2026-11-25 (requirements v0.30).

| Where | Change |
| --- | --- |
| [sharing.md](sharing.md) §6.6 point 7, §7 | "The newer one wins" and the time of a title's change belong to step 3 (I2); an outline is empty conversations, not directions like recommended nodes (I1) |
| Requirements §10.7, Import | A received conversation is read-only and the user's questions continue in a conversation of their own (points 4 and 7 and their acceptance rows; two continuations move to device copies); point 8: a structure-only share arrives as empty conversations; a shared file never changes a title or the current summary; the approval rule for received URLs |
| Architecture §16.4, §16.5 | No separate outline; received conversations are read-only and two continuations belong to device copies; single-valued fields change only with device copies; the migration of §6 here |
| [sharing.md](sharing.md) S4, §6.4 | Two continuations arise only between the user's own devices (I4) |
| The exchange document | Each message's stored number and each summary version's coverage (added in the 1b build on 2026-11-25) |
