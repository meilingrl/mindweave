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

## Network

| Document | Content |
| --- | --- |
| [Summary network: every conversation a dot, and the timeline](session-network.md) | Conversations as dots, the spatial view and the timeline, quiet links, exploration places; what the owner decided |

## Sharing

| Document | Content |
| --- | --- |
| [Sharing a thinking path](sharing.md) | Approved 2026-11-25 (evolution D8), increment 1a built (0.6.13): a share as one HTML file with the exchange data inside, structure only by default, what never leaves, the page a reader sees, import and continuing, copies between the user's devices, acceptance scenarios and increments |
| [Sharing, step 2: import and continue](sharing-import.md) | Approved 2026-11-25, not built: a structure-only share arrives as empty conversations; a shared file only adds; a received conversation is read-only and the user's questions attach as conversations of their own; the rules of comparison, the migration, acceptance scenarios and increments 2a and 2b |

## Model access

| Document | Content |
| --- | --- |
| [Free trial](free-trial.md) | Adopted 2026-11-22 (evolution D9), not built: a relay on the owner's VPS, anonymous install tokens with proof-of-work, per-install and global caps, the request flow, starting limits, hosting, the app changes, cost and open decisions |

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
