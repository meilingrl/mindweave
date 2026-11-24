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

## Open questions

In order of what depends on them:

1. The form of the result: page or file, and how it travels.
2. What is shown inside each conversation: full answers, folded answers, summaries, retries.
3. What never leaves: attachments, citations, tool steps, model details, unticked branches.
4. How a reader who was not there follows the path: the timeline and the network in the page.
5. Language and theme of the page.
6. After sharing: later changes, deletion, sharing again.
7. Failure cases, what is out of scope for 0.7, acceptance scenarios, the first increment.
