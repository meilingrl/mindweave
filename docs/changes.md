# Changes

One section per merge into `main`, newest first, headed by the version and the date. Each has one line per change, then how to try it and what was not verified ([versioning.md](design/versioning.md) §5); sections are added when merging and are not edited afterwards, so they stay true. What is in progress now is in [STATUS.md](../STATUS.md); the reasons behind choices are in the [decision log](design/decisions.md).

## Versions

The second number rises when a capability is finished and on by default, and the third with every merge.

| Version | Dates | What the phase delivered | Where to read |
| --- | --- | --- | --- |
| 0.5.0 | 2026-11-20 to 11-22 | The bilingual product: English interface, prompts in the interface language, the reasoning fold, answer typography, living documents in English | The dated entries below |
| 0.4.0 | 2026-11-17 to 11-20 | The agent platform: attachments, the run loop and local tools, web search, MCP; onboarding, icons, the context ring, the daily build | The dated entries below |
| 0.3.0 | 2026-11-04 to 11-16 | The original requirements completed (items 3–8b): delete rules, workspace management, new-answer hints, suggested names, restore from backup, statistics, recommended questions, links; the structure refactor | [Stage reports](history/design/stage-reports.md) (frozen, in Chinese) |
| 0.2.0 | 2026-10-29 to 11-04 | S7: four rounds of interface polish, the architecture remediation, the owner's Windows acceptance | [Stage reports](history/design/stage-reports.md) and [track-claude-auto.md](history/track-claude-auto.md) (frozen, in Chinese) |
| 0.1.0 | 2026-10-25 to 10-28 | The core product, stages S0–S6: storage, real chat, branching and return, context budget and compression, summaries and versions, the summary network | [Stage reports](history/design/stage-reports.md) |

The nine merges after 0.5.0, on 2026-11-22 and 2026-11-23, are 0.5.1 to 0.5.9 in order. They keep their dated entries below.

## 0.5.11 · 2026-11-23

- Added the context ring's details: the figures on hover, and on a press what the context is made of; it now counts a run's instructions and tool definitions (D-037)
- Added a text width for conversations, in Settings or by dragging the text's edge; medium (800 px) is the default, where the text took the whole page
- Added folding and resizing to the summary network's details, both remembered
- Changed an answer being written to never move the page: sending puts the question at the top once, and what is read stays in place when other content changes size (requirements v0.26 §6.3)
- Changed the citations list to one line that opens it (requirements v0.26 §10.4.5)
- Changed the summary network's legend to an icon that shows it on hover, on a press and from the keyboard (requirements v0.26 §10.3)
- Changed the summary network's lines to be a step stronger in both themes
- Fixed a follow-up question landing out of sight below an answer that had stopped at its start
- Try: `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial`; ask a follow-up in a long conversation, press the ring, drag the text's edge, open the network
- Not verified: Windows; a real model's tool steps while the page stays; summaries keep their own width for now

## 0.5.10 · 2026-11-23

- Added tags for the five finished phases, `v0.1.0` to `v0.5.0`
- Added `ChangeLogRulesTests`, which keeps this file and the build's version in step
- Changed the version to rise with every merge into `main`: the third number at each merge, the second when the owner calls a milestone ([versioning.md](design/versioning.md), D-036)
- Changed this file to one section per version, one line per change
- Try: `scripts/daily.sh publish`, then read the bottom of Settings; `git tag --list 'v*'`
- Not verified: Windows. Builds from 2026-11-22 that say "0.2.0" are not the 0.2.0 of the table above.

## Before version headings

The entries below were written by day, from 2026-11-16 to 2026-11-23, and are kept as written. This file starts on 2026-11-22. The entries from 2026-11-16 to 2026-11-22 were moved here from STATUS.md, grouped by day.

### 2026-11-23

- **Requirements §10.3.2: title search looks at conversations** (documents only; no change on screen).
  - The sentence said title search "looks only at saved summaries", left over from before v0.25. It now says "only at conversations, not recommended nodes".
  - Try: read §10.3.2 of [requirements.md](requirements.md).
  - Not verified: nothing to run. Until the switch is lifted, the default build still searches summaries only.
- **Session network: explorations keep their place** (behind `MINDWEAVE_SESSION_NETWORK=1`; **migration v18**; D-035 option C; [session-network.md](design/session-network.md) §16).
  - The spatial view saves each exploration's place and turn in `exploration_place`; nothing needs saving by hand. The molecule inside an exploration is laid out again on every load.
  - An untouched exploration stays where it was. A changed one is redrawn in its place and moves to the nearest free place only when it has outgrown it. A new one goes beside its neighbour in time, with 120 units of room to grow.
  - Measured on 1,367 simulated single-branch additions: the changed exploration moved more than 100 units in 3.8%, at most about 600; no other exploration moved; nothing overlapped. 1,000 dots in 220 explorations with saved places lay out in about 60 ms.
  - The migration runs without the switch: a build from here moves the data to v18 when it first opens it, after a v17 backup. Older builds cannot open v18 data.
  - Try: `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial`.
  - Not verified: Windows; an extremely spread-out workspace, where a coarse packing grid can still push an untouched exploration.
- **Requirements v0.25 and architecture §11: the network of every conversation** (documents only; no change on screen; D-035).
  - Every conversation is a dot; a summary is a ring on it and opens from it. Lines are real lineage; the time thread joins explorations. There is a spatial view and a timeline view.
  - "Stable layout" means no large sudden shifts: each exploration keeps its place, saved automatically, and only one that has outgrown its place moves. Explicit saving, arranging and dragging go.
  - Recommended nodes count conversations; summary links are quiet until active; link suggestions never pair a summary with its own line of descent.
  - The glossary has exploration, the two views, the time thread and weight. The term "summary network" stays.
  - The text runs ahead of `main` until `task/network-stable` merges and the switch is lifted: `exploration_place` (schema v18) and `ExplorationPlace` are not in `main`, the code still says `SessionWeights`, and the default build still has the summary-only view with explicit layout saving.
  - Try: read §7.3 and §7.4 of [requirements.md](requirements.md) and §11 of [architecture.md](architecture.md).
  - Not verified: nothing to run. If the owner changes v18, the architecture text changes in its own commit that the owner confirms.
- **Session network round 3: quiet links, better suggestions, fewer reloads** (no migration; D-035; [session-network.md](design/session-network.md) §8, §16).
  - Behind `MINDWEAVE_SESSION_NETWORK=1`: a summary link is a thin, quiet line with no label. Its note shows while you point at it or select it. A selected link, or a dot with links, brings out its ends and fades the rest. The legend names the line.
  - Behind the switch: a conversation change reloads only its own workspace's network page, where it used to reload every visited workspace.
  - Not behind the switch: link suggestions never pair a summary with one it branched from, directly or further up. The model is told each summary's exploration and what it branched from, asked to prefer pairs across explorations, and any pair on one line of descent is dropped; stored suggestions of that kind are no longer offered. Both prompts changed.
  - Not behind the switch: the network page also ignores summary and visibility changes of conversations in other workspaces.
  - D-035 records your choices for finishing the network.
  - Try: `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial`.
  - Not verified: Windows; suggestions with a real model.
- **Session network: the spatial view is a molecule layout** (behind `MINDWEAVE_SESSION_NETWORK=1`; no migration; [session-network.md](design/session-network.md) §6.1, §15.5).
  - Each exploration is a molecule. Lines spread round each dot by the size of the branch behind them, so two lines run straight and three sit at 120°. Turns alternate so chains never curl. Each line is long enough for its branch to fit its wedge, so lines do not cross.
  - Explorations keep gaps. The heaviest sits in the middle; the others follow outward in time order, each beside its neighbour in time.
  - Titles are whole, wrapped to about nine wide characters a line, and placed in each dot's widest free angle. A root's date sits under its title. Hit areas, "Show all" and recommended nodes follow the placed titles.
  - `SpatialLayout` replaces `SpatialPacking`. The owner judged the prototype in [prototypes/molecule-layout](../prototypes/molecule-layout/README.md) from pictures first.
  - Try: `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial`.
  - Not verified: Windows; two or more explorations with hundreds of direct branches each are slow (about 4.5 s for two of 500).

### 2026-11-22

- **Free trial adopted as evolution D9, with no schedule** (documents only; no change on screen; D-034).
  - The draft moved from `docs/ideas/` to [docs/design/free-trial.md](design/free-trial.md), updated from the owner's technical discussion: the request flow, why the installer needs no secret, conservative starting limits, hosting on the owner's Singapore VPS with a C# relay and SQLite counters, server size, provider terms to check, development cost and open decisions with defaults.
  - [evolution.md](evolution.md) has D9; the design index has a Model access group.
  - Try: open [docs/design/free-trial.md](design/free-trial.md).
  - Not verified: nothing is built. Prices, DeepSeek's terms, turning thinking off, and reachability from mainland China are still to check.
- **Design documents by standing; first drafts of next-phase ideas** (documents only; no change on screen).
  - `docs/ideas/` is new: thinking that is not agreed. It holds four first drafts: a free trial, more platforms, reply quality, and more directions. None is requirements or design.
  - 14 finished-stage designs, executed plans and the stage reports moved from `docs/design/` to [docs/history/design/](history/design/README.md). `docs/design/` keeps 22 current files, and its index is in six groups.
  - AGENTS.md says what each folder means; a design or plan moves to history in the merge that finishes its work.
  - Older review batches and the track record keep the old `docs/design/` paths.
  - `DocsRulesTests` checks links in `docs/ideas/` too. The source package leaves out both new folders.
  - Try: open [docs/ideas/README.md](ideas/README.md) and [docs/design/README.md](design/README.md).
  - Not verified: the ideas themselves. Nothing in them has been built or run.
- **Version 0.2.0; the build is named in settings** (no migration; D-033).
  - The line at the bottom of Settings now reads like "Mindweave 0.2.0 · 5f50734 · 2026-11-22 · 数据通道 claude-auto" in daily and dev builds; a release shows only "Mindweave 0.2.0".
  - The version is raised at each finished plan step.
  - Try: `scripts/daily.sh publish`, then look at the bottom of Settings.
  - Not verified: on Windows (the commit date there goes through a batch file; a test checks it on CI).
- **Maintainability.**
  - Failure handling has written rules ([failure-handling.md](design/failure-handling.md)), and `CatchPolicyTests` checks them in the source.
  - 16 desktop catches that left no trace now log through `DesktopLog`.
  - The settings page saves through one helper, and saves stay in order.
  - Four unexpected-failure messages no longer show the exception text: creating or renaming a workspace, the copy before a restore, and export.
  - The design and review indexes are complete, and `DocsRulesTests` keeps them complete and checks links in living docs.
  - The requirements and architecture state no progress.
  - Prototype screenshots are no longer in git (D-032).
  - STATUS.md is split into this file, the decision log and [workstation.md](design/workstation.md) (D-032).
  - The source package passes its check again at 2.8 MB (it was 12.8 MB). Two older faults in the check were fixed: since merge 3 a long file list made it report files as missing, and a test named the daily channel.
  - Try: `scripts/test.sh full`. Nothing on screen changed except the four failure messages.
  - Not verified: the four new messages in a real window.
- **Bilingual product, merge 3** (no migration; [bilingual-ui.md](design/bilingual-ui.md)).
  - Model prompts, tool descriptions and model-facing phrases are sent in the interface language. Prompts are one file per language under `Prompts/en` and `Prompts/zh`.
  - The Chinese prompts are unchanged and pinned by captured-prompt tests.
  - English length limits count words; text with any Chinese keeps the character limits.
  - Not verified:
    - English prompts on a real model. You decided on 2026-11-22 to check them in daily use, not with a paid run (D-031).
    - English short names on the summary network canvas.
    - The language switch in a real window.
    - Windows.
  - Six minor issues are deferred in §13 of the design.
- **Session network, rounds 1 and 2**, behind the switch `MINDWEAVE_SESSION_NETWORK=1` (no migration, nothing saved; §15 of [session-network.md](design/session-network.md)).
  - Every conversation is a dot, sized by the questions asked, with a timeline view and a circle-packed spatial view.
  - Without the switch, the summary network is unchanged, except that mouse-wheel zoom is on by default.
  - You tried it and said it can pass; the spatial arrangement is to be redone.
  - Try: `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial`.
- **Answer typography** (D-027, no migration; prototype `prototypes/text-spacing`).
  - Answer body text has a 24 px line height and 10 px between blocks.
  - Bold is Medium weight in a muted colour, chosen under 设置 › 外观 › “加粗颜色” (Settings › Appearance › Bold colour).
  - You agreed after screenshots.
  - Not seen on Windows, where Microsoft YaHei has no Medium face and coloured bold draws at regular weight.
- **Bold beside CJK punctuation** (D-026, render profile `cjk-emphasis-1`, no migration): `的**“火箭号”**在` shows as bold in answers and summaries. You agreed after before/after screenshots.
- **Bilingual product, merge 2** (look approved by you).
  - English is the default, with a language choice in Settings › Appearance and on the first onboarding screen.
  - A switch rebuilds the view in place; running answers, approvals and summary generation continue.
  - The choice is kept in the file `language` in the data folder, so daily data without it opens in English.
- **Tests.**
  - The two quarantined tests are back. The attachment time-out was an app race: a paste stored while the page read its draft showed as two chips, now fixed in the composer. The summary link test retries when its two presses do not make a double click.
  - The `WebSearchUiTests` approval-card flake was the test clicking before the text above the card was laid out. Now 0 of 16 under `--load`; it was 3 of 8.

### 2026-11-21

- **Bilingual product, merge 1**: every interface string in English and Chinese.
- **Answer output limit** under 设置 › 模型连接 › 高级设置 › “输出上限” (requirements v0.23 §9.6, no migration; 256 to half the context capacity, empty sends none). Not tried with a real model.
- **Reasoning and run status** (requirements v0.21, no migration; [reasoning-and-status.md](design/reasoning-and-status.md)).
  - While an answer is in progress, reasoning shows in a collapsed “思考过程” (thinking process) section that disappears when the answer ends and is never saved.
  - One status line at the end of the answer names each stage.
  - New motions: "reaching" (tools) and "holding" (waiting for approval).
  - You agreed after screenshots. Not tried with a real reasoning model.
- **Docs in English.** The living docs were translated; requirements v0.22 removed the stale §5.4 non-goal "AI execution reports and decisions".
- **Windows 11, real hardware**: full run passed at daabd5b over the home network (Core 115, Architecture 10, Infrastructure 947 + 24 skipped, Desktop 612 + 83 skipped).
  - The two failures of the 0233689 run (2026-11-20) were fixed first: the workspace badge pulse race (5 runs in a row passed on the real machine) and the scroll margin of the highlight-line test.
- **CI.**
  - Occasional failures were fixed, the last at da3348a: the MCP path and process on Windows, a database file still open, the Chinese fallback font shortening answers, a tooltip covering a test's click, and timing on slow machines.
  - The Linux CJK font package is cached.
  - From here on, a merge counts as done only once CI passes.

### 2026-11-20

- **Run loop improvements L1–L11** (requirements v0.20, no migration; section 8 of [agent-loop.md](design/agent-loop.md)): the run note and the limit line, one retry after an occasional error, answering directly when a middle round is truncated, 25% of the context kept for the run, read-only calls in parallel, repeated calls not executed again, eviction by citation, context in search terms, an index of earlier material, and attachments and summaries in the citations.
  - The real evaluation is in [loop-steps.md](reviews/run-eval-20261119/loop-steps.md). You decided to verify it in daily use.
- **CI** on `main` (Linux + Windows) passes again (a2ebf62) after failing since 2026-11-17.
- **Repository cleanup**: personal paths and relay addresses removed from current files; history kept (D-025).

### 2026-11-19

- **Agent platform phase 4, third-party tools (MCP)** (requirements v0.19 §10.4.6, no migration; M1–M18 and Q15 in [mcp.md](design/mcp.md)).
  - 设置 › 工具 › MCP 服务器 (Settings › Tools › MCP servers) adds a local command or a URL.
  - Tools are enabled one by one, and every call with side effects is approved.
  - Servers end when the app exits.
  - You tried an npx server on macOS.
- **Generating indicator** (no migration; [generating-indicator.md](design/generating-indicator.md)): a three-dot "weaving" motion per stage replaces the progress bar. You agreed after the animation and screenshots. Not tried with a real model.
- **Web reading through a system proxy** (W8 revised): fake-IP answers 198.18.x.x no longer count as local.
  - Tested under Clash Verge: Wikipedia, arXiv, GitHub, government sites and Sina could be read.
  - Baidu Baike returns a security-check page.

### 2026-11-17

- **Agent platform phase 3, web search and page reading** (requirements v0.18 §10.4.5, no migration; W1–W16 in [web-search.md](design/web-search.md)).
  - Tavily search.
  - Page reading with local and private addresses blocked.
  - Numbered citations and a citation list.
  - Approval for URLs the model made up.
  - The globe switch 自动／开／关 (Auto / On / Off).
- **Agent platform phase 2, runs and tool calls** (migration v17, requirements v0.17 §10.4.4; R1–R10 in [runs-and-tools.md](design/runs-and-tools.md)).
  - Two local tools: “检索附件” (search attachments) and “检索总结网络” (search summary network).
  - Activity text above the answer.
  - 设置 › 工具 (Settings › Tools).
- **Agent platform phase 1, resources and attachments** (migration v16; [attachments.md](design/attachments.md)).
  - "+" in the input box; long pastes become attachments; PDF, DOCX and images.
  - Full text or a digest plus relevant parts is sent, by context capacity.
  - You tried it item by item on the macOS trial build 367bcca and it passed. Not tried on Windows.
- **Icon direction A**: rounded line icons generated from `Themes/IconStrokes.xml` by `scripts/icons/outline-icons.cs`; corner radii from `Themes/Radii.axaml`.
- **Agent platform design**: requirements v0.16 §10.4 and architecture §15; resource storage (migration v15).
- **Model capabilities** under 设置 › 模型连接 › 高级设置 (Settings › Model connection › Advanced settings).
- **Direction 1 batch.**
  - Reading starts from the first sentence of new content.
  - A context ring beside the input box.
  - A daily usage calendar.
  - A conversation started from a recommendation links its first summary to the basis with “来自推荐” (From a recommendation) (requirements v0.14, migration v14).
  - Onboarding (requirements v0.15).
- **Daily build** c806fbd (schema v16) published; the backup from before publishing is in `~/Documents/Mindweave-daily-backups/`.
- The third-party notices include the full texts of Apache-2.0 and MIT.

### 2026-11-16

- **One line of development.**
  - The Claude track is merged into `main`, and the Codex track stopped at the tag `archive/codex-steady-final`.
  - The Codex working directory's untracked records (`.superpowers/`, 802 MB) were moved to `../mindweave-archive/codex-steady-superpowers`.
  - You judged that the course's technical requirements are met; the course submission version is the tag `course-submission`.
- **Context compression** keeps each requirement or prohibition together with its object and condition, and the compression window went from 7,500 to 20,000. DeepSeek passed 3 times in a row.
- **Windows 11, real hardware** (08ead83): full run 1,126 passed, 97 skipped.
  - The Release build starts on first launch and shows only "Mindweave 0.1.0".
  - Its data is in `%LOCALAPPDATA%\Mindweave\release`.
  - Stress results are from this run.
