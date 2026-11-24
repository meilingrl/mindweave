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

1. What is kept for each conversation so that it can be brought back (Q1, asked 2026-11-25).
2. When it is written, and what it costs.
3. What returning from a branch shows.
4. What coming back after some days shows, and where.
5. Unresolved questions as the edge of an exploration in the summary network.
6. Answers that cite the user's own summaries (with reply quality).
7. Extending a single summary on request (§10.2).
8. Joint summaries of several conversations (§10.1).
9. Relations across workspaces and automatic categorization (§10.2).

## Decisions

None yet.

## Still to write

The approach, out of scope, failure cases, acceptance scenarios and the first increment follow the decisions.
