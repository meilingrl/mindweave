# Mindweave evolution directions and engineering guidance

Recorded 2026-10-26. The user proposed six later evolution directions; after discussion with Claude they were organized into this document, as engineering guidance for extensions after the first release. D7, asking with multiple quotes, was added on 2026-11-05, and D8, sharing thinking paths (initial vision), on 2026-11-20.

## Standing and force

- This document is **directional guidance**, not current requirements. The first-release scope, behavior and acceptance still follow [requirements](requirements.md) v0.11 and [architecture](architecture.md); this document does not change either of them.
- When a direction becomes formal requirements, it must be written into requirements and architecture after the user decides; the matching entry here then changes to a reference.
- Whether each track adopts this guidance, and how far, is in "Track adoption status" at the end. A track that has not adopted it is not bound by this document.

## 1. Overall principles

**The product's positioning does not change.** Mindweave is still a learning application centered on knowledge exploration: branching, returning, summarizing and reviewing are the core flows. Tool and agent capabilities serve exploration and do not change the nature of the product.

**Control extension cost with seams.** The main cause of scope getting out of control is not adding features. It is new features landing where no boundary was reserved, which forces existing models and flows to be rewritten. Extensions should land on known seams, so that the cost of each addition can be estimated. The current architecture already has the main seams: the dependency direction of the four projects, generation coordination decoupled from the View (§6), separate adapters for connection, model and protocol (§7.2), and knowledge content saved separately from layout (§11).

**Seams first, features after.** Each direction splits into two kinds of work:

- **Enabling seams (enabler)**: small changes to the data model or to boundaries. The user does not see them. Done early they are cheap; done late they need migration and rewriting (see Section 2).
- **Features (feature)**: built on top of the seams. They may be ordered freely by value and are delivered as themed versions.

**Make architecture rules executable.** Dependency direction, log redaction, performance budgets and the like are fixed by automated tests, so that the seams do not gradually stop working as features pile up.

## 2. Enabling seams

| ID | Seam | Content | Serves | Cost of not reserving it early |
| --- | --- | --- | --- | --- |
| E1 | Navigation state held per viewer slot | The current location, navigation stack, reading position, drafts and returning to the branch source belong to a "viewer slot"; the workbench holds a set of slots, and the first release has only one | D1 | Once the reading position and returning to the branch source are built for a single view, side-by-side reading needs navigation and persistence rewritten |
| E2 | Block-based message content | Message content is an ordered sequence of content blocks (text, tool call, tool result, citation, table / chart / diagram, and so on); the first release has only text blocks. A selection anchor is located by "block + position within the block" | D3, D5 | Anchors, summaries, export and context assembly are all built on a single body string; introducing tools then needs a full migration |
| E3 | Request tracing and redaction at the source | Each AI request records a request ID, the identity of the settings snapshot, timing, protocol status, and a truncated, redacted error excerpt; keys and user content are told apart by type, and the logging layer refuses to output them as they are | D2, D3, D4 | Compatibility and performance problems lack a reproducible basis; filtering text after the fact cannot reliably prevent leaks |
| E4 | Model capability description | Each model configuration declares its capabilities (streaming, tool calling, image input, context capacity, reasoning output, prompt caching, and so on); features decide by capability, not by provider or model name | D3, D5 | Provider special cases spread through new protocols and new features |
| E5 | Versioned exchange format | Import and export use a separate, versioned, documented format (JSON structure + Markdown body), decoupled from the SQLite table structure | D3 | Every change to the database structure breaks export compatibility |
| E6 | Performance baseline and architecture tests | Reproducibly generated large-scale test data (many conversations, deep branch trees, very long answers) and benchmarks; automated tests for dependency direction and redaction rules | D4, all | Performance problems can only be judged by feel; seams are bypassed unintentionally during iteration |

E1 and E2 are designed in the same period as the reading position, selection anchors and migration v3 in the requirements, so reserving them early has the lowest marginal cost.

## 3. The directions

### D1 Interface and interaction

**Goal:** Visual refinement and interaction improvements, including a two-pane conversation interface with adjustable width that supports side-by-side reading (for example, comparing with the parent answer while asking in a branch).

**Where it lands in the architecture:** Views and navigation in the Desktop layer. Generation is already independent of the View (§6) and can serve several visible conversations at once.

**Prerequisite:** E1. The hard part of two panes is not the splitter control. It is whether the "current conversation", the reading position, returning to the branch source and the drafts are isolated per viewer slot.

**Key questions:** Who owns the reading position and the draft when both panes show the same conversation; in which pane the branch source opens when returning to it; the fallback layout when the window is too narrow; whether the slot layout is restored with the conversation.

### D2 Security and diagnostics

**Goal:** Prevent leaks of API keys and of the content the user sends and receives, and defend against injection attacks; provide detailed logs and error information the user can view.

**Where it lands in the architecture:** Logging, credentials and protocol adaptation in Infrastructure; the diagnostics view in Desktop; the content rendering layer.

**Prerequisite:** E3.

**Key points:**

- **Keys:** System credential storage is already implemented per §7.3. The remaining leak surfaces are logs, error details, diagnostic export and project export; redaction must be done by type where the data is produced.
- **Rendering surface:** When model output is rendered as Markdown, remote images, automatically loaded resources and links may encode conversation content into a URL and send it out. By default, remote resources are not loaded, and a link shows its real target before it opens.
- **Injection:** While output is text only, the impact is mainly limited to the rendering surface. Once tools are introduced (D5), web pages, search results and tool output are all untrusted input, and the permission model must be delivered together with the tools.
- **Diagnostics:** Request details the user can view (status, duration, error category and a redacted excerpt), and a redacted diagnostic package that can be exported.

### D3 Compatibility

**Goal:** Support more AI models and protocols, support importing and exporting content, and cover more devices and platforms.

**Where it lands in the architecture, and prerequisites:**

- **Models and protocols:** Keep the protocol adapter boundary (§7.2) and declare capabilities with E4. Adapters such as Responses and Messages are added as new implementations without changing the business model.
- **Import and export:** E5 is the only external format; import from external tools is implemented as a mapping to that format.
- **Platforms:** Linux relies on Avalonia; the cost is mainly verification and packaging. Mobile is a different interaction design that reuses Core and Application; it is a separate product decision and is not handled as a compatibility item.

### D4 Performance

**Goal:** Improve message transfer, token caching, context management, memory and disk use, page loading, and database reads and writes.

**Method:** Measure first with E6, then optimize; benchmarks go into CI with budgets. The main costs are expected to be, in order: rendering and re-layout of very long Markdown (needs virtualization), context assembly for deep branch trees, and database access (WAL and suitable indexes are usually enough).

**Coupling with existing design:** Provider prompt caching depends on a **stable prefix**. Context assembly should put frozen material first (the ancestors' frozen ranges, reusable context summaries), then recent questions and answers and the current question. This interacts with the compression and context-summary reuse rules of §6 and should be designed together with them, not treated as later tuning.

### D5 Agent capabilities

**Goal:** Through the large model, introduce tool and skill calls and web search, and the creation of diagrams, charts and tables.

**Where it lands in the architecture:** Generation coordination in Application, the message model in Core, tool implementations and protocol adaptation in Infrastructure.

**Prerequisites:** E2, E3, E4.

**Structural changes:**

- **A message changes from a string into a sequence of content blocks** (E2).
- **Generation changes from a single request into a loop:** one answer may include several rounds of model calls and tool execution. "At most two AI requests globally", cancellation, late results and interruption recovery must be redefined at the loop level.
- **Anchors:** The anchoring and fallback behavior must be specified for a selection that falls on a tool result, a table or a chart.
- **Permissions and transparency:** What each tool call sent, and to whom, must be visible; content fetched from outside is treated as untrusted input (see D2).

**Low-cost starting point:** Diagrams, charts and tables can first be rendered as controlled code blocks (such as Mermaid, chart specifications, Markdown tables) without depending on tool calls; web search comes with clickable citations, consistent with the principle "sources are verifiable".

#### Direction formed in the 2026-11-13 discussion (accepted by the user, not yet written into requirements)

It started with long text pastes: the user proposed following DeepSeek and automatically turning long text into a `.txt` attachment, designed together with files, web search, multimodality and agent capabilities. The discussion extended D5 into the direction below. The user found it consistent with their idea and asked for requirements and architecture to be drafted separately. The original discussion (the English original and the drafting prompt prepared at the time) is kept in the development record `docs/history/agent-platform-discussion-20261113.md`, which is not shipped in the source package.

- **Resource:** Any material that enters a conversation or is produced in one is a resource: pastes, files, images, audio, web pages, tool output. It stores the original (large files on disk, not in SQLite), the media type, the provenance (who or what produced it, when, the URL), whether it is trusted, and derived forms (extracted text, chunks, digest, image description / OCR, audio transcript). What gets sent is decided by model capability (E4, for example `ImageInput`). A long paste is just a `.txt` resource without a name.
- **Content blocks:** A resource says "what it is"; a content block (E2) says "when and where it entered the conversation": text, attachment reference, tool call, tool result (pointing to a resource), citation marks, and later generated charts.
- **Context:** If a resource fits, it is sent whole and marked as a resource; if it does not fit, it is chunked, and the relevant chunks or the digest are taken (taking chunks by meaning needs an embedding service; without one, it falls back to local keyword retrieval). Resources are kept apart from conversation turns, so compression no longer meets a single message larger than the window; each resource's digest is made only once and reused.
- **Run loop:** One answer becomes one run: model → tool call → tool result → model … until a final answer is given or the step, time or token limit is reached. Every step is saved; an interrupted run is not automatically resumed or resent; steps with side effects are not repeated; "at most two AI requests" is counted per run instead. Each provider's native tool-call format is unified by the protocol adapter; tools are given only to models that support tool calling, and are not simulated with prompts.
- **Tools:** The registry records name, description, input format, risk level (local read-only / web read / has side effects), timeout and output limit. First "search attachments" and "search this workspace's summary network" (unique to Mindweave), then web search and web fetch (with citations); third-party tools connect through MCP.
- **Safety:** Tool results are treated as untrusted input (see D2); web fetch refuses local and private addresses and `file://`; keys never appear in tool arguments; every outgoing request is visible; operations with side effects must be approved by the user.
- **Interface:** One input box plus "+" (files, images); long pastes and images automatically become attachments and can be reverted to plain text; a web search switch (auto / on / off); options the current model cannot use are grayed out with the reason. A quiet one-line activity note in the answer (such as “搜索了 3 次 · 读了 2 个网页” ("searched 3 times · read 2 web pages")) that can be expanded to show each step; numbered citations support “回到来源” (back to source); approvals appear as small cards inside the answer, not as modal dialogs; advanced settings go in “设置 › 工具” (Settings › Tools); costs per tool and per run go into usage statistics.
- **Phases (each usable on its own):** ① resources and attachments: pastes, files, images (only for models that can see images), no tools; ② the run loop and tool calls, with only two local tools; ③ web search and web fetch, with citations, plus search service settings; ④ MCP client; ⑤ generated charts and audio. Not doing for now: a code execution sandbox, and agents that run in the background for a long time.
- **To be decided:** When writing requirements and architecture: each phase's acceptance scenarios, data model and migration, the behavior of selection branching and “回到来源” (back to source) on tool results and citations, and the questions the user must decide.
- **Drafted 2026-11-17:** Written as [requirements](requirements.md) §10.4 (v0.16) and [architecture](architecture.md) §15; the questions to decide are in [Agent platform: questions to decide](design/agent-platform-questions.md). Once the user confirms and it is merged, this section changes to a reference.

### D6 Long-term vision: map-based exploration

**Goal:** Combine a world map with the software, with places such as cities as nodes; the user can enter a place and carry out knowledge exploration and questions there.

**Where it lands in the architecture:** Of the same kind as the summary network (§11): another spatial projection of knowledge content, with layout and content saved separately.

**Core model change:** Add an optional, general **place anchor** (coordinates and a place identifier) to a workspace or conversation, without hard-coding "city" in Core; the place information can serve as the initial context for questions.

**How to verify:** The main uncertainty is in learning value rather than engineering feasibility. First build an independent prototype in `prototypes/` (an Avalonia map control such as Mapsui), and at the same time check map tile licensing and offline behavior, before deciding whether it enters the formal scope.

### D7 Asking with multiple quotes

Proposed by the user on 2026-11-05 and added after discussion with Claude.

**Goal:** The user can select several passages in the original text one by one; each passage becomes a numbered quote ([1], [2], [3] …), and the user then writes one prompt that refers to these quotes. It solves three things: asking about several points at once, pointing exactly at "which place", and not having to select a large block of unrelated text or paste the rest of the original text by hand in a branch (the burden in requirements §2.2 item 1).

**One composer, two uses:**

| | Create a branch | Revise a summary |
| --- | --- | --- |
| Where quotes come from | Messages in the parent conversation | The summary's current version |
| Result | A new branch with several anchors | A **candidate version**, which the user adopts or discards |
| Reuses | Branch creation, reading position and return | The candidate / adopt mechanism, "write a version only after success" (MW-C06) |

Asking with multiple quotes directly inside the current conversation (without opening a branch) can be a third use that reuses the same composer.

**Where it lands in the architecture:** Selection and the composer in Desktop; branch creation, summary generation and context assembly in Application; the anchor model in Core; storage in Infrastructure.

**Prerequisite:** E2 (anchors of block + position within the block).

**Constraints:**

- **A branch's quotes come only from the same parent conversation.** The parent conversation is still unique, and the shape of the conversation tree and the summary network does not change; quoting across conversations would turn the tree into a graph and must be decided separately.
- **Storage:** `branch_source` keeps the parent conversation and one main anchor, and existing reads do not change; a new child table stores the list of quotes in order, each with its own quoted text, anchor and version. The migration number is taken by merge order.
- **Return location:** Return to the reading position at the time the composer was opened, not to quote [1]; a branch entry mark is shown next to every quoted passage.
- **Display:** A sent message points back to the original text with quote marks, and does not disguise quoted text as content the user wrote (consistent with the similar requirement in requirements §6.5 for summary content).
- **Context capacity:** When the quotes and the prompt together exceed the capacity, the current rules prompt the user to remove or shorten quotes; nothing is silently truncated.
- **Summaries:** A quote points to the version the user is looking at; a revision only produces a new candidate version, so anchors do not need to survive in-place editing.

**Relation to the current requirements:** It extends "create a branch from selected text" from a single selection to several. Revising a summary uses a selection in the summary body, which touches MW-C04 (the first release does not create branches from a selection in the summary body), but the use is revision, not creating a branch. Turning it into formal requirements must be decided by the user and written into requirements and architecture.

**Key questions:** When selections are added one by one across several messages, how the selected quotes stay visible, removable and reorderable; the composer's layout when there are many quotes; whether the branch source shown in the summary network uses the main anchor or all quotes.

**Parts not adopted:** On 2026-11-05, "context annotations" for collaborative documents (author, reply threads, resolve / reopen, filters, diff review) were discussed at the same time. Mindweave is a single user's exploration with AI; selection → focused conversation → return is already handled by branches, and managing threads and lifecycles goes against the positioning of "not requiring the user to manage content", so it is not adopted. The worthwhile parts are this section.

### D8 Sharing thinking paths (initial vision)

Proposed by the user from 2026-11-19 to 11-20 and added after discussion with Claude. **This is an initial vision**: it records the idea, the scenarios and the principles settled in the discussion, for later reference. It is not a development specification; turning it into requirements must be decided by the user and written into requirements and architecture.

**How it started.** While discussing what makes Mindweave unique compared with general AI applications, the user talked about their own actual use: branching fully meets daily needs; the review value of the summary network is not obvious, partly because the summary network itself is not yet well designed, and partly because daily questions are mostly brief dips into a topic. The user thinks the meaning of the summary network may lie more in a sense of accomplishment, in sparking curiosity, and in **sharing with people who have not yet asked these questions**.

**Core judgment (the user's):** In an era when answers are so plentiful that they have become cheap, what is truly meaningful is the thinking process: what was asked, how the questions went on step by step, and where things suddenly became clear.

**Five scenarios (the user's original meaning):**

1. **Exam review → classmates.** When preparing for exams, everyone tends to ask similar questions. Share with classmates to exchange directions for review, while each person explores topics beyond the course in their own branches; when classmates also use Mindweave, the user also wants to see the paths they share and what they are all asking.
2. **Casual curiosity → companions on the trip.** On an outing with friends, seeing interesting animals or plants at a zoo or botanical garden, note the species met and learn about them, then share this path with the companions on the trip, so everyone learns something new together.
3. **Being a little teacher → students or family.** To pass on things from one's own field of study, share with "students" or family, to help them grasp the basic concepts, get started more easily and build a structured understanding.
4. **Casual talk and reflection → partner.** When chatting with AI about life or digging into philosophy, share with one's partner, so they understand what is troubling the user and also witness the moment the user figures it out.
5. **Progress report → advisor or report material.** When preparing a progress report, share with the advisor, or turn it into report material. The advisor can clearly see what has been researched in these days, how much was asked each day and what was learned, rather than just "I asked AI some questions".

**Principles settled in the discussion (the user, 2026-11-20):**

- **Do not split features by scenario.** The scenarios explain the value of sharing; they do not each correspond to a view or module. Features stay few and unified, avoiding complexity and fragmentation.
- **Choosing the paths or nodes to share** is a light choice made when sharing, not a separate view and not a main module.
- **No side-by-side comparison of two people's summary networks.** Different people's summary networks differ greatly and almost never overlap; if two people share the same summary network, side-by-side comparison is unnecessary.
- **The timeline is a standard view that is always available**, usable at any time, not something generated temporarily per scenario. (The timeline view is not yet written into requirements and architecture.)
- **The form of sharing at this stage is one-way publishing:** after the user chooses the content to share, a page or file that anyone can open is generated; the recipient does not need to install Mindweave and needs no account and no server. Importing between Mindweave users and a hosted community will be discussed later, depending on the actual response to one-way sharing.

**Relation to other directions from the same discussion.** The same discussion raised three related directions: the summary network and branches themselves (for example, bringing back one or two key points when returning from a branch, answers citing the user's own existing summaries, showing unresolved questions as the edge of the exploration, a short recap when coming back after a few days); presentation forms (the timeline, and the map, see D6); and the look of the summary network (letting the summary network show how thinking flows, the differences between nodes and their order in time). Sharing connects the three: the summary network and branches decide what is in a path, and the presentation form and look decide whether a reader who was not there can understand it.

**To be decided:** What sharing includes, and how to protect privacy (scenario 4 is especially sensitive); the form of the shared page; when the map is brought in (scenario 2, see D6).

## 4. How work moves forward

1. **One design note per direction:** Before implementation, write a short design note (the problem, the seams used, data model changes, completion criteria) that covers only the next increment.
2. **Themed versions:** Deliver by theme, for example "reading and diagnostics", "rich content", "tools"; unfinished features are isolated behind switches.
3. **Turning into requirements:** When a direction enters the formal scope, it is written into requirements and architecture after the user decides, and marked in this document.
4. **Record findings:** For adopted seams, record the actual cost and findings; see [evolution seams](design/evolution-seams.md).

## 5. Adoption status

The user decided on 2026-10-26: introduce the enabling seams E1–E6 as an experiment in the implementation from S2 onward, and record the actual cost and findings; do not implement the features of D1–D6 themselves ahead of time. E1–E4 and E6 have landed; E5 has only a specification; see [evolution seams](design/evolution-seams.md). (The other development line running in parallel at the time did not adopt it; since 2026-11-16 there is only one development line; see "Branches" in `AGENTS.md` at the repository root.)
