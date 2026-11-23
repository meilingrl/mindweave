# Status

What is true now: work in progress, next steps, what needs you, open decisions, and when each platform was last verified. When an item is done, it leaves this file. Each merge raises the version and adds a section for it to [docs/changes.md](docs/changes.md), decisions go into the [decision log](docs/design/decisions.md), and facts about the machines go into [workstation.md](docs/design/workstation.md).

## In progress

- Nothing in progress.

## Next steps

- Next-phase ideas: refine the first drafts in [docs/ideas](docs/ideas/README.md) one by one with you. Nothing in them is agreed.
- Free trial (evolution D9): adopted, done later, no schedule. The design is [free-trial.md](docs/design/free-trial.md).
- `node_layout` (the positions of the earlier summary-only view) is dropped in the release after 0.6.0 ([session-network.md](docs/design/session-network.md) §12.2): a migration, so you decide then.
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
- The UI points of 0.5.11 to 0.5.14 and 0.6.2 (D-037): on Windows; a real model's tool steps while the page stays.
- In a real window: the language switch, and English short names on the summary network canvas.
- The network's quiet links and the new link suggestions: on Windows; the suggestions with a real model (the prompts changed). Recommended nodes counted by conversations (0.6.0): with a real model.
- Exploration places (option C): on Windows; an extremely spread-out workspace, where a coarse packing grid can still push an untouched exploration.
- The network's molecule layout: on Windows, and an exploration with hundreds of direct branches in two or more explorations (about 4.5 s for two of 500). The title of a root with many branches can lie over one of its branch lines.

## Needs you

| Item | Reason | Blocks |
| --- | --- | --- |
| Look at the icon size in a real window (direction A looks smaller than before; making it bigger is one size in the style) | Look and feel is yours to judge | Nothing |
| Real service checks (paid, optional): DeepSeek images + tools, Kimi K3 base64 images, GPT-6 tool calls, DashScope streaming tools | Only within the budget you give | The capability presets of phases 1–2 |
| Remove the old Mindweave icon from the Dock and pin `~/Applications/Mindweave.app` (once) | The old icon points to the v11 build of 11-13, which cannot open v13 data | Opening the daily build from the Dock |

## Open decisions

- The contrast of the “输出” (Output) colour in the statistics chart is below 3:1: deferred, not blocking (you, 2026-11-16).

## Last verified

| What | Commit | Date | Result |
| --- | --- | --- | --- |
| macOS full run | the settings row fix, 0.6.2 | 2026-11-23 | passed: Core 137, Architecture 29, Infrastructure 1019 + 23 skipped, Desktop 4 shards 0 failed |
| CI on `main` (Linux + Windows) | see the latest run on GitHub | | |
| Windows 11 hardware, full run | daabd5b | 2026-11-21 | passed |
| Windows 11 hardware, stress and Release start | 08ead83 | 2026-11-16 | passed |
| Daily build | 797b8bd (0.5.14, schema v18) | 2026-11-23 | published by you; the backup taken before it is `mindweave-v18-20261123T124924Z-before-publish-797b8bd.db`. A build before v18 cannot open this data: going back to one needs the v17 backup |

First command: `scripts/test.sh full`.
