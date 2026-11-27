# Detailed design

This directory records the choices that the handoff document lets implementers refine on their own (written on the Claude track before 2026-11-16; see “分支” (Branches) in [AGENTS.md](../../AGENTS.md)). Product behavior follows the [requirements](../requirements.md) and the [architecture](../architecture.md). Where this directory conflicts with them, they win, and the conflict is recorded under “Open decisions” in [STATUS.md](../../STATUS.md).

**What belongs here.** Only documents that describe the product as it is built, or as the owner has approved it to be built, and the engineering rules in force. Each file has one row in a group below.

- Thinking that is not agreed goes to [ideas](../ideas/README.md). It moves here when the owner adopts it and work is about to start.
- A design whose stage is finished, and an execution plan whose work has merged, moves to [finished designs and plans](../history/design/README.md) in the merge that finishes it. It is then a frozen record.

## Conversation and data

| Document | Content |
| --- | --- |
| [Data relationships](data-model.md) | Stable identity, source dependencies, the frozen ancestor range, version ownership, transaction and deletion boundaries; each stage adds its tables step by step through migrations |
| [Resource storage](resources.md) | The internal storage foundation for resources (evolution D5 phase 1): concepts, tables and blobs, with no interface of its own |
| [Attachments (agent platform phase 1)](attachments.md) | Migration v16 (provenance markers, chunking and retrieval, draft attachments, request purpose), PDF / DOCX extraction, context planning at the resource layer and attachment digests, backups before deletion that include the originals, the input box attachment UI; differences from the architecture text |

## Agent platform

| Document | Content |
| --- | --- |
| [Model capabilities](model-capabilities.md) | Research on image input and tool calling per service (official documents and sources), preset rules, 设置 › 模型连接 › 高级设置 (Settings › Model connection › Advanced settings), `ActiveModelHasAsync` as the only query features use, items waiting for verification with real calls |
| [Runs and tools (agent platform phase 2)](runs-and-tools.md) | This phase's new decisions R1–R10 (confirmed by the user 2026-11-17), migration v17 (run steps, snapshots of tools and limits), the run loop, tool calling in Chat Completions, two local tools, 设置 › 工具 (Settings › Tools) and statistics, items waiting for verification with real services |
| [Web search and citations (agent platform phase 3)](web-search.md) | This phase's new decisions W1–W16 (confirmed by the user 2026-11-17), the Tavily adapter, URL checks for page fetching, approval cards, citation numbering and locating, 设置 › 工具 › 网络搜索 (Settings › Tools › Web search), items waiting for verification with real services |
| [Third-party tools (agent platform phase 4, MCP)](mcp.md) | This phase's new decisions M1–M18 (accepted by the user 2026-11-19), the MCP client package, server settings and keys, starting and restarting processes, enabling tools and definition snapshots, approval cards, untrusted results, the fake server for tests |
| [Run loop improvements](agent-loop.md) | A walkthrough of the run loop, the problems found P1–P10, improvements L1–L12 (run prompt, budget line, concurrent read-only calls, retry, leaving room, eviction by citation, material index, local citations, evaluation); implemented and merged 2026-11-20; §8 lists what is left |
| [Reply quality](reply-quality.md) | Approved 2026-11-26 (D-043), not built: answer instructions for every answer with a branch part, the personal note, the titles of matching summaries, stop and refine rules, the best-matching passages of a long page, summaries by concept, the evaluation with cost and time; no suggested questions under an answer, no quiz or guided mode; three increments |

## Network

| Document | Content |
| --- | --- |
| [Summary network: every conversation a dot, and the timeline](session-network.md) | Conversations as dots, the spatial view and the timeline, quiet links, exploration places; what the owner decided |
| [Return and review](return-and-review.md) | Approved 2026-11-26 (D-044), not built: a recap for a rested, heavier conversation (key points and questions left open), where it shows on coming back (the branch entry, new exploration, the network's details and a dashed ring), Extend on a chosen dot, a summary of a conversation with its branches, failure cases, acceptance scenarios and five increments |

## Sharing

| Document | Content |
| --- | --- |
| [Sharing a thinking path](sharing.md) | Approved 2026-11-25 (evolution D8), step 1 built (0.6.13, 0.6.15): a share as one HTML file with the exchange data inside, structure only by default, what never leaves, the page a reader sees, import and continuing, copies between the user's devices, acceptance scenarios and increments |
| [Sharing, step 2: import and continue](sharing-import.md) | Approved 2026-11-25, built (0.6.18, 0.6.19): a structure-only share arrives as empty conversations; a shared file only adds; a received conversation is read-only and the user's questions attach as conversations of their own; the rules of comparison, the migration, acceptance scenarios and increments 2a and 2b |
| [Sharing, step 3: the manual backup](sharing-backup.md) | Approved 2026-11-26, built (0.6.26, 0.6.27): everything in all workspaces as one package file the user keeps; the same operation as sharing with a wider scope; not the app's automatic backups and not sync: importing only adds, and what differs keeps this device's value; a conversation continued on both sides is joined by time; no migration; acceptance scenarios and increments 3a and 3b |

## Security and diagnostics

| Document | Content |
| --- | --- |
| [Security, diagnostics and upkeep](diagnostics-and-upkeep.md) | Approved 2026-11-27 (evolution D2, D-042), not built: what a failed request shows, the diagnostics page and package, removing keys and keys left behind, erasing deleted content for good, the renderer guard, failure cases, acceptance scenarios and five increments; the standing of the health check's open rows and the upkeep decisions |

## Model access

| Document | Content |
| --- | --- |
| [Free trial](free-trial.md) | Adopted 2026-11-22 (evolution D9), not built: a relay on the owner's VPS, anonymous install tokens with proof-of-work, per-install and global caps, the request flow, starting limits, hosting, the app changes, cost and open decisions |
| [Ask from anywhere, connections, search and caching](ask-anywhere.md) | Approved 2026-11-27 (D-045), not built: quoting with notes from a conversation, from another application and from the screen, with the app staying in the menu bar; several connections with one current model, a model for background work, a local server and the Messages and Responses protocols; search by meaning with an embedding model on a saved connection; cached tokens recorded, a stable request order and cache points; what is set aside (browser extension, MCP server, command line, a model inside the app) |

## Interface

| Document | Content |
| --- | --- |
| [Interface language](bilingual-ui.md) | English and Chinese text side by side, the language choice and switching in place (requirements §10.5, architecture §3.5) |
| [Generating indicator](generating-indicator.md) | The three-dot "weaving" motion shown while answers and summaries are generated |
| [Reasoning and run status](reasoning-and-status.md) | The collapsed “思考过程” (thinking process) section while an answer runs, and the one-line run status at its end |

## Engineering

| Document | Content |
| --- | --- |
| [Run guide](run-guide.md) | Building, running and testing; the data directory and releases |
| [Daily build](daily-build.md) | The developer's everyday macOS build and its separate data |
| [Version numbers and the change log](versioning.md) | In force since 2026-11-23 (D-036): the third number rises with every merge into `main` and the second at a milestone the owner calls; tags for the five finished phases 0.1–0.5; one change-log section per version with one line per change |
| [Test organization and health](testing.md) | Fast / full commands, categories, time budgets, isolation rules for flaky failures and their common causes |
| [Failure handling](failure-handling.md) | Where catches belong, that a handled failure always leaves a trace, cancellation, no programmer-error types as signals, `async void` only in view handlers; checked by `CatchPolicyTests` |
| [Evolution seams](evolution-seams.md) | The form E1–E6 took, their cost and findings; the E5 exchange format specification |

## Logs

| Document | Content |
| --- | --- |
| [Decision log](decisions.md) | Parameters and library choices that handoff §4 authorizes implementers to decide, and the owner's decisions, one paragraph each |
| [Development machines](workstation.md) | The owner's Mac and Windows hardware: data folders, how to reach them, the mailbox, what agents may do |
