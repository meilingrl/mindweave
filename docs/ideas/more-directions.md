# More directions

**Status:** first draft, 2026-11-22. Not agreed. Proposed by Claude at the owner's request, beside the owner's three directions ([free trial](../design/free-trial.md), [platforms](platforms.md), [reply quality](reply-quality.md)). Each is one paragraph and a first step; none has been designed.

## Directions

### 1. Ask from anywhere

A global hotkey opens a small ask window over any application. Text selected in any application can be sent to Mindweave as a question or as a branch. A browser extension does the same for a web page.

**First step:** a quick-ask window on macOS that drops the question into the current conversation.

### 2. Voice and camera

Speak a question, hear the answer, or photograph something and ask about it. Images are already resources; audio is the planned later phase of the agent platform ([evolution.md](../evolution.md) D5).

**First step:** push-to-talk input with the operating system's speech recognition, which needs no extra service.

### 3. Fully offline and private

A local model for answers, and local embeddings so that searching one's own summaries works by meaning with no service. It also gives a zero-setup start beside the free trial.

**First step:** an embedding index over summaries with a small on-device model, behind the existing keyword fallback.

### 4. Bring your history in

Import conversations exported from other assistants as ordinary conversations, which can then be branched and summarized. It builds on the exchange format (seam E5).

**First step:** one importer that maps one export format into E5.

### 5. Answers that show

Diagrams, timelines, charts and maps as controlled blocks inside an answer. Each can be selected for branching, like text. evolution.md names this as the low-cost start of D5; the map connects to D6.

**First step:** diagram and table blocks with anchors.

### 6. Gentle return

When the user comes back, Mindweave shows the open questions and the edges of earlier explorations. There is no review schedule to manage. This is among the ideas of the D8 discussion.

**First step:** an "unresolved" mark that the model sets at the end of an answer, shown as an edge in the summary network.

### 7. Mindweave as something others build on

The exchange format made real, a command-line entry to Core and Application, and Mindweave as an MCP server so other agents can read a user's summary network. The MCP client side already exists.

**First step:** an MCP server with one read-only tool, "search this workspace's summaries".

## Technical pieces worth publishing

Simple features with reference value for other people.

| Piece | What is worth showing | First step |
| --- | --- | --- |
| Anchors on streaming Markdown | Selecting text in an answer, branching from it and returning, with positions never guessed. Already built. | Extract it as a small library with a write-up |
| Branch-aware prompt caching | A conversation tree is a prefix tree. Sibling branches can share a cached ancestor prefix. evolution.md D4 points here. | Log cached tokens per request; order the context with frozen material first; publish the numbers |
| Evaluation set for small-budget agents | The run evaluation exists. A public scenario set with reports per model is rare. | Publish the scenarios and one report |
| An open format for branched conversations | No common format exists. E5 could be one; sharing and the manual backup use it, and sync would build on it. | Implement E5 export and import |
| Joining what two devices asked | When a manual backup meets a device that also went on, questions in one conversation join by time ([sharing-backup.md](../design/sharing-backup.md) V2). This is not sync: the owner's position (2026-11-27, D-043) is that real sync is networked and needs a server (evolution D10). | Write it up once the manual backup is built |
| Molecule layout | A layout by structure in which adding a conversation redraws only its own exploration. Built (0.6.0). | Write it up |
| The development method | A tested desktop application built mostly by agents, with human gates, a mailbox to a Windows tester, and rules for parallel agents. | A public write-up from AGENTS.md, the decision log and the stage reports |

## How the directions connect

- The **relay** of the free trial is also what the Web target needs.
- The **exchange format** is under sharing (D8), import and the manual backup; sync (evolution D10) would build on it.
- **Local models** serve both the zero-setup start and privacy.
- **Follow-up suggestions** (reply quality) and **gentle return** both make branching the natural next action, with nothing to manage.
