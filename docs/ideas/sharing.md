# Sharing a thinking path

**Status:** working draft, in discussion with the owner since 2026-11-24. Not adopted. It refines [evolution.md](../evolution.md) D8 into a design for the 0.6.x cycle. Decisions below are the owner's unless marked as the writer's call.

## Settled before this draft

From evolution D8 (owner, 2026-11-19/20): one mechanism, not a feature per scenario; choosing what to share is a light choice made when sharing; no side-by-side comparison of two people's summary networks; one-way publishing as a page or file that anyone can open, with no install, account or server. Reasoning is never saved, so it is never shared ([reasoning-and-status.md](../design/reasoning-and-status.md)). Nothing that leaves the app serializes stored records ([evolution-seams.md](../design/evolution-seams.md), E5).

## Decisions

### S1 What one share is (2026-11-24)

- A share is a set of conversations from one workspace.
- The selection starts as the current exploration: the root conversation and all its branches. The user unticks conversations, or ticks further explorations.
- A ticked branch conversation always brings its ancestors, so the reader can see where each branch left from.
- A share never crosses workspaces.
- The picker is part of the share dialog and shows the same dots as the timeline view, with ticks. It is not a view of its own.

Considered: one path only (the chain from a root to one conversation), which loses the side branches; the whole workspace always, which is too much for private topics. Both are ends of the chosen selection.

### S2 The form of the result (2026-11-24)

- The result is one self-contained HTML file. It loads nothing from the network: styles, scripts, formula drawing and the picture of the path are inside the file.
- It travels as a file, by whatever means the user has. Mindweave uploads nothing.
- The page is laid out for narrow screens and prints cleanly, so printing from the browser is the route to a PDF.

Considered: a PDF (static, loses the branching); Markdown or a zip (not readable for most recipients); a hosted link (needs a server, ruled out by D8).

### S3 Sharing also carries import and device copies (owner's direction, 2026-11-24)

The owner widened D8, which had left import between Mindweave users for later:

- A recipient who uses Mindweave can bring a shared file in and keep asking from it.
- The same mechanism carries a copy of the user's own work between their devices, through cloud storage the user already has. It may be more cumbersome than automatic sync; it must be possible.

How this is done is under discussion (proposal P1 below).

### S4 One exchange format; import and export stay incremental (2026-11-24)

The owner's principle: whether importing or exporting, things can only stay the same or grow; nothing is ever deleted automatically.

- The exchange format of seam E5 is implemented. Every file that leaves the app is that format.
- A shared HTML file holds the same content twice: as the page a person reads, and as exchange data Mindweave reads. The data holds exactly what the page shows, never more.
- A device copy is the same format with everything in it (attachment originals, all summary versions, summary links), as a package file that also contains the page.
- One import reads both. Everything is recognised by its stable identity:
  - a conversation or message the app already has is left as it is: it already exists, and nothing is copied;
  - a conversation the app has never seen arrives as new;
  - a conversation it already has gains only the messages it lacks;
  - for single-valued things (a title, the current summary version, archived or not) the newer one wins.
- Importing the same file again changes nothing, however often it is done (owner: the same thing is reported as already there, never copied).
- When both sides continued the same conversation differently after their last exchange, this side keeps its conversation untouched, and the other continuation arrives once as a separate conversation, marked as coming from elsewhere. Importing the file again recognises it and adds nothing (owner, 2026-11-25).
- Import never deletes. A file's silence about a conversation has three possible causes (deleted there, not selected, created here later), so silence is never read as deletion.
- The database backup is a different thing: it protects the local machine across version changes and deletions. It is unchanged and outside this design (owner).

**Deletions between devices (writer's proposal, not yet confirmed).** A device copy carries the identities of deleted conversations, with no content. On import the app offers to delete them here too, through the existing delete confirmation; it never does so by itself. A shared file never carries this list. It needs a stored record of deleted identities, which is a migration.

### S5 What belongs to this cycle (2026-11-25)

All three steps, in this order, each merged on its own. The format is designed for all three from the start.

1. Export: the HTML file with the exchange data inside.
2. Import and continue: bring a shared file in and keep asking.
3. Device copies: the full package with attachment originals, and adding to conversations that already exist.

### S6 Structure only, or with conversation content (2026-11-25)

The owner's decision: the user chooses when sharing whether the share carries the conversation content or only the conversation structure. Without a choice, it carries only the structure.

**With content** (the writer's proposal, accepted by the owner for this case):

| Part | In the share |
| --- | --- |
| The user's questions | In full, always visible |
| Answers | In full, folded to a few lines under each question, with "show all" |
| The sentence a branch left from | Marked in the answer, with a link into that branch and a way back |
| Retried answers | Only the answer that stands; earlier attempts are left out |
| Stopped answers | Kept, marked incomplete |
| Failed answers with no text | Left out |
| Summary | The current version, shown as a layer on top of its conversation; history versions are left out |

**Structure only** (owner, 2026-11-25: titles only): no message text leaves.

| In it | Not in it |
| --- | --- |
| Each conversation as a dot, with its title | The text of the user's questions |
| The branch lines | The answers |
| The order in time and each conversation's start date | The sentence a branch left from |
| Dot size: how much was asked there | Summaries (only the ring that says one exists) |

Importing a structure-only share (writer's proposal, to be settled with import): it arrives as an outline whose dots are directions the recipient can explore in their own conversations.

A device copy (S5 step 3) always carries everything; this choice applies to shares.

### S7 What never leaves (2026-11-25)

Never in any share:

| Material | Rule |
| --- | --- |
| Conversations not ticked | Nothing, not even that they exist: no branch mark for an unticked branch, and dot sizes count only what is shared |
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

The owner judged the prototype in [prototypes/share-page](../../prototypes/share-page/README.md) and accepted its shape ("absolutely unbeatable"). It is the visual baseline for the page.

- The page opens with the share's title, the dates it covers, the counts of explorations, conversations and questions, and one sentence that tells a stranger what the page is.
- One picture: the path drawn as the app's timeline view draws it. There is no spatial view and no view switch.
- Under the picture, the same titles as an indented outline. On a narrow screen the picture scrolls sideways and the outline is the main way to move.
- Structure only: the picture and the outline are the whole page. Choosing a dot shows its title, start time, number of questions and whether it has a summary.
- With content: the chosen conversation appears below. Questions are in full; long answers are folded with "show all". The sentence a branch left from is marked and leads into the branch; the branches of an answer are also listed under it, so they stay visible while it is folded. A branch conversation names where it left from and links back.
- The summary is a closed layer on top of its conversation.

Known roughness of the prototype, for the builder: the fold cuts at a fixed height and can slice a formula; the product folds at a paragraph boundary.

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

## Open questions

In order of what depends on them:

1. What importing a structure-only share gives the recipient.
7. Failure cases, what is out of scope for 0.7, acceptance scenarios, the first increment.
