# Return and review

**Status:** working draft, in discussion with the owner since 2026-11-25. Not adopted. It covers what happens between sessions of use: coming back, unresolved questions, and the summary network as the place to review. Decisions below are the owner's unless marked as the writer's call.

**Related:** [more directions](more-directions.md) (direction 6, gentle return), [evolution.md](../evolution.md) D8 ("Relation to other directions from the same discussion"), [session-network.md](../design/session-network.md), [requirements](../requirements.md) §4.2, §6.3–6.5, §7, §10.1–10.3.

## Settled before this draft

- Answers are cheap; the questions and the path are what is worth keeping (owner, session-network.md §1).
- Branching is the valued core. The summary network's value lies in accomplishment, curiosity and sharing (owner, 2026-11-19).
- No review schedule to manage, and no assessing of learning results (requirements §5.4). No reading state infers understanding (§7.1).
- One general mechanism, not a view or a feature per scenario (evolution D8).
- The network's look, exploration places and `node_layout` are settled (D-035, D-038) and are not reopened here.
- The owner rarely clicks "summarize" (session-network.md §1). Anything that appears only after a manual step stays mostly empty.

## What exists today (code at 0.6.11, checked 2026-11-25)

| Topic | Today |
| --- | --- |
| Coming back | At start the last workspace's last page is restored, and each conversation's reading position and draft. Nothing says what happened before or where an exploration stopped. No "last visited" time is stored beyond `updated_at` columns. |
| New answers | A dot in the sidebar and on the workspace picker for an answer that finished while its conversation was not on screen (D-024). Not shown in the summary network. |
| Returning from a branch | “返回来源” (Back to source) opens the parent at the selected passage and highlights it. No text from the branch comes back. The parent's answers list their branches as quote chips. |
| Unresolved questions | No such concept in code. The summary prompt asks the model to keep "the questions still open" inside a summary's text. |
| Summaries | One conversation each. Generating again covers the whole conversation and gives a candidate version; nothing extends an existing text. |
| The user's summaries in answers | Only through the `search_summaries` tool: a keyword score over current versions, when tools are on and the workspace has summaries. |
| Recommendations | Up to 3 recommended questions in new exploration and 1–3 recommended nodes in the summary network, each needing a basis summary. |
| Summary links | Made by the user, or suggested by the model on request and confirmed. Same workspace only. |
| Across workspaces | Nothing: no links, no search, no categories. Only the new-answer marks cross workspaces. |
| Later content | A summary whose conversation has grown shows “有后续” (later content) in the network's details. |

## Boundary with reply quality (proposal, the owner settles it)

The reply-quality discussion owns everything inside an answer or a summary: the answer prompt, follow-up suggestions, memory, the summary prompt. This draft owns what is kept and shown between sessions of use. Two places touch:

- **A mark set at the end of an answer.** Proposed: reply quality decides whether an answer carries anything extra and in what form; this draft decides what is stored from it, and where it shows later.
- **Answers that build on the user's own summaries.** Proposed: which summaries are relevant and how they are found is here (it is the same question as review); how the answer prompt uses them is reply quality's.

## Questions, in the order they depend on each other

1. What is kept for each conversation so that it can be brought back: decided, R1.
2. When it is written, and what it costs: decided, R2.
3. What returning from a branch shows: decided, R3.
4. What coming back after some days shows, and where.
5. Unresolved questions as the edge of an exploration in the summary network.
6. Answers that cite the user's own summaries (with reply quality).
7. Extending a single summary on request (§10.2).
8. Joint summaries of several conversations (§10.1).
9. Relations across workspaces and automatic categorization (§10.2).

## Decisions

### R1 What is kept for each conversation (2026-11-25)

- Each conversation gets a short note, written by the model with no action from the user, like the automatic title.
- The note has two parts: one or two key points, and at most two questions left open.
- The same note is what shows in every place of return: at the branch entry in the parent, in the summary network's details for the conversation's dot, and on coming back after some days. No place gets material of its own.
- Facts the app already has (where reading stopped, branches never opened, failed answers, later content after a summary) may show beside the note; they need no model.
- The note is not a summary node: it has no versions, no read state and no ring, and the user does not manage it.

Considered: structure only, with no model (free, but no key points come back from a branch); a mark set by the model at the end of each answer (no extra request, but it piles up per answer and lies inside the answer, which is reply quality's).

Follows from it: a new table (a migration, tried on a copy of the owner's data first), and a glossary row for the note before any code.

### R2 When a note is written (owner, 2026-11-25)

The owner's rule: a note is not written each time a conversation is left. It is written only for a conversation that has rested for a fairly long time and that matters more than most; a conversation with no new messages never gets a second note.

- **Rest.** A conversation is due when 36 hours have passed since both its last answer finished and it was last read (the reading position's time). 36 hours is the default.
- **Weight.** It must be above the average weight of the conversations in its workspace. The weight is the one the summary network draws as dot size: the questions asked in the conversation and in its branches (requirements §7.3).
- **No new messages, no new note.** A note records the last message it covers. A conversation is due again only when it has later answers and has rested again.

The writer's details of the weight rule (the owner asked for them; change any):

- A conversation in which only one question was asked gets no note, whatever its weight: its title and question already say what it is.
- In a workspace with fewer than 5 conversations the average says little; there a conversation needs 3 questions of its own, or a weight of 3.
- Archived conversations get no note. A note already written stays.
- The check is local and costs nothing: at start, and once an hour while the app runs. It writes at most 3 notes per check and 10 per day, the most recently active first, so the first run over old data and a long absence stay cheap.
- The request gets the earlier note and the messages after it, not the whole conversation again. It queues behind questions, titles and summaries (§9.2). A failed note shows nothing and is tried at the next check.
- Settings has one switch for notes, on by default, beside the automatic preparation of recommendations.

What follows: a note exists only for older, heavier conversations. Right after leaving a branch there is no note; the places of return must read well without one.

Considered: on leaving a conversation (the writer's first recommendation; a request per visit, most of them never read); after every answer; only when a place needs to show it.

### R3 Returning from a branch (2026-11-25)

- The immediate return stays as it is: the parent opens at the selected passage, highlighted, and nothing else is added.
- Once a branch has a note (R2), its entry in the parent, the quote chip under the answer, carries the note: the key points, then the questions left open. A branch without a note shows its title, as now.
- The chip's look is the owner's to judge from a picture.

Considered: a "bring back" action in the branch that writes its note at once (one request per click and one more control). Not taken now; it can be added later without changing what is stored.

## Still to write

The approach, out of scope, failure cases, acceptance scenarios and the first increment follow the decisions.
