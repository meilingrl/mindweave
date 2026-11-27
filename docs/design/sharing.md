# Sharing a thinking path (design)

**Status:** approved by the owner on 2026-11-25 (D-039). Step 1 (§12) is built: 1a in 0.6.13, 1b in 0.6.15. Step 2 is built: 2a in 0.6.18, 2b in 0.6.19. Step 3 is not. Discussed with the owner on 2026-11-24 and 11-25; every decision in §4 is the owner's unless it says "writer's call". It carries out [evolution.md](../evolution.md) D8 in the 0.6.x cycle. The requirements and architecture text that follows from it is listed in §13; the owner confirmed it on 2026-11-25.

Related: [session-network.md](session-network.md) (every conversation is a dot; the timeline), [evolution-seams.md](evolution-seams.md) E5 (the exchange format), [attachments.md](attachments.md), [web-search.md](web-search.md), [reasoning-and-status.md](reasoning-and-status.md), requirements §8.3 (export) and §10.1 (full import and export, sync). The page's look: [prototypes/share-page](../../prototypes/share-page/README.md).

## 1. Intent

The owner's judgment (D8): answers are cheap; what is worth keeping, and worth showing someone else, is the thinking: what was asked, how one question led to the next, where things became clear.

Sharing lets the user hand a path of questions to someone else as one file. The reader needs no Mindweave, no account and no server. A reader who does use Mindweave can bring the file in and keep asking from it. The same mechanism carries the user's own work between their devices, through cloud storage they already have.

The five scenarios in D8 (classmates, companions on a trip, students or family, a partner, an advisor) explain the value. None of them gets a feature of its own.

## 2. Goals, non-goals, anti-goals

**Goals**

1. One file that anyone can open and follow without having been there.
2. The default sends no message text. Content leaves only when the user chooses it.
3. What the page shows is everything the file contains.
4. A Mindweave user can import a shared file and continue asking.
5. A user can move their work between their own devices by saving and importing a file.
6. Import and export only keep or add. Nothing is deleted automatically, and the same thing is never copied (owner).

**Non-goals for this cycle (§11)**: hosted links, accounts, a community, automatic sync, attachment contents in a share, a record of shares.

**Anti-goals: failures even if the code works**

- A feature, view or mode per scenario (D8).
- A shared file that contains anything the user did not see listed before saving.
- An import that removes or duplicates the user's existing work.
- A list of past shares the user has to manage.
- A page that loads anything from the network.

## 3. Constraints

- **No server** (D8). The app uploads nothing; the user moves the file.
- **Nothing that leaves the app serializes stored records** (E5). The share has a document model of its own, as the summary export has (`SummaryExportDocument`).
- **Reasoning is never saved**, so it cannot be shared.
- **Stable identities exist**: conversations, messages, summary nodes and versions have version-7 GUIDs. Finished messages never change.
- **A conversation is one line of messages.** Two different continuations cannot be joined into one.
- **Names** come from the architecture §2.3 glossary. It has `Share`; the new terms need rows first (§13).
- **Migrations of real data** are tried on a copy first and the owner decides (AGENTS.md). Step 1 needs none.

## 4. Decisions

### S1 What one share is (2026-11-24)

- A share is a set of conversations from one workspace.
- The selection starts as the current exploration: the root conversation and all its branches. The user unticks conversations, or ticks further explorations.
- A ticked branch conversation always brings its ancestors, so the reader can see where each branch left from.
- A share never crosses workspaces.
- The picker is part of the share dialog and shows the same dots as the timeline view, with ticks. It is not a view of its own.

### S2 The form of the result (2026-11-24)

- The result is one self-contained HTML file. It loads nothing from the network: styles, scripts, formula drawing and the picture of the path are inside the file.
- It travels as a file, by whatever means the user has.
- The page is laid out for narrow screens and prints cleanly, so printing from the browser is the route to a PDF.

### S3 Sharing also carries import and device copies (2026-11-24)

The owner widened D8, which had left import between Mindweave users for later:

- A recipient who uses Mindweave can bring a shared file in and keep asking from it.
- The same mechanism carries a copy of the user's own work between their devices, through cloud storage the user already has. It may be more cumbersome than automatic sync; it must be possible.

### S4 One exchange format; import and export stay incremental (2026-11-24, 11-25)

The owner's principle: whether importing or exporting, things can only stay the same or grow; nothing is ever deleted automatically.

- The exchange format of seam E5 is implemented. Every file this design writes is that format.
- A shared HTML file holds the same content twice: as the page a person reads, and as exchange data Mindweave reads. The data holds exactly what the page shows, never more.
- A device copy is the same format with everything in it, as a package file that also contains the page.
- One import reads both. Everything is recognised by its stable identity:
  - a conversation or message the app already has is left as it is: it already exists, and nothing is copied;
  - a conversation the app has never seen arrives as new;
  - a conversation it already has gains only the messages it lacks;
  - for single-valued things (a title, the current summary version, archived or not) the newer one wins. *Narrowed on 2026-11-25 ([sharing-import.md](sharing-import.md) I2): this holds for device copies only; a shared file never changes them.*
- Importing the same file again changes nothing, however often it is done.
- *Narrowed on 2026-11-25 ([sharing-import.md](sharing-import.md) I4): a received conversation is read-only, so the following case arises only between the user's own devices.* When both sides continued the same conversation differently after their last exchange, this side keeps its conversation untouched, and the other continuation arrives once as a separate conversation, marked as coming from elsewhere. Importing the file again recognises it and adds nothing.
- Import never deletes. A file's silence about a conversation has three possible causes (deleted there, not selected, created here later), so silence is never read as deletion.
- The database backup is a different thing: it protects the local machine across version changes and deletions. It is unchanged and outside this design.

### S5 What belongs to this cycle (2026-11-25)

All three steps, in this order, each merged on its own. The format is designed for all three from the start.

1. Export: the HTML file with the exchange data inside.
2. Import and continue: bring a shared file in and keep asking.
3. Device copies: the full package with attachment originals, and adding to conversations that already exist.

### S6 Structure only, or with conversation content (2026-11-25)

The user chooses when sharing whether the share carries the conversation content or only the conversation structure. Without a choice, it carries only the structure.

**Structure only** means titles only: no message text leaves.

| In it | Not in it |
| --- | --- |
| Each conversation as a dot, with its title | The text of the user's questions |
| The branch lines | The answers |
| The order in time and each conversation's start date and time | The sentence a branch left from |
| Dot size: how much was asked there, and the number of questions | Summaries (only the mark that one exists) |

**With content:**

| Part | In the share |
| --- | --- |
| The user's questions | In full, always visible |
| Answers | In full, folded to a few lines under each question, with "show all" |
| The sentence a branch left from | Marked in the answer, with a link into that branch and a way back |
| Retried answers | Only the answer that stands; earlier attempts are left out |
| Stopped answers | Kept, marked incomplete |
| Failed answers with no text | Left out |
| Summary | The current version, shown as a layer on top of its conversation; history versions and candidates are left out |

A device copy always carries everything; this choice applies to shares.

### S7 What never leaves (2026-11-25)

Never in any share:

| Material | Rule |
| --- | --- |
| Conversations not ticked | Nothing, not even that they exist: no branch mark for an unticked branch, and dot sizes and counts cover only what is shared |
| Attachment contents and originals | Never; the question shows only that an attachment was there, with its file name |
| Saved text of web pages and search results | Never |
| Tool steps | Only the one-line count; no queries, arguments, results or MCP server names |
| Model and service | No model name, connection, address, keys, usage or cost |
| Reasoning | Never saved, so never shared |
| The user's state in the app | No reading positions, drafts, read marks, recommendations or link suggestions |
| The workspace | Not its name; the share has its own title, which starts as the root conversation's title and can be edited |
| The user | Nothing about the user or the device |

Kept in a share with content:

| Material | Rule |
| --- | --- |
| Web citations | Number, page title and real URL |
| Citations of the user's own material | Only its kind ("an attachment", "a summary"); a title only if that summary is itself in the share |
| Summary links between two shared conversations | The line always; the note only with content |
| Dates and times | As the app shows them |

The share dialog lists what will leave before the file is saved.

A device copy carries everything except keys, connections and addresses.

### S8 The shared page, as the reader sees it (2026-11-25)

The owner judged the prototype in [prototypes/share-page](../../prototypes/share-page/README.md) and accepted its shape. It is the visual baseline for the page.

- The page opens with the share's title, the dates it covers, the counts of explorations, conversations and questions, and one sentence that tells a stranger what the page is.
- One picture: the path drawn as the app's timeline view draws it. There is no spatial view and no view switch.
- Under the picture, the same titles as an indented outline. On a narrow screen the picture scrolls sideways and the outline is the main way to move.
- Structure only: the picture and the outline are the whole page. Choosing a dot shows its title, start time, number of questions and whether it has a summary.
- With content: the chosen conversation appears below. Questions are in full; long answers are folded with "show all". The sentence a branch left from is marked and leads into the branch; the branches of an answer are also listed under it, so they stay visible while it is folded. A branch conversation names where it left from and links back.
- The summary is a closed layer on top of its conversation.
- The foot of the page says the file was made with Mindweave and can be opened in it.

### S9 Language and theme of the page (2026-11-25)

- The page follows the reader. Its own words are carried in English and Chinese; it opens in Chinese when the reader's browser is set to Chinese, otherwise in English. Its theme follows the reader's system setting.
- A small switch for each sits in a corner of the page.
- Printing is always light.
- Conversation text is never translated: questions, answers, titles and summaries appear as written.

### S10 After sharing (2026-11-25)

- A share is a snapshot of the moment it was saved. Later questions, edits and deletions do not reach a file already saved, and nothing can call a sent file back. The share dialog says so in one line.
- The app keeps no record of shares. The file is the record.
- Sharing again is saving a new file; a recipient who imports it gets only what is new (S4).
- The suggested file name is the share's title and the date.

### S11 The remainder (2026-11-25)

The owner accepted these together:

- **Deletions in a device copy.** The copy lists the identities of conversations deleted on the other device, with no content. On import the app offers to delete them here too, through the existing delete confirmation; it never does so by itself. A shared file never carries this list.
- **Importing a structure-only share** gives an outline: its dots and titles appear as directions the recipient can explore in their own conversations. There are no messages to continue from. *Settled on 2026-11-25 ([sharing-import.md](sharing-import.md) I1): the outline is real, empty conversations.*
- **Entry points, failure cases, out of scope, acceptance and the first increment** as in §6, §9, §11, §10 and §12.

## 5. Approaches considered

| Question | Options | Outcome |
| --- | --- | --- |
| Unit of a share | One path (a root down to one conversation); a set of conversations in one workspace; the whole workspace | The set: the other two are its ends |
| Form | One HTML file; PDF; Markdown or a zip; a hosted link | HTML. A PDF loses the branching; Markdown is not readable for most recipients; a link needs a server |
| Import | Separate formats for sharing, import and device copies; one exchange format under all three | One format: one writer, one importer |
| A conversation continued on both sides | Interleave the messages; keep only the newer; the other continuation as a separate marked conversation | The separate conversation: nothing is mixed and nothing is lost |
| Default content | Everything open; answers folded; questions only; titles only | Titles only by default; folded answers when the user chooses content |
| The page's picture | Timeline; spatial view; both with a switch | Timeline: it reads left to right for a stranger and needs no saved places |
| Record of shares | A list of past shares; none | None: the file is the record |

## 6. The parts

### 6.1 The exchange document

One model, in Application, built from the stores and holding no stored record:

- the share: format name and version (E5: `mindweave-export`, version 1), title, the time it was made, the app version, and its kind: `structure`, `content` or `device`;
- conversations: identity, title, start time, parent identity, number of questions, whether it has a summary;
- with content: per conversation, its messages in order (identity, role, time, state, text as Markdown, attachment names, the tool count line, citations as S7 allows), its branch source (the parent message's identity, the quoted text, and the position as block index and offset, per E5; a continuation of a received conversation has no quoted text, [sharing-import.md](sharing-import.md) I4), and its current summary version (identity, title, body, time). For import, each message also carries its stored sequence number, gaps included, and the summary the number it covers up to (owner, 2026-11-25);
- summary links between two shared conversations (identities; the note only with content);
- for a device copy only (step 3): everything S7 holds back from a share except keys, connections and addresses; the list of deleted identities.

Filtering happens when the document is built, in one place. Writers and the page never see what was filtered out.

Identities are the stored ones, unchanged: import depends on them (S4).

### 6.2 The file

**A share** is one `.html` file:

- the exchange document as JSON, in a data block the page's script and the app both read;
- the page's style and script, inline;
- nothing fetched from anywhere: no remote script, font, image or link that loads by itself. Links in answers and citations are ordinary links the reader may click.

Writer's calls, each easy to change:

- **The page draws from the data block.** There is one source inside the file, so "the data holds exactly what the page shows" holds by construction.
- **Answers are read as the app reads them.** Whether Markdown becomes HTML at export or in the page's script is the builder's choice (§7). Either way, a set of sample answers must read the same in the page as in the app: emphasis next to Chinese punctuation, tables, and the four formula delimiters. Formulas are drawn by a formula library bundled in the file. Code blocks are plain monospace in step 1; highlighting can come later.
- **Nothing in an answer runs.** HTML written inside an answer is shown as text, never as markup, so a shared or imported answer cannot carry a script.
- **Remote images in answers stay links**, as in the app.

**A device copy** (step 3) is a package: a zip that holds the same page, the exchange document, and the attachment originals by content hash. Its extension and layout follow E5.

### 6.3 The share dialog

Opened from "Share…" on a conversation's menu in the sidebar and on a dot in the Mindweave page. It holds:

- the conversations of the workspace as the timeline's dots with ticks, starting with the exploration it was opened from (S1). Unticking a conversation unticks its branches; ticking a branch ticks its ancestors;
- the choice "include conversation content", off unless chosen (S6);
- the share's title, editable;
- the list of what will leave: counts of conversations and questions, and with content, the attachment names and the number of web citations (S7);
- the line saying a saved file is a snapshot and cannot be called back (S10);
- Save, which opens the save dialog with the suggested name.

The dialog's look is the owner's to judge from a running build or screenshots before it is called done.

### 6.4 Import (step 2)

The note for this step is [sharing-import.md](sharing-import.md) (approved 2026-11-25). It holds where it differs from the outline below: a received conversation is read-only and the user continues in a conversation of their own; a shared file changes nothing that is already here.

- Entry: dragging a file onto the window, or "Import…" in the workspace menu. The user chooses the workspace: the current one or a new one.
- The app reads only the data block and never runs the page.
- Before anything is written, a summary says what will happen: how many conversations are new, how many gain messages, how many are already here, how many arrive as another continuation.
- The whole import is one transaction.
- Imported conversations are marked as coming from a shared file, shown in their details. This needs a stored mark, so step 2 has a migration.
- A conversation already present in another workspace is reported as already here, with the workspace's name; it is not moved or copied.
- Continuing to ask in an imported conversation works as in any conversation, with the recipient's own model. What a share left out (attachment contents, saved pages) is simply absent from the context.

### 6.5 Device copies (step 3)

- "Save a copy for another device" for a workspace writes the package of §6.2.
- Import is the same import, with the "already here" cases doing real work: conversations gain missing messages, summaries gain missing versions, attachment originals are added by hash.
- Deleted identities are recorded from step 3 on (a migration) and travel in the copy; the offer of S11 follows the import.

### 6.6 Details settled after an independent builder's read (writer's calls, 2026-11-25)

A reviewer read this design against the code as a builder would. Its questions are settled here; each is easy to change.

1. **A conversation with no title yet.** The app then shows the branch's quoted sentence or the start of the first question as its name, and both are message text. In a structure-only share such a conversation is named "Untitled conversation" (in the page's language). The share dialog shows the names exactly as they will leave, so the user can give a title first. With content, the app's name is used as it is.
2. **A conversation with a summary.** Its dot carries the name the app shows for it in the Mindweave page (the summary's short name), in both kinds of share. That name is a title, not summary text; the user sees it in the dialog before saving.
3. **Where a branch left from.** With content, the format carries the source message's identity and the quoted sentence, and the position in the answer's Markdown when the app has it. The page marks the sentence by finding the quote in that answer; when the quote is not found, or to tell two occurrences apart without a position, the sentence is not marked, and the branch is still listed under the answer (S8). The app's own stored anchor travels too, with its version and render profile, when it holds no text beyond the quote, so that an importing app can highlight the sentence; otherwise the importing app finds the quote again.
4. **A branch that left from an earlier attempt of an answer.** That attempt is included, although S6 leaves earlier attempts out, because a shared branch depends on it. It is shown folded and labelled as an earlier answer. The format carries which answer replaced which, so an importing app never shows two standing answers.
5. **Where Markdown is turned into HTML** is not in Core or Application, which have no Markdown library; the exchange document carries Markdown.
6. **Times** are carried as instants, with no time zone of the sharer. The page shows them, and groups days, in the reader's own time zone, so a day boundary can fall differently from the sharer's picture.
7. **"The newer one wins"** needs the time a title was last changed, which is not stored today. *Moved to step 3 on 2026-11-25 ([sharing-import.md](sharing-import.md) I2).* It was to be added with step 2's migration and enters the format as an optional field, which E5 allows inside one format version. Step 1 needs no migration.
8. **Archived conversations** are dots like the others (session-network.md §3), so they are ticked with their exploration and can be unticked. The page does not say that a conversation is archived: that is the user's state in the app (S7).

### 6.7 Details settled while building 1b (builder's calls, 2026-11-25)

1. **Markdown is read at export, by the app's own pipeline.** The formula delimiters come from a parser that depends on the UI library, so this is in Desktop, behind `IShareMarkdown` in Application. The result is not HTML but a tree of a few named elements. The file holds it as data beside the exchange document, and the page builds elements from it by name. The page has no Markdown of its own and never turns text into markup.
2. **The file therefore holds each answer twice**: its Markdown, which an importing app reads, and its drawn form, made from that same document when the file is saved. This replaces "the page draws from the data block" of §6.2 for answers and summaries. Questions, names, citations and everything else are still drawn from the data block.
3. **Where the app shows nothing** (a footnote's text, a custom container), the page shows nothing either. HTML is the exception: requirements §10.7 has it shown as text, and the app leaves it out.
4. **Formulas are drawn by KaTeX 0.19.1** (MIT; its fonts SIL OFL 1.1), because the app's own formula library is a port of it. It is kept as published in `src/Mindweave.Infrastructure/Share/katex`, only a share with a formula carries it, and its notice is in that file and in `THIRD-PARTY-NOTICES.txt`. A formula it cannot read is shown as it was written. It takes about 640 KB of the file; without a formula the page itself is about 38 KB.
5. **The page's policy with content is stricter than the structure-only one**: each script is named by its hash, so no other script and no event handler can run. Fonts may come only from inside the file. A structure-only file is unchanged.
6. **The answer that stands** is the latest attempt that ended with any text. When a retry failed without text, the attempt before it is shared, rather than the question without an answer.
7. **A summary citation's title** is the one its summary has in the share, not the one stored with the citation, which can be an earlier version's.
8. **Where sentences of two branches overlap**, the shorter one is on top, and two branches from the same words share one mark that leads to the first; both are listed under the answer.
9. **Data the page does not show.** Message identities, sequence numbers, how far a summary covers, and a branch's position and anchor serve only an importing app. They hold no text of their own beyond the quote. A gap in the numbers tells that something was left out, such as a replaced attempt, but not what. The dialog's list names the identities, the numbers and the coverage (owner, 2026-11-25).
10. **Printing** shows every answer of the chosen conversation in full. It does not print the other conversations.
11. **The dialog's list of what leaves** (owner, 2026-11-25) is a card apart from the notes. What a reader can read stands out in it, one labelled row each, as a warning once message text leaves. The rest of what is in the file, and what is not, are quiet rows below.

## 7. Left to the builder

- The JSON shape of the exchange document inside the rules of E5 and §6.1.
- Which formula library is bundled, how the page script is kept in the repository and embedded, and whether Markdown is turned into HTML at export or by the page's script (§6.2, §6.6).
- Whether the timeline in the page is laid out by the page's script (as in the prototype) or its positions are computed at export; the rules are those of session-network.md §5.1 either way.
- Folding at a paragraph boundary; the fold height.
- Code structure. Following the glossary: `Share` for the selection, the dialog and the page writer; the exchange document and import under the names §13 proposes.
- For step 2, how the other continuation of a conversation gets identities that a second import recognises (derived from the incoming identities, not random).
- *Settled on 2026-11-25 by [sharing-import.md](sharing-import.md) I1 and I4, which replace the two step 2 points here.* For step 2, how an imported outline (S11) is stored and drawn. The writer's suggestion: as directions like recommended nodes, which the product already shows as "not yet explored", with the outline's branch lines between them; opening one starts the recipient's own conversation with its title as the question. A design note for step 2 settles it before code.

## 8. Design notes per step

Following evolution.md §4: this document decides all three steps and details step 1. Steps 2 and 3 each get a short note of their own before their code (data model, migration, the builder's open points in §7), based on what step 1 taught.

## 9. Failure cases

| Case | What happens |
| --- | --- |
| An answer is still being generated in a ticked conversation | The share goes up to the last finished answer, and the dialog says so |
| Nothing is ticked | Saving is unavailable |
| A ticked conversation is deleted while the dialog is open | It is left out at save, and the user is told |
| The file cannot be written | As the summary export: the reason is shown, and the target is left as it was |
| The imported file is damaged or not from Mindweave | Refused; nothing changes |
| The file's format version is newer than the app knows | Refused, with "update Mindweave to open this" |
| An import fails halfway | Nothing is imported |
| A file made to cause harm | Only the data block is read; the page is never run; imported text is drawn like any answer and loads no images |
| A device copy whose attachment original is missing | The attachment arrives as "original missing", as the app already shows it |

## 10. Acceptance scenarios

Step 1:

1. Share one exploration, structure only. The file opens in a browser with the network off. Searching the file's bytes finds no message text, no quoted sentence and no summary text.
2. Share with content. Questions, folded answers, branch marks with their way back, and summaries appear as in the prototype.
3. Untick a branch. Nothing of it is in the file: not its title, not a branch mark in its parent's answer, not its questions in any count.
4. A share with an attachment, a web citation, a citation of an unshared summary and tool steps shows only what S7 allows. A test plants a marker string in each excluded place (attachment text, a saved page, tool arguments, the model name, the workspace name, an unticked conversation) and finds none of them in the file.
5. The page opens in the reader's language and theme, switches both, and prints light.
6. An answer still being generated is left out, and the dialog says so.
7. The owner confirms the dialog and a page made from their own data.

Step 2:

8. A recipient imports a share with content into a new workspace and continues asking in one of its conversations.
9. Importing the same file twice: the second time reports everything as already here and changes nothing.
10. A newer share of the same exploration adds only the new conversations and messages.
11. A conversation continued on both sides arrives once as a marked separate conversation; importing again adds nothing.
12. A structure-only share arrives as an outline of directions.
13. A damaged file, a file of another kind and a file of a newer format are each refused with nothing changed.

Step 3:

14. A device copy moves a workspace with attachments to a second data folder; conversations, branches, summaries and attachments open there.
15. A later copy brings only the changes, and a second import of it changes nothing.
16. A conversation deleted on one device is offered for deletion on the other, and stays when the user declines.

## 11. Out of scope for this cycle

- Hosted links, a server, accounts, a community.
- Automatic sync, such as watching a cloud folder.
- Attachment contents in a share.
- Editing or hiding individual sentences inside the share dialog.
- Password protection of the file.
- A PDF exporter of its own.
- Importing exports from other assistants.
- The map (D6), and sharing across workspaces.
- A record of past shares.
- Code highlighting in the page (step 1).

## 12. Increments

Step 1 is built in two merges:

- **1a, the first increment: the structure-only share, end to end.** The exchange document for structure, the share dialog with ticks, and the page with the picture, the outline, language and theme. No Markdown, formulas or content filtering yet. It is the default the owner chose, and it proves the format, the dialog and the page.
- **1b: with content.** Messages, folded answers, branch marks, summaries, the S7 filter with its marker test, formulas.

Then step 2 and step 3, each after its own short note (§8).

Each merge states how to try it, what was run, and what was not verified; the dialog and the page wait for the owner's confirmation.

## 13. Requirements and architecture text that must change (separate proposal)

Written as its own commit and confirmed by the owner on 2026-11-25: requirements v0.29 (§10.7 and its acceptance in §11) and architecture §16. The table records what changed.

| Where | Change |
| --- | --- |
| Requirements §4 concepts | Share; device copy |
| Requirements §8.3 | Sharing beside the summary export; what a share contains (S6, S7) |
| Requirements §10 | A section for sharing, import and device copies, with the acceptance scenarios of §10 here in §11 |
| Requirements §10.1 | Full import and export and cross-device sync: the part this design delivers, and what stays later |
| Architecture §2.3 glossary | `Share` (no longer "not yet in the requirements"); rows for the exchange format, import and the device copy |
| Architecture, export | The exchange document as the only outward form; E5 changes from specification to built |
| [evolution-seams.md](evolution-seams.md) E5 | The specification predates attachments, citations, tool steps and summary links; it is brought up to the exchange document of §6.1 when step 1 is built |
