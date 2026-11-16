# 智能体平台讨论记录（2026-11-13）

> 本文是**讨论记录**，不是现行需求或架构。2026-11-13 用户与 Claude 在一次修缺陷会话中讨论了长文本粘贴、文件、多模态与智能体能力，用户认可了方向（“perfectly aligns with my vision”），但当时只留在会话里，没有写进仓库。2026-11-17 从会话记录原样整理到这里，作为写需求与架构时的起点，不从头开始。
>
> - 转为正式范围须经用户决定，写入 [需求](../requirements.md) 与 [架构](../architecture.md)（AGENTS.md“改动需求或架构”）。
> - 方向的摘要与状态见 [演进方向](../evolution.md) D5。
> - 2026-11-16 用户决定：压缩时超过窗口的单条长消息不再单独处理，由本设计中的长文本／文件导入取代（见 STATUS.md）。
> - 已过时的部分：文中的轨道分支（`track/claude-auto`、`track/codex-steady`）已于 2026-11-16 合并为一条 `main`；“课程提交轨道不纳入”一句随之失效，现行约定见 AGENTS.md。原文其余部分不改。

以下按时间顺序保留原文（英文）。第一条回答只保留与本话题相关的第 2 节；输出上限与健康检查进度的部分不在此列。

---

## 1. 用户（13:27）：长文本粘贴

2. Long pasted messages: I agree this is a great question to tackle. However, I think the solution should be considered together with future features like file handling, web search, multimodal capabilities, and even Agentic abilities. For example, when I use DeepSeek, short messages are sent normally, but long texts are automatically converted into a `.txt` file and uploaded. I don't know the exact mechanism behind this, but it gave me an idea. Please share your thoughts on how we should design the handling of long messages.

## 2. Claude（13:29）：把长粘贴当作文档

### Long pasted messages: treat them as documents

Your DeepSeek example points at the right idea. What DeepSeek does on its servers isn't public, but the visible part is the important bit: **a long paste stops being a message and becomes an attachment.** Mindweave needs that same idea for files, web search, images and agent tools, so I'd design it once.

**One concept: a "source document."** It's a piece of material that isn't part of the conversation itself. Each one has:
- where it came from: a paste, a file, a web page, a tool result
- its extracted text, size and a content hash
- a summary made once and reused

A long paste is just an unnamed `.txt` document. A file adds a text-extraction step. A web search result adds a URL. An agent tool's output is a document produced by a tool. Images would be documents too, and would be offered only for models that can read images.

**How it fixes today's failure:**
- **Composer:** above a size threshold, a paste turns into an attachment chip that you can turn back into plain text. The message list and branch quoting stay light. The audit found the message list already slows down in proportion to total text.
- **Sending:** if the document fits the context budget, it goes in whole, clearly marked as a document. If it doesn't fit, it's split into pieces, and either the relevant pieces are selected or a summary is used. Picking pieces by meaning needs an embeddings service, which not every provider offers, so local keyword search is the fallback.
- **Compression:** documents are kept apart from conversation turns, so one huge paste can no longer make a single oversized chunk. That oversized chunk is exactly what fails today. Each document's summary is made once and reused.
- **Branching and summaries:** you can still quote from a document, and its origin is shown.

**Constraints:**
- **Scope:** this is a change to what the product does, so it goes into `main`'s requirements first, for you to approve.
- **Schema:** it needs a new table for documents, i.e. a migration. That conflicts with "no migrations in the bug-fix phase", so it's for after this phase.
- **Interim fix:** the only cheap step without a migration is a composer warning when a paste won't fit the model.

If you agree with the direction, I can draft the requirements text on `main` for you to review. That's a later step, not something I'd start now.

## 3. 用户（13:46）：从更高的角度重新评估

Let's step back. I am not just asking how to handle long text. My real vision for Mindweave is about User Experience and Product Capability.
I want to integrate rich AI capabilities: multimodal (images/audio), tool calling, web search, etc. I want users to have more choices and feel as powerful as using top-tier AI software like ChatGPT or Claude, rather than feeling restricted by a simple chat box. Even if this means we have to design our own Agent architecture, that's completely fine.
So, please re-evaluate your "Source document" design from this higher perspective:

1. Future-proofing: Does treating inputs as "documents" seamlessly support future multimodal inputs (images) and web search results?
2. Tool Calling & Agents: How should we architect the system so that the AI can autonomously use tools (like web search or reading a file) to answer questions, rather than just passively receiving text?
3. UX: How do we package all this complexity into a simple, intuitive UI for the user?

Please share your architectural thoughts on how we can build Mindweave into a true AI Agent platform, not just a chat app.

## 4. Claude（13:49）：资源、内容块与运行循环

Short answer: "documents" is the right model for what goes *into* a conversation, including images, audio and web results. It isn't enough for an AI that *acts*. For that, an answer has to become a sequence of steps: the model thinks, calls a tool, reads the result, and continues. Mindweave was partly built for this already. `main`'s evolution guide has a direction called **D5 智能体能力** (agent capabilities), and the code has three hooks for it:
- **Messages made of parts.** Each message is stored as a list of content blocks; today the only kind is text.
- **Several requests per answer.** The request log already allows many model requests under one answer.
- **Per-model capabilities.** Each model records whether it supports tool calls and image input; the settings page doesn't expose them yet.

### 1. Future-proofing: two layers, not one

**Layer A: resources (what the material is).** I'd rename "source document" to *resource*. A resource is any piece of stored material: a paste, a file, an image, an audio clip, a web page, a tool's output. Each has:
- a stored copy of the original, kept as a file on disk (large media shouldn't go in the database)
- its type and origin (who or what produced it, when, from which URL)
- whether it's trusted
- versions derived from it: extracted text, chunks, a summary, a description of an image, a transcript of audio

This handles your question directly:
- **Images:** if the model can see images, the original is sent. Otherwise a description or extracted text is used, or the attach button explains why images aren't available for this model.
- **Audio:** a transcript is derived from it.
- **Web results:** a resource with its URL, the time it was fetched, and a "not trusted" flag.

The same mechanism covers files, pastes, search and media.

**Layer B: content blocks (how things appear in the conversation).** A message holds text, attachment references, tool calls, tool results (which point to resources), citations, and later generated outputs such as charts. A resource says *what* something is; a block says *where and when* it entered the conversation.

### 2. Tools and agents: an answer becomes a loop

**The run loop.** An answer becomes a *run*:
1. The model is asked.
2. If it requests tools, they run.
3. Their results go back to the model, which continues.
4. This repeats until the model gives its final answer, or a limit is hit: number of steps, time, or tokens.

Every step is saved. Two existing rules carry over:
- **Interrupted runs:** they are never resumed automatically after a crash.
- **Side effects:** a tool step that changed something is never repeated.

The "at most two AI requests at once" rule then applies to runs rather than single requests.

**Providers.** Each provider's tool-calling format is translated into one internal format. Tools are offered only to models that support them; I wouldn't fake tool use through prompting. DeepSeek, Kimi and GLM each implement tool calling slightly differently, so real-service tests matter here.

**Tools.** Each tool declares:
- its name and description
- the format of its inputs
- a risk level: local read-only, reads the network, or changes something
- a timeout and a limit on how much output it may return

Starting set:
- **Search the attached material.** This is the real answer to long pastes: the model pulls the parts it needs instead of everything being sent in.
- **Search this workspace's summaries.** This is Mindweave's unique tool: answers grounded in what you have already explored.
- **Web search and fetching a page.** These need a search provider and its key.
- **Draw a chart or table,** rendered locally.

For third-party tools, I'd adopt **MCP** (Model Context Protocol), the emerging standard for plugging tools into AI apps, rather than inventing a plugin system.

**Safety, learned from this audit.**
- **Tool results are untrusted input.** The Markdown-image leak we just fixed is exactly the kind of attack tool results make easier.
- **Page fetching** blocks local and private addresses and `file://`.
- **Keys** never appear in tool inputs.
- **Visibility:** every request that goes out shows what was sent and where.
- **Approval:** anything that changes something waits for your OK.

**Context.** A large tool result is stored as a resource. The model gets a short summary plus a handle it can use to fetch more. Compression has to understand run steps too.

**Mindweave's own features.** Branching from a selection, returning to the source, and the summary network have to work on tool results and citations as well.

### 3. UX: simple by default, detail on demand

**Composer:** one box with a "+" for attaching files and images. Dropping or pasting a long text or an image attaches it automatically, and you can turn it back into plain text. A small switch sets web search to *automatic*, *on* or *off*. Options the current model can't use are greyed out, with the reason.

**During a run:** one quiet line inside the answer, such as "搜索了 3 次 · 读了 2 个网页" (searched 3 times · read 2 web pages), which expands into the steps. Citations like [1] link to their sources, and "return to source" works on them. Stop works at any step and keeps what arrived.

**Approvals:** a small card inside the answer, never a modal dialog, and only for actions that change something.

**Settings:** advanced options (search provider, MCP servers, step limits) live under Settings › 工具 (Tools), away from the main flow. Costs per tool and per run show on the existing statistics page.

**Positioning:** I wouldn't chase ChatGPT feature for feature. Mindweave's distinctive angle is an agent that explores *with your knowledge*. It searches your summary network, cites its sources, and feeds what it finds back into the network.

### How to get there

**Constraints.**
- **Scope:** this changes what the product is, so it starts as requirements and architecture on `main`, for you to approve.
- **Migrations:** it needs new tables and data migrations, which the bug-fix phase rules out. Writing the documents can happen now; code comes after this phase.
- **Course submission:** the course-submission track (`track/codex-steady`) should stay out of scope.

**Phases, each usable on its own.**
1. Resources and attachments: pastes, files, and images for models that can see them. No tools yet.
2. The run loop and tool calling, with the two local tools only: search the attached material, and search the workspace's summaries. No new keys needed.
3. Web search and page fetching, with citations, plus a search-provider setting.
4. MCP client.
5. Generated charts and audio.

**What I'd hold back:** running code in a sandbox, and agents that keep working in the background for a long time. Both carry much higher risk and cost.

## 5. 用户（13:57）：认可方向，另开会话起草

This architecture design is brilliant and perfectly aligns with my vision. However, I need to clarify our boundaries and priorities.
This design work is completely out of your current scope. Your role is the "Bug-fix Agent", and our highest priority right now is to stabilize the system by fixing the medium and low-priority issues from the health check. We must not allow scope creep.
Here is what I need you to do:

1. For the Agent architecture: Please generate a clear "Kick-off Prompt" that I can use to start a brand new, dedicated session with a new Agent. This prompt should contain the context, the phased plan (1-5), and the instruction to draft the requirements and architecture documents on `main`.
2. For your current task: After giving me that prompt, immediately drop this architecture design and get back to your original job. Proceed with fixing the remaining medium and low-priority bugs (e.g., the slow message list, restored databases leaving WAL mode, etc.).


## 6. 为新会话准备的启动提示（15:14，未使用）

Claude 按用户要求写出启动提示；之后没有会话用它起草需求与架构。

```
You are the architecture agent for Mindweave (a .NET 10 / Avalonia 12 desktop app for exploring topics with AI:
conversations, branching from selected text, a summary network). Repository: ~/Developer/Projects/mindweave
(branch `main` holds the shared requirements and architecture; read README.md and AGENTS.md first, then
docs/requirements.md, docs/architecture.md and docs/evolution.md — especially D5 "智能体能力" and D2).
Track branches (track/claude-auto, track/codex-steady) are in separate worktrees; do not change them.

Goal: turn Mindweave from a chat app into an AI agent platform — multimodal input (images, audio), tool
calling, web search, files, and eventually third-party tools — while keeping it simple to use. Your job is
ONLY to draft the requirements and architecture documents on `main` for the user to review. No product code.

Design direction already agreed with the user (refine it, do not start over):
1. Resources: every input or produced artefact (paste, file, image, audio, web page, tool output) is a
   resource: stored original (blob on disk, not in SQLite), media type, provenance (who/what produced it,
   when, URL), trust flag, and derived forms (extracted text, chunks, summary, caption/OCR, transcript).
   What is sent depends on model capabilities (E4: ImageInput etc.).
2. Content blocks: messages are ordered blocks (E2 already exists): text, attachment reference, tool call,
   tool result (refers to a resource), citation, later generated artefacts (charts).
3. Run loop: an answer becomes a run — model turn → tool calls → tool results → model turn … until a final
   answer or a limit (steps, time, tokens). Each step persisted; an interrupted run is never resumed or
   re-sent automatically; a step with side effects is never repeated. "At most two AI requests" is redefined
   per run. Provider adapters normalise native tool calling; tools only for models that support them.
4. Tools: registry with name, description, JSON schema, risk class (local read-only / network read /
   side effects), timeout, output budget. First tools: search attachments, search the workspace's summary
   network (Mindweave's differentiator), then web search + fetch with citations. Third-party tools via MCP.
5. Safety: tool results are untrusted input (prompt injection); fetch blocks private addresses and file://;
   keys never in tool arguments; every outbound request visible; side effects need explicit approval.
   (Context: an audit just fixed model-output Markdown images that made the app fetch URLs.)
6. UX: one composer with "+" (files, images), automatic attachment of long pastes and images (undoable),
   a web-search switch (auto/on/off), options the model can't use greyed out with the reason; inside an
   answer one quiet activity line ("搜索了 3 次 · 读了 2 个网页") that expands into steps; numbered
   citations that work with return-to-source; approvals as inline cards; advanced settings under
   设置 › 工具; costs per tool and run on the existing statistics page.

Phased plan (each phase usable on its own; write it into the documents with acceptance criteria):
  Phase 1 — Resources and attachments: pastes, files, images (only for models that can see them). No tools.
  Phase 2 — Run loop and tool calling with two local tools: search attachments, search summary network.
  Phase 3 — Web search and page fetching with citations; search-provider setting.
  Phase 4 — MCP client for third-party tools.
  Phase 5 — Generated artefacts (charts) and audio.
Explicitly out of scope for now: code execution sandboxes, long-running background agents.

Deliverables on `main` (as new sections or new documents, following the repo's existing style — Chinese for
requirements/architecture, as the existing documents are):
  - Requirements: user-visible behaviour, scenarios and acceptance criteria per phase (extend §11 scenarios).
  - Architecture: data model (resources, blocks, run steps, migrations needed), run loop and scheduling,
    provider adaptation, tool registry and permission model, context budgeting with resources and tool
    results, anchoring/branching on tool results and citations, failure and recovery rules, security.
  - Open questions and decisions for the user, each with a recommendation.
Rules: follow AGENTS.md (main → track merges only; do not edit track branches). Commit on `main` only after
the user approves the text; do not push without the user's say-so. Ask the user when a choice is theirs.
```

