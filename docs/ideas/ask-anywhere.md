# Ask from anywhere, openness and local AI

**Status:** in discussion with the owner since 2026-11-25. Not agreed. Last discussed: 2026-11-25 (the order is set; the form of quoting from outside is being discussed).

This draft takes over directions 1, 3 and 7 of [more directions](more-directions.md), the question Q13 of the agent platform (search by meaning) and the caching part of [evolution](../evolution.md) D4. Those texts stay as they are until the owner settles what moves.

## 1. Topics

Seven topics in four groups. The order and what is set aside are in §5 and §6.

| Group | Topic | In one line |
| --- | --- | --- |
| A. Ask from anywhere | A1 quick-ask window | A global hotkey opens a small ask window over any application |
| | A2 selected text | Text selected in any application becomes a question or a branch |
| | A3 browser extension | The same from a web page, with the page's address |
| B. Openness | B1 MCP server | Other agents read the user's workspace through Mindweave |
| | B2 command line | `mindweave ask …` and similar, from a terminal or a script |
| C. Model access | C1 several connections, more protocols | More than one model connection; Responses and Messages beside Chat Completions |
| | C2 prompt caching for branch trees | Sibling branches share a cached ancestor prefix (evolution D4) |
| D. Local AI | D1 local models | Answers from a model on this machine |
| | D2 search by meaning | Search over the user's own content by meaning, not only by keyword (Q13) |

## 2. What exists today (checked in the code, 2026-11-25, main at f9edc39)

- **One protocol.** `ChatProtocol` has one value, `ChatCompletions`; the only adapter is `ChatCompletionsClient`.
- **One active configuration.** The data separate service connection and model configuration and allow several rows (architecture §7.2). The interface manages one, and every task uses it.
- **Local models already answer, by hand.** A local server that speaks Chat Completions (for example Ollama at `http://localhost:11434/v1`) works as a typed address. Its capability presets are all off ([model capabilities](../design/model-capabilities.md) §3.9).
- **MCP client only.** The package is `ModelContextProtocol.Core`, which has no server hosting ([third-party tools](../design/mcp.md) M1).
- **Keyword search only.** Attachments and summaries are searched with SQLite FTS5 (`SearchSummariesTool`, the search over attachments). There are no embeddings.
- **No way in from outside the window.** There is no global hotkey, no tray or menu-bar item, and no command-line argument that carries a question. A second launch sends one byte over a named pipe, which brings the running window to the front (`SingleInstance`). That pipe is the natural place for a quick-ask or command-line request to arrive.
- **Caching is not measured.** `ModelCapabilities.PromptCaching` exists and the presets fill it; no feature reads it. `ai_request` stores prompt and completion tokens, not cached tokens.

## 3. What each topic needs, and what leaves the machine

"Leaves the machine" means beyond what a question in the window already sends to the model the user configured.

| Topic | macOS | Windows | Leaves the machine |
| --- | --- | --- | --- |
| A1 quick-ask window | A system hotkey registration (no permission prompt). Mindweave must be running: a menu-bar item, optionally start at login | `RegisterHotKey` (no permission). A tray icon, optionally start at sign-in | Nothing new |
| A2 selected text | Two ways. A Services menu entry: no permission, the user assigns its shortcut in System Settings. Or reading the selection on the hotkey: needs the Accessibility permission, granted once in System Settings | Reading the selection through UI Automation, with a copy-and-restore of the clipboard as fallback. No permission; some applications do not answer | Nothing new. The selection goes to the model only when the user sends the question |
| A3 browser extension | One extension for Chrome, Edge and Firefox, talking to the running app on this machine. Safari needs its own packaging and an Apple developer account | The same extension; no Safari | The selection and the page address go to Mindweave on this machine, then to the model like any question. Publishing in the browser stores is an outward-facing act |
| B1 MCP server | A small program the other agent starts, which asks the running app (or reads the data read-only). Needs the MCP server package | The same | Whatever a tool returns goes to the other agent, and from there to that agent's model provider. This is the one topic where the user's stored content leaves through a door other than the model connection |
| B2 command line | A command inside the app bundle, linked into `PATH` | A separate console program beside the windowed one | Nothing new |
| C1 connections and protocols | No platform work | No platform work | The same content, but to whichever connection the task uses. Each request must say which |
| C2 prompt caching | No platform work | No platform work | Nothing new. Providers keep the cached prefix for minutes to hours on their side |
| D1 local models | Through a local server the user installs: nothing to build per platform. Built-in inference: Metal, and a model file of several GB | Through a local server: the same. Built-in: CUDA, Vulkan or DirectML, and the same model file | Nothing. Getting a model is a download, once |
| D2 search by meaning | A small embedding model on the machine (about 100 to 500 MB), or the local server's embedding endpoint | The same | Nothing with a local model. With a provider's embedding service, the text of every summary and chunk leaves |

## 4. Where this touches the other discussions (proposed boundaries, for the owner to settle)

| Touches | Proposed boundary |
| --- | --- |
| Sharing (import, exchange format E5) | A3 and B2 bring content in as ordinary questions and attachments. Anything that brings in or writes out whole conversations belongs to sharing and uses E5 |
| Reply quality (prompts, memory) | C2 owns measuring cached tokens and the order of the context parts. What the parts say belongs to reply quality |
| Return and review (the summary network) | D2 provides one search. Using it for link suggestions or for returning belongs to return and review |
| Safety and upkeep (stored keys, diagnostics) | C1 adds keys under the existing credential rules; how keys are kept belongs to safety. B1 decides what is offered; the record of who asked for what belongs to diagnostics |

## 5. Decisions

| Date | Decision |
| --- | --- |
| 2026-11-25 | The order (owner): first the quick-ask window and selected text (A1, A2); then several connections and local models through a local server (C1, D1); then search by meaning (D2); then prompt caching (C2). |
| 2026-11-25 | The MCP server, the command line and the browser extension (B1, B2, A3) are set aside for now (owner). |
| 2026-11-25 | The answer is not shown in a small window (owner): it would not work well and brings many complications. Answers are read in the main window. |

## 6. Set aside

- **B1 MCP server, B2 command line:** set aside for now (owner, 2026-11-25).
- **A3 browser extension:** set aside for now (owner, 2026-11-25).

## 7. Per topic

Filled in as each topic is discussed: the decisions, the approach, out of scope, failure cases, acceptance scenarios and the first increment.

### A1 and A2: quoting from outside (in discussion)

**What the owner means by asking from anywhere (2026-11-25).** Not a place to read answers. After a hotkey in another window, content on the screen is selected and used directly as a quote: text, or a part of a web page or an image. The owner pointed to an annotation flow seen elsewhere: select a passage, write a note on it, keep it; do this several times; then write one prompt and send everything to the model. The owner asked whether this can be built.

**Proposed form (Claude, not agreed).** Collect, then ask:

1. Select text in any application and press the hotkey. A small box appears with the quote and a note field. Enter keeps it and the box goes away.
2. Press the hotkey with nothing selected and drag a rectangle on the screen. The picture becomes a quote in the same way.
3. Repeat. A small marker shows how many quotes are kept.
4. Open the marker: the numbered quotes with their notes, each removable, and one field for the prompt. Send puts the question into Mindweave, and the answer is read in the main window.

This is asking with multiple quotes (evolution D7) with quotes that come from outside. One quote list in the composer serves both.

**What cannot be built this way.** A box that stays beside the selection inside another application's page, and highlights that stay on that page, need Mindweave to own the page: a browser extension (set aside) or a document opened inside Mindweave.
