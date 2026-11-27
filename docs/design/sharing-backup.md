# Sharing, step 3: the manual backup (design note)

**Status:** approved by the owner on 2026-11-26 (D-042), not built. It is the short note that [sharing.md](sharing.md) §8 asks for before step 3's code. It builds on [sharing-import.md](sharing-import.md). Where it differs from sharing.md, this note holds for manual backups; sharing.md points here. Decisions are the owner's unless marked as the writer's call.

## What a manual backup is

Until 2026-11-27 this note and the documents around it called it a device copy (V5).

A complete copy of the user's work in one file. The user saves it on one device and can import it on that device again, or on another, by whatever means they have (sharing.md S3). Unlike a share it is for the same user: it holds everything, and what it brings is the user's own, not read-only (sharing.md S4, S7; sharing-import.md I4).

**It is a backup, not a sync** (V4). Importing a copy adds what the device lacks. It does not try to make two devices alike.

## Decisions

### V1 What one copy covers (2026-11-26)

- A manual backup holds all workspaces by default. The user may untick workspaces before saving; this is a list with ticks, not a view of its own.
- On import nothing is chosen: each workspace goes to the same workspace on the device, by identity, and is created there when it is missing.
- An attachment original the device already has is not copied again.

Considered: one workspace per copy (smaller files, but one save and one import per workspace every time).

### V2 Questions asked on both sides join into one line, by time (2026-11-26)

The owner's cases:

1. **Separate conversations.** Each side started new conversations. After the import the device has all of them. Nothing conflicts.
2. **A line of branches.** Each side made a branch from the same conversation. After the import that conversation has both branches. Nothing conflicts.
3. **Questions inside one conversation.** Both sides kept asking in the same conversation. The owner's rule: merge by timestamp. The questions join into one line in the order they were asked, and it stays one conversation.

How this was reached: the owner first chose, on the writer's recommendation, that the other side's questions arrive as a branch attached after the last shared question. An independent read against the code showed that the same questions would then be filed under different conversations on different devices, so that deleting or renaming one side's would not meet the other's, and that a retried answer would have no place. The owner's first form, one line by time, has neither fault.

What it costs: when the user alternated between devices in one conversation, the questions interleave by time, and an answer can follow a question it did not know. A summary made on one side does not include what the other side asked before its end; it then shows the existing mark "later content not included" (§2).

### V3 The remainder (2026-11-26)

The owner accepted these, of which V4 then withdrew two (marked):

- **Every copy is complete.** There is no copy of only what changed. Import writes only what is new.
- **Usage statistics stay on each device.**
- *Withdrawn by V4:* what was deleted here does not come back; a backup is made before a copy changes anything here.

### V4 A backup, not a sync (2026-11-26)

The owner's question: why solve consistency at all, when this is a backup and not a sync? The writer had read sharing.md S3 ("sync across multiple devices") as the goal and designed for two devices that both change and must end up alike. The owner's decision:

- A manual backup is a backup that can also be carried to another device. Importing one only adds: nothing already here is changed, and nothing is deleted (the owner's rule of sharing.md S4: only stay the same or grow).
- "The newer one wins" is dropped. A title, a name, a current summary or an archived state that differs keeps the value this device has.
- Deletions are not recorded and do not travel. What was deleted here and is in the copy comes back: that is what restoring a backup is for. This withdraws the deletion offer of sharing.md S11.
- A conversation continued on both sides is joined by time (V2).
- Step 3 therefore has no migration.

Not goals: that two devices show the same thing after exchanging copies; that a deletion or a rename on one device reaches another.

### V5 Sharing and the manual backup are one operation; sync is something else (2026-11-27)

The owner's clarification, which the documents follow from here on:

- **One operation.** Sharing and the manual backup are the same operation: export to a file, import from a file. Only the scope and what import may do differ: a share is a selection, filtered, and what it brings is read-only; a manual backup is everything, and what it brings is the user's own.
- **Not the app's automatic backups.** The backups the app makes by itself (before a deletion, a data upgrade or a restore) are versions of the app's own data, kept inside its data folder. If that data is lost, they are lost with it, however many there are. A manual backup is a file the user keeps, largely independent of the app.
- **Not sync.** Practical sync goes through a network: a shared cloud, server or repository. A file-based backup can be made to behave like sync by a careful user who accepts the conflicts and the trouble, but the product does not ask that of the user and does not call it sync. It is called the manual backup.
- **Sync stays a vision.** Real sync needs a server side and a client side, which is another architecture. The manual backup is not declared to have achieved it, and Mindweave does not rule out offering a server and good sync later ([evolution.md](../evolution.md) D10).
- **Names.** In documents: manual backup (手动备份), automatic backup (自动备份) for what the app makes by itself, and sync only for networked sync. The stored word for the kind of file stays `device` (the exchange document's kind, and the `import` table's check since migration v19): a stored name does not follow a rename.

## 1. What a copy holds

| In the copy | Not in the copy |
| --- | --- |
| Workspaces with their names | Keys, connections and their addresses |
| All conversations, archived ones included, with every finished question and answer and every attempt, and which answer replaced which | Model settings, tool settings, MCP servers |
| Attachments with their originals and extracted text; saved web pages, search results and tool results; the steps of a run | Drafts and reading positions |
| Branch sources as stored, with their anchors | Recommendations, link suggestions and what was ignored of them |
| Every saved summary version, which one is current, a summary's name; summary links with their notes | Usage statistics and request records |
| Whether a conversation is archived; which summary versions were read | Candidates of summaries, and edit drafts |
| That a conversation was received from a shared file, with the share's title | The places of explorations in the spatial view |
| The model's name on an answer and on a summary version, as text | The app's preferences: language, theme, text width |
| | An answer still being generated |

## 2. What import does

It extends sharing-import.md §2. Everything is matched by identity; what is here is never changed, with the one exception of the numbering in V2's case.

**Workspaces.** One that is missing is created with its name. One that is here keeps its name.

**Conversations.** One that is missing is created in its workspace as the copy has it: its title, its archived state, and whether it was received from a shared file. One that is here keeps its title and state, and may gain messages, branches and summary versions. A conversation that is here in another workspace than the copy says stays where it is, and is counted in the dialog.

**Messages.**

- A message that is here is left as it is. A message that is missing is added.
- When the missing messages all come after the end of the line here, they are added after it.
- **When both sides have messages the other lacks** (V2), the conversation becomes one line by time:
  - a question keeps its answers with it, attempts included, so the line is ordered by when each question was asked;
  - a further attempt at an answer both sides have goes with that answer's question;
  - each side's own order is kept; questions asked at the same instant are ordered by identity.
- The messages of that conversation are then numbered anew. Whatever refers to a place in it by number follows the message it meant: the context of branches that left from it, and the coverage of its summaries.
- A summary version covers only as far as the line goes without a message it did not know. Where a question from the other side now lies before its end, its coverage ends before that question, so the conversation shows "later content not included".
- The same copy imported again adds nothing and numbers nothing anew.
- A received conversation is read-only everywhere, so a copy can only bring more of it, never two lines.

**Branches** are matched by the identity of their conversation and arrive with their source as stored. Their context is built from the sources, as in sharing-import.md §2.

**Attachments and other stored material** are matched by identity, and their originals by content hash.

**Summaries** are matched by their conversation.

- A conversation without a summary here gains the copy's, with all its saved versions, its name and its current version.
- A conversation with a summary here gains the versions it lacks as further saved versions. Its name and its current version do not change.
- Read marks arrive with the versions that are new here.

**Summary links** are matched by the pair they join; a missing one is added when both conversations have summaries here.

## 3. Safety

- One import is one transaction for the database. Originals are written first, as attaching a file does (resources.md); if the transaction fails, the unreferenced originals are removed by the existing cleanup.
- The package is read entry by entry by known names. A path in it is never used as a path on disk, and every original is checked against its hash.
- The save dialog says that the file holds everything, attachments included, and is meant for the user's own keeping and devices.
- No backup of the database is made before an import, because an import only adds.

## 4. The package

- One file with an extension of its own, a zip. It holds: the exchange document of kind `device`, split by workspace; the originals by content hash; and a readable form: an entry page and one page per workspace, drawn as a share with content is, so that after unpacking the copy can be read in a browser without Mindweave.
- The format is version 1 of seam E5, with the fields of §1 as optional additions.
- A reader refuses a higher major version, as for shares.

## 5. In the app

- **Save:** "Back up to a file…" in the workspace menu. The dialog lists the workspaces with ticks, all ticked (V1), states the size, and saves through the app-modal file panel (D-040). The suggested name is "Mindweave" and the date. Progress is shown, and saving can be cancelled; a cancelled or failed save leaves no file.
- **Import:** the same "Import…" and drop as for a share. For a manual backup the dialog shows, per workspace, what is new, what gains messages, what is joined by time, and what is already here. After importing it names what was done.
- The dialogs wait for the owner's confirmation of their look.

## 6. Failure cases

| Case | What happens |
| --- | --- |
| An original is missing or damaged on the saving device | The copy says so; the attachment arrives as "original missing", as the app shows it |
| An original in the package does not match its hash | It is treated as missing; the rest is imported, and the dialog says so |
| The disk fills while saving or importing | Saving leaves no file. Importing imports nothing |
| The copy is from a newer Mindweave | Refused, saying the app must be updated |
| The package is damaged or not a manual backup | Refused; nothing changes |
| An answer is being generated in a conversation that would gain messages | The dialog asks the user to finish or stop it first |
| The devices' clocks disagree | The order of V2's line follows the clocks; nothing is lost |

## 7. Increments

- **3a: save a manual backup.** The exchange document of kind `device`, the package, the readable pages, the dialog.
- **3b: import a manual backup.** Everything in §2.

Neither has a migration. Each merge states how to try it, what was run and what was not verified.

## 8. Acceptance scenarios

3a:

1. A copy of all workspaces is saved; a copy with one workspace unticked holds nothing of it.
2. Unpacked, the copy's pages open in a browser with the network off.
3. The package contains no key, no connection address and no MCP server setting; a test plants markers in them and finds none.

3b:

4. A copy imported into an empty data folder: every workspace, conversation, branch, summary with all its versions, summary link and attachment opens there; a branch returns to its sentence; a citation opens its saved page; a received conversation is received and read-only.
5. The same copy again: nothing changes.
6. More questions, a new branch and a new attachment on the first device; a new copy imported on the second: only these are added.
7. A conversation renamed, archived or given another current summary on one side keeps this device's value after the import.
8. A conversation deleted here and present in the copy comes back.
9. Both sides ask further in the same conversation; the copy is imported: the conversation is one line in the order the questions were asked, each answer with its question; a branch from any of those answers still returns to its sentence and keeps its context; importing the other way gives the same line on the other device; importing again changes nothing.
10. In scenario 9, one side had retried the last shared answer: the retry sits with that answer's question, and the later attempt stands.
11. In scenario 9, a summary made on one side shows "later content not included" where the other side's question lies before its end.
12. A damaged package, a package with a wrong hash, and a newer format behave as §6 says.

## 9. Left to the builder

- The layout of the package and the names in it, within E5.
- The numbering anew of V2's line, as long as both sides reach the same line and a second import changes nothing.
- Whether context summaries (the compression of earlier messages) travel or are made again; those of a conversation joined by time are made again.
- How the readable pages are divided for a large workspace.
- How travelled answers and run steps are stored without a local generation task (2b already shows received answers without one).
- Limits on package size and counts, from the app's existing limits.

## 10. Known limits

- A workspace exists once per device it was created on. Two devices that were each used before have their own first workspaces; a copy brings the other one beside this one, and the two are not joined.
- After V2's joining, the numbers of that conversation's messages differ from those in a share made earlier from one side. A recipient who imported that share and later imports a newer one may find that conversation skipped as not fitting (sharing-import.md §2).

## 11. Text that changed with this note

The requirements and architecture rows were written as their own commit and confirmed by the owner on 2026-11-27 (requirements v0.31).

| Where | Change |
| --- | --- |
| [sharing.md](sharing.md) S3, S4, S11, §6.5 | A manual backup is a backup that only adds; no "newer one wins", no deletion offer, no separate conversation for two continuations: they join by time |
| Requirements §10.7, Manual backups | All workspaces by default; only adds; joined by time; deleted things in a copy come back; what a copy holds; the acceptance rows |
| Architecture §16 | The package; joining by time and numbering anew; no migration for manual backups; the times of single values and the record of deletions are dropped |
| [evolution-seams.md](evolution-seams.md) E5 | The device kind of the exchange document |
