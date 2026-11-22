# Detailed design

This directory records the choices that the handoff document lets implementers refine on their own (written on the Claude track before 2026-11-16; see “分支” (Branches) in [AGENTS.md](../../AGENTS.md)). Product behavior follows the [requirements](../requirements.md) and the [architecture](../architecture.md). Where this directory conflicts with them, they win, and the conflict is recorded under “Open decisions” in [STATUS.md](../../STATUS.md).

| Document | Content |
| --- | --- |
| [Data relationships](data-model.md) | Stable identity, source dependencies, the frozen ancestor range, version ownership, transaction and deletion boundaries; each stage adds its tables step by step through migrations |
| [S0 skeleton and storage](s0-skeleton-storage.md) | Projects and dependencies, data directory and track isolation, single instance, SQLite connection / migrations / backup / restore, three-layer navigation and ViewLocator |
| [S1 real chat](s1-chat.md) | Generation coordination, message state machine, Chat Completions client, credentials |
| [S2 branching and return](s2-branching.md) | Migration v4, the create-branch transaction, context assembly, anchors and rendering, E1 viewer slots, return and reading position |
| [S3 context budget and compression](s3-context.md) | Budget and estimation, required items and chunking, reuse and ownership of context summaries, compression inside the answer's execution slot, failure and overflow, migration v5 |
| [S4 summaries and versions](s4-summaries.md) | Migration v6, summary nodes and versions, candidates and the adoption baseline, edit drafts, version deletion and the “上一保留版本” (previous kept version), read detection, node markers, two-level naming |
| [S5 summary network](s5-network.md) | Short node names, projection and path edges through unsummarized conversations, deterministic layout, saving new nodes directly, explicit save and revision numbers, leave prompts, viewport rules |
| [Model capabilities](model-capabilities.md) | Research on image input and tool calling per service (official documents and sources), preset rules, 设置 › 高级设置 (Settings › Advanced settings), `ActiveModelHasAsync` as the only query features use, items waiting for verification with real calls |
| [Evolution seams](evolution-seams.md) | The form E1–E6 took on this track, their cost and findings; the E5 exchange format specification; conventions handed to S2 |
| [Naming and structure](naming-and-structure.md) | One concept, one name (glossary and reserved words); stored names do not change with code renames; folder equals namespace; the AGENTS.md section and glossary proposal for `main`; the deferred full version of the rules |
| [Failure handling](failure-handling.md) | Where catches belong, that a handled failure always leaves a trace, cancellation, no programmer-error types as signals, `async void` only in view handlers; checked by `CatchPolicyTests` |
| [Run guide](run-guide.md) | Building, running and testing; the data directory and releases |
| [Daily build](daily-build.md) | The developer's everyday macOS build and its separate data |
| [Test organization and health](testing.md) | Fast / full commands, categories, time budgets, isolation rules for flaky failures and their common causes |
| [Attachments (agent platform phase 1)](attachments.md) | Migration v16 (provenance markers, chunking and retrieval, draft attachments, request purpose), PDF / DOCX extraction, context planning at the resource layer and attachment digests, backups before deletion that include the originals, the input box attachment UI; differences from the architecture text |
| [Runs and tools (agent platform phase 2)](runs-and-tools.md) | This phase's new decisions R1–R10 (confirmed by the user 2026-11-17), migration v17 (run steps, snapshots of tools and limits), the run loop, tool calling in Chat Completions, two local tools, 设置 › 工具 (Settings › Tools) and statistics, items waiting for verification with real services |
| [Web search and citations (agent platform phase 3)](web-search.md) | This phase's new decisions W1–W16 (confirmed by the user 2026-11-17), the Tavily adapter, URL checks for page fetching, approval cards, citation numbering and locating, 设置 › 工具 › 网络搜索 (Settings › Tools › Web search), items waiting for verification with real services |
| [Third-party tools (agent platform phase 4, MCP)](mcp.md) | This phase's new decisions M1–M18 (accepted by the user 2026-11-19), the MCP client package, server settings and keys, starting and restarting processes, enabling tools and definition snapshots, approval cards, untrusted results, the fake server for tests |
| [Run loop improvements](agent-loop.md) | A walkthrough of the run loop, the problems found P1–P10, improvements L1–L12 (run prompt, budget line, concurrent read-only calls, retry, leaving room, eviction by citation, material index, local citations, evaluation); implemented and merged 2026-11-20; §8 lists what is left |
| [Agent platform: open questions](agent-platform-questions.md) | Questions in requirements §10.4 and the architecture §15 draft that the user must decide, each with a recommendation; fixed details for alignment with parallel sessions |
| [Decision log](decisions.md) | Parameters and library choices that handoff §4 authorizes implementers to decide, and the owner's decisions, one paragraph each |
| [Development machines](workstation.md) | The owner's Mac and Windows hardware: data folders, how to reach them, the mailbox, what agents may do |
| [Resource storage](resources.md) | The internal storage foundation for resources (evolution D5 phase 1): concepts, tables and blobs, with no interface of its own |
| [Generating indicator](generating-indicator.md) | The three-dot "weaving" motion shown while answers and summaries are generated |
| [Reasoning and run status](reasoning-and-status.md) | The collapsed “思考过程” (thinking process) section while an answer runs, and the one-line run status at its end |
| [Interface language](bilingual-ui.md) | English and Chinese text side by side, the language choice and switching in place (requirements §10.5, architecture §3.5) |
| [Session network and timeline](session-network.md) | Sessions as dots, the timeline and the spatial view, behind `MINDWEAVE_SESSION_NETWORK=1`; rounds 1 and 2 merged 2026-11-22 |

## Plans and records

Execution plans are kept for traceability once their work has merged; they are not current to-dos. Finished-stage designs and stage-report entries before 2026-11-20 are frozen records, kept as written (AGENTS.md "Language and writing").

| Document | Content |
| --- | --- |
| [Interface language: implementation plan](bilingual-ui-plan.md) | Task-by-task plan for the bilingual product, merges 1 to 3 |
| [Session network round 1 plan](session-network-plan-round1.md) | Task-by-task plan for sessions as dots and the timeline |
| [Session network round 2 plan](session-network-plan-round2.md) | Task-by-task plan for the readable timeline, session details and circle packing |
| [Stage reports](stage-reports.md) | How to try each stage, what was verified and what was not, up to 2026-11-16; frozen, in Chinese. Later merges are in [docs/changes.md](../changes.md) |
| [S6 wrap-up](s6-wrapup.md) | Archiving, deletion, export, exit order, fault injection and the course delivery checks (frozen record, in Chinese) |
| [S6 Windows checklist](s6-windows-checklist.md) | The real-hardware acceptance list for Windows, one row per requirements §11 scenario (frozen record, in Chinese) |
| [S7 prototype visual language](s7-prototype-visual-language.md) | The visual rules of the approved network prototype, taken value by value from its code (frozen record) |
| [S7 UI polish](s7-ui-polish.md) | The rounds of interface polish after S5, each approved from screenshots (frozen record, in Chinese) |
