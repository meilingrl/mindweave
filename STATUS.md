# Status

What is true now: work in progress, next steps, what needs you, open decisions, and when each platform was last verified. When an item is done, it leaves this file. Each merge raises the version and adds a section for it to [docs/changes.md](docs/changes.md), decisions go into the [decision log](docs/design/decisions.md), and facts about the machines go into [workstation.md](docs/design/workstation.md).

## In progress

- Sharing (evolution D8, [sharing.md](docs/design/sharing.md)), the theme of 0.6.x towards 0.7: the design is adopted (D-039) and step 1 is built: the structure-only share (0.6.13) and the share with content (0.6.15). Next are import, then device copies, each after a short note.
- Four design discussions are open with you on task branches: reply quality; return and review; safety, diagnostics and upkeep; ask from anywhere and local AI. Nothing in them is agreed as a whole.

## Next steps

- Next-phase ideas: refine the first drafts in [docs/ideas](docs/ideas/README.md) one by one with you. Nothing in them is agreed.
- Free trial (evolution D9): adopted, done later, no schedule. The design is [free-trial.md](docs/design/free-trial.md).
- `node_layout` (the positions of the earlier summary-only view) is dropped in a later migration, once you have used a daily build of 0.6.0 or later and are satisfied (D-038).
- Verify in daily use:
  - agent platform phases 2–4;
  - the run loop (L1–L11);
  - the English prompts of bilingual merge 3 (D-031).
- Then keep improving the agent from the to-dos in section 8 of [agent-loop.md](docs/design/agent-loop.md), with `scripts/real-api.sh eval`.
- Other candidates:
  - a full run and a Release build trial on the Windows hardware;
  - the six minor issues in §13 of [bilingual-ui.md](docs/design/bilingual-ui.md);
  - the [low / info-level to-dos](docs/reviews/claude-auto-health-20261112/low-triage.md) still open.
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
- The application icon (0.6.11): on Windows; the small sizes in daily use.
- Sharing (0.6.13, 0.6.15): on Windows; the page in Safari and Firefox; on 1,000 conversations the dialog took about 1.2 s to open in a debug build, and its speed with content was not measured; the dialog's new list in a real window. The foot of the page says the file can be opened in Mindweave, which is true only once import exists.
- macOS file panels (0.6.13): picking real attachments; quitting from the Dock while a panel is open; an Intel Mac.
- 0.6.10: a real model reading the rest of a long result; a real MCP server stopped during a call. On Windows: all of it.
- The UI points of 0.5.11 to 0.5.14, 0.6.2, 0.6.4, 0.6.6 and 0.6.7 (D-037): on Windows; a real model's tool steps while the page stays.
- Many new-answer hints at once in a very small window, which can lie over the page.
- In a real window: the language switch, and English short names on the summary network canvas.
- The network's quiet links and the new link suggestions: on Windows; the suggestions with a real model (the prompts changed). Recommended nodes counted by conversations (0.6.0): with a real model.
- Exploration places (option C): on Windows; an extremely spread-out workspace, where a coarse packing grid can still push an untouched exploration; a title made very long can do the same (rare: 1 of 660 tried). Keeping it out needs a migration.
- The network's molecule layout: on Windows, and an exploration with hundreds of direct branches in two or more explorations (about 4.5 s for two of 500). The title of a root with many branches can lie over one of its branch lines.

## Needs you

| Item | Reason | Blocks |
| --- | --- | --- |
| The application icon: the tile colour (black nearly disappears on a dark taskbar), and whether the open state with nodes is used | Look and feel is yours to judge | Nothing |
| Look at the icon size in a real window (direction A looks smaller than before; making it bigger is one size in the style) | Look and feel is yours to judge | Nothing |
| Real service checks (paid, optional): DeepSeek images + tools, Kimi K3 base64 images, GPT-6 tool calls, DashScope streaming tools | Only within the budget you give | The capability presets of phases 1–2 |
| Set the GitHub noreply address for commits (D-025) | The repository is public, and every commit so far has your personal address; it changes only new commits | Nothing |

## Open decisions

- The contrast of the “输出” (Output) colour in the statistics chart is below 3:1: deferred, not blocking (you, 2026-11-16).

## Last verified

| What | Commit | Date | Result |
| --- | --- | --- | --- |
| macOS full run | sharing 1a, 0.6.13 | 2026-11-25 | passed: Core 137, Architecture 36, Infrastructure 1071 + 23 skipped, Desktop 4 shards 0 failed |
| CI on `main` (Linux + Windows) | see the latest run on GitHub | | |
| Windows 11 hardware, full run | daabd5b | 2026-11-21 | passed |
| Windows 11 hardware, stress and Release start | 08ead83 | 2026-11-16 | passed |
| Daily build | f9edc39 (0.6.11, schema v18) | 2026-11-24 | published by you; the backup taken before it is `mindweave-v18-20261124T143852Z-before-publish-f9edc39.db`. A build before v18 cannot open this data: going back to one needs the v17 backup |

First command: `scripts/test.sh full`.
