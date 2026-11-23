# Status

What is true now: work in progress, next steps, what needs you, open decisions, and when each platform was last verified. When an item is done, it leaves this file. Each merge adds a dated entry to [docs/changes.md](docs/changes.md), decisions go into the [decision log](docs/design/decisions.md), and facts about the machines go into [workstation.md](docs/design/workstation.md).

## In progress

- Finishing the session network (D-035). `task/network-stable` (option C: each exploration keeps its place and turn, migration v18) waits for you (see "Needs you").
- Requirements v0.25 and architecture §11 run ahead of the code until that branch merges and the switch is lifted: `exploration_place` and `ExplorationPlace` are only on the branch, the code still says `SessionWeights` for `ConversationWeights`, and the default build still has the summary-only view with explicit layout saving.

## Next steps

- Next-phase ideas: refine the first drafts in [docs/ideas](docs/ideas/README.md) one by one with you. Nothing in them is agreed.
- Free trial (evolution D9): adopted, done later, no schedule. The design is [free-trial.md](docs/design/free-trial.md).
- Session network (behind `MINDWEAVE_SESSION_NETWORK=1`; [session-network.md](docs/design/session-network.md) §16): after that branch, lifting the switch, which is your own decision after screenshots and a trial. It also removes the summary-only view and renames the code's "session" to "conversation".
- Verify in daily use:
  - agent platform phases 2–4;
  - the run loop (L1–L11);
  - the English prompts of bilingual merge 3 (D-031).
- Then keep improving the agent from the to-dos in section 8 of [agent-loop.md](docs/design/agent-loop.md), with `scripts/real-api.sh eval`.
- Other candidates:
  - a full run and a Release build trial on the Windows hardware;
  - course report material;
  - the six minor issues in §13 of [bilingual-ui.md](docs/design/bilingual-ui.md);
  - the [low / info-level to-dos](docs/reviews/claude-auto-health-20261112/low-triage.md), which is the only to-do list.
- Maintainability, waiting for the next change to that code:
  - EXC-3: a dedicated exception for a closed or shutting-down state, which empties `CatchPolicyTests.InvalidOperationAsSignal`;
  - EXC-4: guards on `GenerationCoordinator` events;
  - the ConversationService extraction;
  - splitting `AnswerRunner.RunAsync`.

## Not verified yet

- On Windows:
  - attachments (phase 1);
  - the bilingual product;
  - coloured Medium bold, which draws at regular weight in Microsoft YaHei.
- With a real model:
  - the generating indicator;
  - reasoning and run status (with a reasoning model);
  - the answer output limit;
  - the English prompts.
- In a real window: the language switch, and English short names on the summary network canvas.
- The session network's quiet links and the new link suggestions: on Windows; the suggestions with a real model (the prompts changed).
- The session network's molecule layout: on Windows, and an exploration with hundreds of direct branches in two or more explorations (about 4.5 s for two of 500).

## Needs you

| Item | Reason | Blocks |
| --- | --- | --- |
| Say how option C looked in the trial (534f89e, run on 2026-11-23: the copy migrated to v18 cleanly) and whether v18 may merge | Migration v18 of your real data is yours to decide | Merging `task/network-stable` |
| Look at the icon size in a real window (direction A looks smaller than before; making it bigger is one size in the style) | Look and feel is yours to judge | Nothing |
| Real service checks (paid, optional): DeepSeek images + tools, Kimi K3 base64 images, GPT-6 tool calls, DashScope streaming tools | Only within the budget you give | The capability presets of phases 1–2 |
| Remove the old Mindweave icon from the Dock and pin `~/Applications/Mindweave.app` (once) | The old icon points to the v11 build of 11-13, which cannot open v13 data | Opening the daily build from the Dock |

## Open decisions

- The contrast of the “输出” (Output) colour in the statistics chart is below 3:1: deferred, not blocking (you, 2026-11-16).

## Last verified

| What | Commit | Date | Result |
| --- | --- | --- | --- |
| macOS full run | the requirements v0.25 merge | 2026-11-23 | passed: Core 137, Architecture 26, Infrastructure 1013 + 23 skipped, Desktop 4 shards 0 failed |
| CI on `main` (Linux + Windows) | see the latest run on GitHub | | |
| Windows 11 hardware, full run | daabd5b | 2026-11-21 | passed |
| Windows 11 hardware, stress and Release start | 08ead83 | 2026-11-16 | passed |
| Daily build | 138bd17 (schema v17) | 2026-11-22 | published by you, no data upgrade; Settings names the build (D-033); the previous build 5f50734 is kept for rollback |

First command: `scripts/test.sh full`.
