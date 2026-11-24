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
- One import reads both:
  - a conversation the app has never seen arrives as new;
  - a conversation it already has gains the messages it lacks;
  - when both sides added to the same conversation, the incoming one arrives as a separate copy, marked as such; nothing is interleaved;
  - for single-valued things (a title, the current summary version, archived or not) the newer one wins.
- Import never deletes. A file's silence about a conversation has three possible causes (deleted there, not selected, created here later), so silence is never read as deletion.
- The database backup is a different thing: it protects the local machine across version changes and deletions. It is unchanged and outside this design (owner).

**Deletions between devices (writer's proposal, not yet confirmed).** A device copy carries the identities of deleted conversations, with no content. On import the app offers to delete them here too, through the existing delete confirmation; it never does so by itself. A shared file never carries this list. It needs a stored record of deleted identities, which is a migration.

## Open questions

In order of what depends on them:

1. Which steps belong to this cycle: export, import and continue, device copies.
2. What is shown inside each conversation: full answers, folded answers, summaries, retries.
3. What never leaves: attachments, citations, tool steps, model details, unticked branches.
4. How a reader who was not there follows the path: the timeline and the network in the page.
5. Language and theme of the page.
6. After sharing: later changes, deletion, sharing again.
7. Failure cases, what is out of scope for 0.7, acceptance scenarios, the first increment.
