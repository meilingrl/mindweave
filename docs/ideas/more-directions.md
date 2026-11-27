# More directions

**Status:** first draft, 2026-11-22; last changed 2026-11-27, when the directions that have been decided moved out (owner: what is decided leaves this folder). Not agreed. Proposed by Claude at the owner's request, beside the owner's three directions ([free trial](../design/free-trial.md), [platforms](platforms.md), [reply quality](../design/reply-quality.md)). Each is one paragraph and a first step; none has been designed. The numbers of the directions are kept, so 1, 3, 6 and 7 are missing below.

## Directions

### 2. Voice and camera

Speak a question, hear the answer, or photograph something and ask about it. Images are already resources; audio is the planned later phase of the agent platform ([evolution.md](../evolution.md) D5).

**First step:** push-to-talk input with the operating system's speech recognition, which needs no extra service.

### 4. Bring your history in

Import conversations exported from other assistants as ordinary conversations, which can then be branched and summarized. It builds on the exchange format, which sharing built ([sharing.md](../design/sharing.md)).

**First step:** one importer that maps one export format into the exchange format.

### 5. Answers that show

Diagrams, timelines, charts and maps as controlled blocks inside an answer. Each can be selected for branching, like text. evolution.md names this as the low-cost start of D5; the map connects to D6.

**First step:** diagram and table blocks with anchors.

## Moved out

| Direction | Where it is now |
| --- | --- |
| 1. Ask from anywhere | Adopted 2026-11-27 (D-045) as quoting: [ask-anywhere.md](../design/ask-anywhere.md). The browser extension is set aside there |
| 3. Fully offline and private | Adopted 2026-11-27 (D-045): local models through a local server, and search by meaning, in [ask-anywhere.md](../design/ask-anywhere.md). A model inside the app is not for now |
| 6. Gentle return | Adopted 2026-11-26 (D-044): [return-and-review.md](../design/return-and-review.md) |
| 7. Mindweave as something others build on | The exchange format is built with sharing ([sharing.md](../design/sharing.md)). An MCP server and a command line are set aside (owner, 2026-11-25; [ask-anywhere.md](../design/ask-anywhere.md) §6) |

## Technical pieces worth publishing

Simple features with reference value for other people. Publishing any of them is an outward-facing step the owner decides.

| Piece | What is worth showing | First step |
| --- | --- | --- |
| Anchors on streaming Markdown | Selecting text in an answer, branching from it and returning, with positions never guessed. Already built. | Extract it as a small library with a write-up |
| Branch-aware prompt caching | A conversation tree is a prefix tree. Sibling branches can share a cached ancestor prefix. Building it is adopted ([ask-anywhere.md](../design/ask-anywhere.md), D-045). | Publish the measured numbers once there are some |
| Evaluation set for small-budget agents | The run evaluation exists. A public scenario set with reports per model is rare. | Publish the scenarios and one report |
| An open format for branched conversations | No common format exists. The exchange format is built with sharing ([sharing.md](../design/sharing.md)). | Publish it as a specification others can implement |
| Joining what two devices asked | When a manual backup meets a device that also went on, questions in one conversation join by time ([sharing-backup.md](../design/sharing-backup.md) V2). This is not sync: the owner's position (2026-11-27, D-047) is that real sync is networked and needs a server (evolution D10). | Write it up once the manual backup is built |
| Molecule layout | A layout by structure in which adding a conversation redraws only its own exploration. Built (0.6.0). | Write it up |
| The development method | A tested desktop application built mostly by agents, with human gates, a mailbox to a Windows tester, and rules for parallel agents. | A public write-up from AGENTS.md, the decision log and the stage reports |

## How the directions connect

- The **relay** of the free trial is also what the Web target needs.
- The **exchange format** is under sharing, import and the manual backup, and under importing other assistants' history (direction 4); sync (evolution D10) would build on it.
