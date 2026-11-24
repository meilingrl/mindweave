# Ask from anywhere, openness and local AI

**Status:** in discussion with the owner since 2026-11-25. Not agreed. Last discussed: 2026-11-25 (quoting is written up in §7 and waits for the owner's confirmation).

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
| 2026-11-25 | Quoting from outside takes the collect-then-ask form of §7 (owner welcomed it). Its details are open. |
| 2026-11-25 | Where a sent set of quotes lands is chosen when sending (owner): the default is a new exploration in the workspace used last; it can be switched to another workspace or to the open conversation. It never starts a branch, because a branch's quotes come from its parent conversation (evolution D7). |
| 2026-11-25 | When the selected text cannot be read, the rectangle takes over (owner's suggestion): the hotkey reads the selection, and if there is none or it cannot be read, it goes straight to the rectangle. |
| 2026-11-25 | The first increment has both sources for the quote list (owner): quotes from outside, and several passages selected in a Mindweave conversation and asked about together in that conversation (the third use in evolution D7). macOS first. A branch with several quotes and revising a summary with quotes stay later. |
| 2026-11-25 | Closing the main window keeps Mindweave running, so the hotkey stays reachable (owner). Quitting is explicit. |

## 6. Set aside

- **B1 MCP server, B2 command line:** set aside for now (owner, 2026-11-25).
- **A3 browser extension:** set aside for now (owner, 2026-11-25).

## 7. Per topic

Filled in as each topic is discussed: the decisions, the approach, out of scope, failure cases, acceptance scenarios and the first increment.

### Quoting (A1 and A2; waiting for the owner to confirm this section)

**What the owner means by asking from anywhere (2026-11-25).** Not a place to read answers. After a hotkey in another window, content on the screen is selected and used directly as a quote: text, or a part of a web page or an image. The owner pointed to an annotation flow seen elsewhere: select a passage, write a note on it, keep it; do this several times; then write one prompt and send everything to the model.

The owner's decisions are in §5. Everything below that is not in §5 is Claude's call, and the owner can change it.

#### How it works

**One quote list, three sources.** The composer gets a numbered list of quotes above the prompt. A quote is a piece of content with an optional note. It comes from:

- text selected in another application;
- a rectangle of the screen;
- a passage selected in an answer of a Mindweave conversation.

This is asking with multiple quotes (evolution D7); quotes from outside are a further source for the same list.

**Collecting outside.**

1. Select text in any application and press the hotkey. A small box appears near the pointer with the quote and a note field. Enter keeps the quote (the note may be empty); Esc discards it. The box then goes away.
2. If there is no selection, or the application does not give it, the hotkey goes straight to the rectangle: drag it on the screen where the pointer is. The picture becomes a quote, with the same box.
3. A picture is also read into text on the machine by the system's text recognition. The picture and the text are both kept.
4. Each quote records where it came from: the application's name and the window's title. Both are shown under the quote and sent with it.
5. Kept quotes wait in Mindweave's menu-bar item, which shows how many there are. They survive quitting.

**Collecting inside.** A selection in an answer offers "quote" beside the branch action. The passage joins the list of that conversation's composer and can have a note.

**Asking.**

1. The menu-bar item opens a panel: the numbered quotes with their notes, each removable, one field for the prompt, and a target line.
2. The target defaults to a new exploration in the workspace used last. It can be switched to another workspace or to the open conversation.
3. Send puts the question there and brings the main window to that conversation. The answer is read there.
4. Quotes from outside can also be added to the open conversation's composer from the main window, where they mix with quotes from inside.

**In the conversation.** The sent message shows the numbered quotes above the user's own words, each with its note and where it came from. A quoted passage is never shown as text the user wrote. A quote from inside points back to its passage. The prompt and the answer can refer to quotes by number.

**For the model.** Each quote is given as numbered material with its note. A quote from outside is marked as external and untrusted, like a web page (requirements §10.4.1): text in it is material, not a request from the user.

**Staying running.** Closing the main window no longer quits. Mindweave stays in the menu bar, where the item has the kept quotes, "Open Mindweave" and "Quit". Answers keep running after the window closes. The warning about running answers moves to quitting. Starting at login is a setting, off by default. The hotkey can be changed in settings.

**Storage.** A quote from outside is a resource with external provenance, stored like a long paste or an image; the recognized text is a derived form of the picture. A quote in a message is a new kind of content block. Whether this needs a migration is settled in the design note. `Quote` (引用) gets a glossary row before any code.

#### What it needs, and what leaves the machine

- **macOS:** the Accessibility permission for reading a selection, asked at the first text quote; the Screen Recording permission, asked at the first rectangle. Mindweave explains each before the system asks.
- **Windows:** no permissions. It follows the macOS increment, when the owner gives time on the hardware. Until then, closing the window still quits there.
- **Leaves the machine:** nothing until Send. Then the quotes, notes, where they came from and the prompt go to the configured model. A picture goes only to a model that declares image input; otherwise its recognized text goes.

#### Out of scope

- An answer in a small window (owner).
- A box or highlights that stay inside another application's page. That needs Mindweave to own the page: the browser extension (set aside) or a document opened inside Mindweave.
- A branch started from several quotes, and revising a summary with quotes (the two other uses in D7). Both come later, with their own decisions.
- A simulated copy as a fallback. It touches the clipboard, and the rectangle covers the case. Add it only if daily use shows the need.
- A rectangle over two monitors, scrolling captures, video.

#### Failure cases

| Case | What happens |
| --- | --- |
| The selection cannot be read | The rectangle takes over (owner) |
| Accessibility is refused | The hotkey always goes to the rectangle; settings say why and how to grant it |
| Screen Recording is refused | Text quotes still work; for a picture, Mindweave says why not and accepts a pasted screenshot in the panel |
| Another application holds the hotkey | Settings show that it could not be registered and ask for another |
| Mindweave is not running | Nothing happens. Start at login is the remedy |
| The rectangle is over protected content and comes back blank | The quote is not kept, and the box says so |
| Text recognition finds nothing and the model has no image input | The quote is kept but marked as not sendable to this model, with the reason; it is not dropped silently |
| The quotes and the prompt exceed the context capacity | The user is asked to remove or shorten quotes; nothing is truncated silently (D7) |
| No model is configured | Quotes are kept; Send is off with the reason |
| The target conversation was deleted or archived meanwhile | The target returns to a new exploration |
| Saving fails | The quote is not kept and the box says so; the selection stays in the other application |

#### Acceptance scenarios

1. In a browser, select a sentence and press the hotkey. Write a note and press Enter. The menu-bar item shows 1.
2. Do it again in a PDF reader, then press the hotkey with nothing selected and drag a rectangle over a diagram. The item shows 3.
3. Open the panel. The three quotes are numbered, each with its note and the application and window it came from. Remove one; the numbers close up.
4. Write a prompt that refers to [1] and [2] and send. The main window comes forward on a new exploration in the workspace used last. The message shows the quotes above the prompt; the answer streams.
5. With a conversation open, collect one quote and choose "Continue the open conversation". The question lands there.
6. In an answer, select two passages one after the other and choose "quote" for each. Add a quote from outside. Ask about all three in the same conversation.
7. Close the main window while an answer runs. The answer finishes. The hotkey still works. "Quit" in the menu-bar item warns about a running answer.
8. Quit with two kept quotes and start again. Both are there.
9. Press the hotkey in an application that does not give its selection. The rectangle appears.
10. With a model that has no image input, a rectangle over text is sent as its recognized text, and the step details say so.
11. A quote from a web page that contains an instruction to the AI does not make the app do anything the user did not ask for.

#### First increment (macOS)

Three steps, each usable when merged:

1. The quote list in the composer and quotes from inside a conversation. It needs no permissions and lets the list be judged first.
2. Staying running, the menu-bar item, the hotkey, text quotes and the panel.
3. The rectangle and text recognition.

Look and feel is the owner's: the note box, the menu-bar item, the panel and the numbered quotes in a message are shown as a prototype or screenshots before they are built in.

#### After this section is agreed

Requirement and architecture text follows as a separate commit that the owner confirms: quoting as a section of its own, the change to quitting (requirements §9.2), the glossary row, and evolution D7 pointing to it.

#### Not tried yet

Nothing here has been prototyped. The parts most likely to surprise: which applications give their selection, and the rectangle on a second monitor with a different scale.
