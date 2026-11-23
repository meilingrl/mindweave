# Finished designs and plans

Frozen records, moved here from `docs/design/` on 2026-11-22: the designs of finished stages, execution plans whose work has merged, and the stage reports. They are kept as written (AGENTS.md "Language and writing") and are not current to-dos. Documents written before the move cite these files under `docs/design/`.

| Document | Content |
| --- | --- |
| [S0 skeleton and storage](s0-skeleton-storage.md) | Projects and dependencies, data directory and track isolation, single instance, SQLite connection / migrations / backup / restore, three-layer navigation and ViewLocator |
| [S1 real chat](s1-chat.md) | Generation coordination, message state machine, Chat Completions client, credentials |
| [S2 branching and return](s2-branching.md) | Migration v4, the create-branch transaction, context assembly, anchors and rendering, E1 viewer slots, return and reading position |
| [S3 context budget and compression](s3-context.md) | Budget and estimation, required items and chunking, reuse and ownership of context summaries, compression inside the answer's execution slot, failure and overflow, migration v5 |
| [S4 summaries and versions](s4-summaries.md) | Migration v6, summary nodes and versions, candidates and the adoption baseline, edit drafts, version deletion and the “上一保留版本” (previous kept version), read detection, node markers, two-level naming |
| [S5 summary network](s5-network.md) | Short node names, projection and path edges through unsummarized conversations, deterministic layout, saving new nodes directly, explicit save and revision numbers, leave prompts, viewport rules |
| [S6 wrap-up](s6-wrapup.md) | Archiving, deletion, export, exit order, fault injection and the course delivery checks (frozen record, in Chinese) |
| [S6 Windows checklist](s6-windows-checklist.md) | The real-hardware acceptance list for Windows, one row per requirements §11 scenario (frozen record, in Chinese) |
| [S7 prototype visual language](s7-prototype-visual-language.md) | The visual rules of the approved network prototype, taken value by value from its code (frozen record) |
| [S7 UI polish](s7-ui-polish.md) | The rounds of interface polish after S5, each approved from screenshots (frozen record, in Chinese) |
| [Stage reports](stage-reports.md) | How to try each stage, what was verified and what was not, up to 2026-11-16; frozen, in Chinese. Later merges are in [docs/changes.md](../../changes.md) |
| [Interface language: implementation plan](bilingual-ui-plan.md) | Task-by-task plan for the bilingual product, merges 1 to 3 |
| [Session network round 1 plan](session-network-plan-round1.md) | Task-by-task plan for sessions as dots and the timeline |
| [Session network round 2 plan](session-network-plan-round2.md) | Task-by-task plan for the readable timeline, session details and circle packing |
| [Version numbers and the change log: implementation plan](versioning-plan.md) | Task-by-task plan for raising the version with every merge and the change log by version |
