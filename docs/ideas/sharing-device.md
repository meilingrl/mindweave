# Sharing, step 3: device copies (design note)

**Status:** working draft, in discussion with the owner since 2026-11-26. Not adopted. It is the short note that [sharing.md](../design/sharing.md) §8 asks for before step 3's code. It builds on [sharing-import.md](../design/sharing-import.md). Decisions are the owner's unless marked as the writer's call.

## What a device copy is

The user saves a copy of their work on one device and imports it on another, by whatever means they have (sharing.md S3). Unlike a share it is for the same user: it holds everything, and on import what it brings is the user's own, not read-only (sharing.md S4, S7; sharing-import.md I2, I4).

## Decisions

### V1 What one copy covers (2026-11-26)

- A device copy holds all workspaces by default. The user may untick workspaces before saving; this is a list with ticks, not a view of its own.
- On import nothing is chosen: each workspace goes to the same workspace on the other device, by identity, and is created there when it is missing.
- An attachment original the device already has is not copied again.

Considered: one workspace per copy (smaller files, but one save and one import per workspace every time).

### V2 What the other device added arrives as a branch where the two parted (2026-11-26)

The owner's three cases:

1. **Separate conversations.** Each device started new conversations. After the exchange both have all of them, in time order. Nothing conflicts.
2. **A line of branches.** Each device made a branch from the same conversation. After the exchange that conversation has both branches. Nothing conflicts.
3. **Questions inside one conversation.** Both devices kept asking in the same conversation. What the other device asked after the last question both share arrives as a continuation: a branch attached after that question (sharing-import.md I4), holding the other device's questions and answers.

- This replaces the "separate conversation" of sharing.md S4 for this case: the shared questions are not repeated, and the conversation's branches stay where they are. A branch that left from one of the other device's later answers hangs from the continuation.
- The other form, both devices' questions in one line by time, was set aside: each answer was written knowing only its own line, and with several questions on each side two trains of thought would be interleaved into something neither device saw.
- Each device keeps its own line as the conversation and shows the other's as the continuation, so the two devices' pictures differ in which is which. Nothing is lost on either side.
- A later copy adds the other device's further questions to that same continuation. Importing a copy again adds nothing.

### V3 The remainder (2026-11-26)

The owner accepted these together:

- **What was deleted here does not come back.** A conversation deleted on this device and still present in the other device's copy stays deleted; the import dialog counts it.
- **A backup is made automatically before a device copy changes anything already here** (a name, a current summary, an archived state, a deletion). An import that only adds makes none.
- **Every copy is complete.** There is no copy of only what changed. Import writes only what is new.
- **Usage statistics stay on each device.**
- **What travels and what stays, what updates, the file, the flow, the failure cases and the building order** as in §1 to §9 below.

## 1. What travels

| Travels | Stays on the device |
| --- | --- |
| Workspaces with their names | Keys, connections and their addresses |
| All conversations, archived ones included, with every finished question and answer and every attempt, and which answer replaced which | Model settings, tool settings, MCP servers |
| Attachments with their originals and extracted text; saved web pages, search results and tool results; the steps of a run | Drafts and reading positions |
| Branch sources as stored, with their anchors | Recommendations, link suggestions and what was ignored of them |
| Every saved summary version, which one is current, a summary's name; summary links with their notes | Usage statistics and request records |
| Whether a conversation is archived | Candidates of summaries, and edit drafts |
| Which summary versions were read: read on either device is read | The places of explorations in the spatial view |
| That a conversation was received from a shared file, with the share's title | The app's preferences: language, theme, text width |
| The model's name on an answer and on a summary version, as text | An answer still being generated |
| What was deleted (§4) | |

A received conversation stays read-only on both devices (sharing-import.md I4).

## 2. The rules of comparison

They extend sharing-import.md §2. A device copy is the user's own, so it may also update (§3) and carry deletions (§4).

**Workspaces** are matched by identity. One that is missing here is created, unless it was deleted here (§4).

**Conversations** are matched by identity. One that is missing here is created in its workspace, as the user's own or as received, as the copy says, unless it was deleted here. A conversation present here in another workspace than the copy says stays where it is: conversations do not move between workspaces.

**Messages.** A device copy carries every finished message, so both sides have complete lines.

- A line in the copy is matched to the line here that holds its first message.
- Where the copy's line only goes on from the end of the line here, its further messages are added, at their numbers.
- Where the line here goes on and the copy's does not, nothing is added.
- **Where the two part** (each has messages the other lacks after their last shared message), the line here is not changed. The copy's further messages become a **continuation**: a conversation attached as a branch after the last shared message, with no quoted sentence and with everything up to that message as its context (V2). Its messages keep their identities. Its own identity is derived from the conversation's and from its first message's, so the same continuation is found again on every later import.
- Two consequences of matching a line by its first message:
  - A later copy from the same device brings that device's further questions. Their line starts, after the shared part, with a message that is here in the continuation, so they are added to the continuation.
  - The other device's copy also holds *its* continuation, made of this device's questions. Its first message is here in the conversation itself, so no conversation is created for it; what the other device asked further in it is compared with the conversation here, by the same rules.
- A continuation's title is given by the device that shows it (the conversation's title and a short mark), and the user may rename it.

**Branches** are matched by the identity of their conversation. A branch follows its source message: its parent here is the conversation or continuation that holds that message here. Its context is built from the sources, as in sharing-import.md §2.

**Attachments and other stored material** are matched by identity, and their originals by content hash. An original that is already here is not written again.

**Summaries.**

- Versions are matched by identity; a version missing here is added.
- A version belongs to the line that holds the last message it covers. A version of the other device that covers messages which are here in a continuation becomes that continuation's summary.
- Read on either device is read.

**Summary links** are matched by the pair they join; a missing one is added.

## 3. What updates: the newer one wins

For these single values the copy also carries when each was last changed, and the newer one is kept:

- a conversation's title, with whether the user wrote it;
- a workspace's name;
- a summary's name, with whether the user wrote it;
- which saved version of a summary is current;
- whether a conversation is archived.

When neither side recorded a time (the value is older than this feature) and the values differ, this device's is kept. Times come from each device's clock.

## 4. Deletions

- From increment 3c on, the app records the identity of every workspace, conversation, summary, summary version and summary link that is deleted, with the time. Nothing else of it is kept. The record is not shown and needs no upkeep.
- A copy carries the record.
- **Deleted here, present in the copy:** it is not brought back. The dialog counts these and offers "bring back what I deleted here", off unless chosen; bringing something back removes its record.
- **Deleted there, present here:** the dialog lists what the other device deleted and offers to delete it here too. Deleting goes through the usual deletion (requirements §8.2): the scope is stated, a backup is made, running work is finished or stopped first. The app never deletes by itself. An offer the user declines is not repeated for the same deletion.
- Something changed here after the other device deleted it (a newer question in it, for example) is named as such in the offer.

## 5. Safety

- Before an import that will update or delete anything already here, the app makes a backup with the existing mechanism and says so in the dialog. If the backup fails, the import is not made.
- One import is one transaction for the database. Originals are written first, as attaching a file does (resources.md); if the transaction fails, the unreferenced originals are removed by the existing cleanup.
- The package is read entry by entry by known names. A path in it is never used as a path on disk, and every original is checked against its hash.
- The save dialog says that the file holds everything, attachments included, and is meant for the user's own devices.

## 6. The package

- One file with an extension of its own, a zip. It holds: the exchange document of kind `device`, split by workspace; the originals by content hash; and a readable form: an entry page and one page per workspace, drawn as a share with content is, so that after unpacking the copy can be read in a browser without Mindweave.
- The format is version 1 of seam E5, with the fields of §1 to §4 as optional additions.
- A reader refuses a higher major version, as for shares.

## 7. In the app

- **Save:** "Save a copy for another device…" in the workspace menu. The dialog lists the workspaces with ticks, all ticked (V1), states the size, and saves through the app-modal file panel (D-040). The suggested name is "Mindweave" and the date. Progress is shown, and saving can be cancelled; a cancelled or failed save leaves no file.
- **Import:** the same "Import…" and drop as for a share. For a device copy the dialog shows, per workspace: what is new, what gains messages, what is renamed or otherwise updated, what arrives as a continuation, what was deleted here and is not brought back, and what the other device deleted, with the offer of §4. It says that a backup will be made when one will. After importing it names what was done.
- A continuation shows in the sidebar and the Mindweave page as a branch of its conversation, with "continued on another device" in its details.
- The dialogs wait for the owner's confirmation of their look.

## 8. Failure cases

| Case | What happens |
| --- | --- |
| An original is missing or damaged on the saving device | The copy says so; the attachment arrives as "original missing", as the app shows it |
| An original in the package does not match its hash | It is treated as missing; the rest is imported, and the dialog says so |
| The disk fills while saving or importing | Saving leaves no file. Importing imports nothing |
| The copy is from a newer Mindweave | Refused, saying the app must be updated |
| The package is damaged or not a device copy | Refused; nothing changes |
| An answer is being generated in a conversation the import would change | The dialog asks the user to finish or stop it first |
| The devices' clocks disagree | "Newer" follows the clocks; the backup of §5 is the way back |
| The copy was made by another build channel or an older version of the same data | It is imported by the same rules; nothing depends on where it was made |

## 9. Increments

- **3a: save a device copy.** The exchange document of kind `device`, the package, the readable pages, the dialog.
- **3b: import that only adds.** New workspaces, conversations, messages that go on from the end of a line, originals, summary versions, links, read marks. Where the two devices differ in a way 3c handles (a different name, two lines that part, something deleted), 3b leaves this device as it is and counts it in the dialog as not merged yet. No migration. With 3a it already moves the user's work to a new device.
- **3c: both devices changed.** One migration: the times of §3 and the record of §4. Then the newer value wins, continuations, deletions both ways, and the backup before a change. The migration is tried on a copy of the owner's data first, and the owner decides.

Each merge states how to try it, what was run and what was not verified.

## 10. Acceptance scenarios

3a and 3b:

1. A device copy of all workspaces, imported into an empty data folder: every workspace, conversation, branch, summary with all its versions, summary link and attachment opens there; a branch returns to its sentence; a citation opens its saved page.
2. The same copy again: nothing changes.
3. More questions, a new branch and a new attachment on the first device; a new copy imported on the second: only these are added.
4. A copy with one workspace unticked brings nothing of it.
5. Unpacked, the copy's pages open in a browser with the network off.
6. The package contains no key, no connection address and no MCP server setting; a test plants markers in them and finds none.
7. A received conversation arrives received and read-only.
8. A damaged package, a package with a wrong hash, and a newer format behave as §8 says.

3c:

9. A conversation renamed on one device takes that name on the other; renamed on both, the later name wins.
10. Both devices ask further in the same conversation, then exchange copies both ways: each device keeps its own line, has the other's as one continuation attached after the last shared question, and a second exchange adds nothing. A branch from one of the other device's later answers hangs from the continuation and returns to its sentence.
11. After 10, more questions on one device and a new copy: they join the same continuation on the other.
12. A conversation deleted on one device: importing its copy on the other offers the deletion; declined, it stays and is not offered again; accepted, it is deleted with a backup.
13. A conversation deleted here and present in the other device's copy is not brought back; with "bring back" chosen it returns.
14. An import that renames something makes a backup first; when the backup cannot be made, nothing is imported.
15. The migration on a copy of the owner's data: nothing visible changes, and every existing value counts as older than any recorded change.

## 11. Left to the builder

- The layout of the package and the names in it, within E5.
- Whether context summaries (the compression of earlier messages) travel or are made again on the other device.
- How the readable pages are divided for a large workspace.
- How an answer without a local generation task shows its model's name (2b already shows received answers without one).
- The derivation of a continuation's identity.
- Limits on package size and counts, from the app's existing limits.

## 12. Text that changes when this note is adopted

| Where | Change |
| --- | --- |
| [sharing.md](../design/sharing.md) S4, S11, §6.5 | Two lines that part give a continuation, not a separate conversation (V2); what was deleted here does not come back; a copy holds all workspaces by default |
| Requirements §10.7, Device copies | All workspaces by default; the continuation; deletions both ways with "bring back"; the backup before a change; what travels and what stays; the acceptance rows |
| Architecture §16 | The package; matching a line by its first message; the times of single values and the record of deletions; the migration |
| [evolution-seams.md](../design/evolution-seams.md) E5 | The device kind of the exchange document |
