# Ask from anywhere, openness and local AI

**Status:** approved by the owner on 2026-11-27 (D-045), after the discussion of 2026-11-25 to 2026-11-27, for four topics: quoting, several connections and local models, search by meaning, prompt caching. Not built. The requirement and architecture text (requirements v0.34 §10.11; architecture §2.3, §7.5, §15.5 and §18) was confirmed by the owner on 2026-11-27. Where this note and that text differ, the text holds.

This draft takes over directions 1, 3 and 7 of [more directions](../ideas/more-directions.md), the question Q13 of the agent platform (search by meaning) and the caching part of [evolution](../evolution.md) D4. Those texts stay as they are until the owner settles what moves.

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
- **Local models already answer, by hand.** A local server that speaks Chat Completions (for example Ollama at `http://localhost:11434/v1`) works as a typed address. Its capability presets are all off ([model capabilities](model-capabilities.md) §3.9).
- **MCP client only.** The package is `ModelContextProtocol.Core`, which has no server hosting ([third-party tools](mcp.md) M1).
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
| 2026-11-25 | The quoting section of §7 is agreed as written, Claude's calls included (owner). |
| 2026-11-25 | Several connections: there is one current model for everything (owner). Settings hold the list of connections; a picker beside the input box switches the current model for all new questions. A model per conversation can be added on top later. |
| 2026-11-25 | One setting, the model for background work (owner). It defaults to the current model and can be any saved model. Background work is titles, compression, attachment digests, recommended questions and link suggestions; summaries stay with the current model. When that model cannot be reached, the task fails with its reason; nothing switches on its own. |
| 2026-11-25 | Messages is the further protocol of the first increment; Responses comes later (owner). The owner now has an official Anthropic key, so Messages can be checked against the real service; there is no official OpenAI key. |
| 2026-11-25 | The section on several connections and local models in §7 is agreed as written, Claude's calls included (owner). |
| 2026-11-25 | Responses moves into the first increment as its fourth step, after Messages (owner). It is checked against a local server and the owner's relay; the check against OpenAI's own service stays open until there is a key. This replaces "Responses comes later" above. |
| 2026-11-25 | Search by meaning serves both the model's tools and the user (owner): one index over summaries, the questions and answers of conversations, and attachments, inside one workspace. The user gets a search of their own beside title search. |
| 2026-11-26 | The vectors come from an embedding model on a saved connection (owner): one more model setting, empty by default, which leaves search as it is today. An embedding model that Mindweave carries itself can follow behind the same setting. |
| 2026-11-26 | The section on search by meaning in §7 is agreed as written, Claude's calls included (owner). |
| 2026-11-26 | Prompt caching: the first increment has all three steps, in this order (owner): record and show cached tokens; stop changing the start of the request; mark the cache points for Messages. |
| 2026-11-26 | On a Messages connection the cache is kept for one hour (owner). Claude added: this is the default of a setting on the connection (off, 5 minutes, 1 hour), and Mindweave sends no requests on its own to keep a cache alive. |
| 2026-11-27 | The section on prompt caching in §7 is agreed as written, Claude's calls included, and with it the whole draft (owner). |

## 6. Set aside

- **B1 MCP server, B2 command line:** set aside for now (owner, 2026-11-25).
- **A3 browser extension:** set aside for now (owner, 2026-11-25).
- **Running a model inside Mindweave:** not for now (Claude's call, 2026-11-25; the order the owner set says "through a local server").

## 7. Per topic

Filled in as each topic is discussed: the decisions, the approach, out of scope, failure cases, acceptance scenarios and the first increment.

### Quoting (A1 and A2; agreed by the owner, 2026-11-25)

**What the owner means by asking from anywhere (2026-11-25).** Not a place to read answers. After a hotkey in another window, content on the screen is selected and used directly as a quote: text, or a part of a web page or an image. The owner pointed to an annotation flow seen elsewhere: select a passage, write a note on it, keep it; do this several times; then write one prompt and send everything to the model.

The owner's decisions are in §5. The rest of this section was proposed by Claude and agreed with it.

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

### Several connections and local models (C1 and D1; agreed by the owner, 2026-11-25)

The owner's decisions are in §5. The rest of this section was proposed by Claude and agreed with it.

#### How it works

**The list of connections.** 设置 › 模型连接 (Settings › Model connection) becomes a list. A connection has a name, an address, a protocol (Chat Completions, Messages or Responses) and a key. It holds one or more models; each model has its own capabilities, context capacity and output limit, filled from the presets as today. The existing configuration becomes the first entry, unchanged.

**The current model.** A picker beside the input box shows the current model and lists the saved models by connection. Choosing one makes it the current model for all new questions, in every conversation. A question already accepted keeps the model it was accepted with (the existing rule). Images and tools follow the current model's capabilities, as today: what it cannot do is grayed out with the reason.

**The model for background work.** One setting. It defaults to "Same as the current model" and can be any saved model. It serves titles, compression, attachment digests, recommended questions and link suggestions. Summaries use the current model.

**A local server.** The list offers a ready entry for a local server: the address `http://localhost:11434/v1`, no key. Its models are listed from the server. Capabilities and context capacity are read from the server where it gives them; otherwise the user declares them. Mindweave does not install or start the server and does not download models; the entry links to how to do that.

**Messages and Responses.** Two further protocol adapters beside the existing one, each with the same scope: streaming text, tool calls, images, reasoning and token counts. Nothing above an adapter knows which protocol a model uses. Responses is used without the parts that keep conversation state on the provider's side; Mindweave keeps that itself.

**Which model answered.** Every answer already records its model. The answer's details name the model and the connection, and the statistics tell models apart.

**Removing a connection.** Its key is deleted. Answers keep the recorded name of their model. If it holds the current model, another is chosen first; if it holds the background model, that setting returns to "Same as the current model".

**Storage.** The tables for connections and models exist and allow several rows. The current model and the background model are preferences. No migration is expected; the design note settles it.

#### What it needs, and what leaves the machine

- **macOS and Windows:** no platform work. A local server is installed by the user on either.
- **Leaves the machine:** a question goes to the connection of the current model, and background work to the connection of the background model. The picker always shows where the next question goes. With a local model, that content stays on the machine. Web search and third-party tools are separate and unchanged.
- **Verification:** the owner has an official Anthropic key (2026-11-25), so Messages can be checked against the real service, within a budget the owner gives. This also closes the gap of architecture §7.1, where the attempts through a relay failed.
- **Verification of Responses:** the ways to test it without an official OpenAI key (looked up 2026-11-25, to be checked again when the work starts): a local server (Ollama serves `/v1/responses` since v0.13.3, without the parts that keep state on the server; free, and it needs a model on the machine); the owner's relay, where a Responses request to a GPT model passed a small check on 2026-10-23 (`prototypes/responses-compat`; paid); other services with a Responses endpoint, such as OpenRouter (beta) and Groq (each needs its own key). None of these proves the behaviour of OpenAI's own service.

#### Out of scope

- A model per conversation (can be added later on top).
- Any automatic choice or fallback between models.
- The parts of Responses that keep conversation state or run tools on the provider's side.
- Running a model inside Mindweave, and downloading models.
- Asking two models the same question side by side.
- Sign-in with a provider account instead of a key.

#### Failure cases

| Case | What happens |
| --- | --- |
| The local server is not running | The request fails and says the server at that address cannot be reached. Nothing switches to another model |
| The current model cannot use tools or see images | Those features are grayed out with the reason, as today |
| A conversation has images or tool steps, and the current model cannot take them | They are left out, and this is stated below the answer (the rule against silent truncation). Reasoning from one model is not sent to another |
| A local model has a small context | The existing compression rules apply with that model's capacity |
| A key is wrong or expired | The request fails and names the connection |
| The model list cannot be fetched | The model ID can be typed, as today |
| The background model returns something unusable | That task fails as it does today; the setting notes that a weak model gives weaker titles and suggestions |
| The current model's connection is being removed | Another model is chosen first |

#### Acceptance scenarios

1. After the update, the existing configuration is the first connection and still the current model. Nothing else changed.
2. Add a second connection with its own key and two models. Both appear in the picker.
3. Switch the current model in the picker and ask. The answer's details name the new model. A second conversation uses it too.
4. Switch while an answer is streaming. That answer finishes on its old model.
5. Add the local server entry with the server running. Its models are listed without a key. Ask a question with the network cable out; it is answered.
6. Stop the local server and ask. The failure says the server cannot be reached; no other model is used.
7. Set the background model to the local one and the current model to a remote one. A new conversation's title is produced by the local model; the statistics show this.
8. Choose a model without image input in a conversation that has an image. The attach option is grayed out with the reason, and the next answer states that the earlier image was left out.
9. Add a Messages connection with a Claude model. A question with a tool call and an image is answered, with streaming and reasoning shown as for other models.
10. Add a Responses connection to a local server or the relay. A question with a tool call is answered, with streaming.
11. Remove a connection. Its key is gone from the system credential store; old answers still name their model.

#### First increment

Four steps, each usable when merged:

1. The list of connections, the picker, and capabilities per model. All on Chat Completions.
2. The local server entry and the model for background work.
3. The Messages adapter, checked against the real service within the owner's budget.
4. The Responses adapter, checked against a local server and, within the owner's budget, the owner's relay. Not verified until there is a key: OpenAI's own service.

Look and feel is the owner's: the list in settings and the picker are shown before they are built in.

#### After this section is agreed

Requirement and architecture text follows as a separate commit that the owner confirms: requirements §9.4 and architecture §7 (which describe one configuration), and evolution D3. How keys are kept stays with the safety and upkeep discussion (§4).

### Search by meaning (D2; agreed by the owner, 2026-11-26)

The owner's decisions are in §5. The rest of this section was proposed by Claude and agreed with it. This section answers Q13 of the agent platform questions.

#### How it works

**The setting.** A third model setting, "Model for search by meaning", beside the current model and the model for background work. It is empty by default, and then search works by keyword and title as today. It can be set to an embedding model on any saved connection; a model entry says whether it is a chat model or an embedding model. Before it is turned on with a connection that is not on this machine, the setting states that the text of all workspaces is sent there once, and about how much text that is.

**The index.** One vector per piece of content: each part of a summary's current version, each question and answer of a conversation (long ones in parts), and each chunk of an attachment, including the recognized text of a picture quote. Vectors are stored in the local database, in a compact form, beside the content they belong to, and are deleted with it. Comparison happens in memory, inside one workspace; no vector database is added.

**Building and keeping current.** After the setting is turned on, the index is built in the background, with progress shown in settings; it can be interrupted and continues later. New content is added after it is saved: a finished answer, an adopted summary version, an extracted attachment. Choosing a different model builds the index again, because vectors of two models cannot be compared.

**Searching.** A search runs by keyword and by meaning and merges the two rankings. The words searched for are sent to the embedding model once per search. What is not indexed yet is still found by keyword.

**For the user.** The existing title search also finds passages. Titles come first and at once, as today. Below them come passages by meaning: the passage, where it is (conversation, summary or attachment), and a jump to that place.

**For the model.** "Search summaries" and "search attachments" use the same search. The step details say that meaning was used.

**Cost and tracing.** Embedding requests are recorded like other AI requests, with a purpose of their own, and count in the usage statistics.

**Storage.** One new table for vectors, so one migration; its number goes by merge order.

#### What it needs, and what leaves the machine

- **macOS and Windows:** no platform work.
- **Leaves the machine:** with an embedding model on the local server, nothing. With a provider's embedding model, the text of every summary, conversation and attachment is sent to it once to build the index, then each new piece of content, and the words of each search.
- **Languages:** the content is Chinese and English mixed, so the embedding model must handle both. The setting says so.

#### Out of scope

- Search across workspaces (not done for now, requirements §10.4.2).
- An embedding model that Mindweave carries itself. It can follow behind the same setting.
- Showing related earlier explorations unasked, and link suggestions from the index. These belong to the return and review discussion (§4).
- A second model that re-orders the results.
- Searching pictures by what they show. Their recognized text is searched.

#### Failure cases

| Case | What happens |
| --- | --- |
| The embedding connection cannot be reached while building | Building pauses and continues later; settings show the state. Search uses what is indexed, and keywords for the rest |
| The embedding request for a search fails | Keyword and title results are shown, with a line saying that search by meaning was not available |
| The provider limits the rate | Building slows down and waits; it does not fail |
| A very large workspace | Building takes long; progress is shown; search works on what is done |
| The model is changed | The old vectors are deleted and the index is built again |
| The connection of the embedding model is removed | The setting returns to empty and the vectors are deleted; search is by keyword again |
| A stored vector does not fit the current model, or is damaged | That piece is indexed again |
| The setting is empty | Everything works as today |

#### Acceptance scenarios

1. With the setting empty, search behaves exactly as before the update.
2. Set an embedding model on the local server. Settings show the index being built, then done. Nothing was sent off the machine.
3. Choose a provider's embedding model. Before anything is sent, the setting states that all text goes to that provider and about how much.
4. Search for words that occur in no title and no text, but describe something explored earlier. The passage is found, and the jump opens the conversation at that message.
5. Search for an exact title. It is the first result, as today.
6. Ask a question that depends on an earlier exploration, worded differently from its summary. The step details show that "search summaries" found it.
7. Finish a new answer and search for its content a moment later. It is found.
8. Delete a conversation. Its passages no longer appear in search.
9. Stop the local server and search. Titles and keyword results appear, with the line that search by meaning was not available.
10. Quit while the index is being built and start again. Building continues.

#### First increment

Three steps, each usable when merged:

1. The setting, the index and its building, and the two tools using it.
2. The user's search: passages below the titles, with the jump. Its look is the owner's to judge before it is built in.
3. Later, together with the reply quality discussion: letting the model search the questions and answers of conversations too, not only summaries and attachments. This changes what the tools offer and the run prompt.

#### After this section is agreed

Requirement and architecture text follows as a separate commit that the owner confirms: requirements §9.6 (which says that vector search is not introduced), §10.4.4 (the two tools), §7 (title search), and evolution D5, where Q13 is marked as answered.

### Prompt caching for branch trees (C2; agreed by the owner, 2026-11-27)

The owner's decisions are in §5. The rest of this section was proposed by Claude and agreed with it.

#### What was found (code at f9edc39)

- A branch's context is its ancestors from the root down, then its own messages; the quoted passage comes with the branch's first question. Sibling branches, and a branch and its parent, begin with the same text. The tree is a prefix tree.
- In a run, each step's request starts with the run's instructions and a line that counts the rounds left (`AnswerRunner.Run.cs`, `Compose`). The line changes at every step, so no step can be served from the cache, with any provider.
- Older tool results are shortened in place as a run goes on, and the set of offered tools can differ between questions.
- `ai_request` stores prompt and completion tokens, not cached tokens.

#### How it works

**Step 1: measure.** Each request records two more numbers when the provider reports them: input tokens read from the cache, and input tokens written to it. The request's details show them. The statistics show, per model, the share of input that was read from the cache. A provider that does not report them shows as "not reported", never as zero.

**Step 2: a stable start.** A request is ordered so that what was sent before is sent again unchanged, and what is new comes after it:

1. the fixed instructions;
2. the tools, in a fixed order;
3. the ancestors, from the root down;
4. the conversation so far;
5. what changes with this request: the line that counts the rounds, and the current question.

Rules that keep it so:

- Nothing that changes from request to request stands before the conversation. The counting line moves to the end. Its wording is not changed here.
- A tool result is shortened at most once, and is sent in that form from then on.
- When part of the context is replaced by a context summary, the start changes once from that point and is stable again afterwards. This is accepted.
- A test fixes the rule: in a run, each request begins with exactly what the previous request sent; and the first requests of two sibling branches are identical up to the branch point.

This step helps with every provider that caches on its own, at no extra price.

**Found while writing the architecture text (2026-11-27).** The answer instructions adopted with reply quality (requirements §10.9, architecture §15.5) put two more parts at the start of a request that differ from request to request: the branch part, which names the selection, and the titles of matching summaries. Left there, they would break the shared start between sibling branches and between two questions. The order in architecture §15.5 therefore moves them to the end as well, beside the counting line. Their wording is unchanged. Instructions that stand at the end of a request instead of its start may be followed differently by a model, so this step is checked with the run evaluation before it merges, within the owner's budget.

**Step 3: cache points for Messages.** On a Messages connection, the request marks up to three places: the end of the instructions and tools, the end of the ancestors (the part siblings share), and the end of the conversation so far. Long runs get a further mark where the protocol needs one. Marks are sent only for a model that declares prompt caching, which the presets fill in.

**The lifetime.** A setting in the connection's advanced settings: off, 5 minutes, or 1 hour. The default is 1 hour (owner). Mindweave sends no requests on its own to keep a cache alive.

**Storage.** New columns on `ai_request`, so one migration.

#### What it needs, and what leaves the machine

- **macOS and Windows:** no platform work.
- **Leaves the machine:** nothing new. The provider keeps the start of recent requests on its side for the lifetime of the cache.
- **Verification:** steps 1 and 2 against the fake server, and by reading what providers report in normal use. Step 3 against the real service with the owner's key, within the owner's budget.

#### Out of scope

- Requests that only keep a cache alive, or that fill it before a question.
- A lifetime per conversation.
- What the instructions and prompts say. That belongs to the reply quality discussion (§4); this topic only fixes the order.
- Publishing the measured numbers as a write-up ([more directions](../ideas/more-directions.md), technical pieces). It can follow once there are numbers, as an outward-facing step the owner decides.

#### Failure cases

| Case | What happens |
| --- | --- |
| The provider does not report cached tokens | Recorded as not reported; the statistics say so |
| A service that speaks Messages refuses the marks | The request fails with the reason; setting the lifetime to off is the remedy. Nothing is resent on its own |
| The start is shorter than the provider's minimum | It is not cached; nothing needs doing |
| The next question comes after the lifetime has passed | The start is written again at the writing price; the statistics show it |
| The current model is switched | The new model starts with an empty cache, because caches are per model |
| The tools offered change between two questions | With Messages, the cache is missed from the start for that question; tools stand first in that protocol |
| Part of the context is compressed | One miss from that point, then stable again |

#### Acceptance scenarios

1. After a question to a provider that reports cached tokens, the request's details show how many input tokens were read from the cache.
2. The statistics show, per model, the share of input read from the cache, and what was written to it.
3. In a run with three tool steps, the second and third request each begin with exactly what the previous one sent (test).
4. The first requests of two sibling branches from the same answer are identical up to the branch point (test).
5. On a Messages connection, a second question within the hour reports the earlier conversation as read from the cache.
6. A sibling branch asked after the first reports the ancestors as read from the cache.
7. With the lifetime set to off, no marks are sent and nothing is reported as written.
8. With a provider that reports nothing, the statistics show "not reported".

#### First increment

Three steps in this order (owner), each usable when merged:

1. Measure.
2. The stable start. It changes the run loop, so it is coordinated with the reply quality discussion, which owns the run's instructions.
3. The cache points and the lifetime setting. It needs the Messages adapter (step 3 of the connections increment).

#### After this section is agreed

Requirement and architecture text follows as a separate commit that the owner confirms: the order of the context in architecture §6, the statistics in the requirements, and evolution D4 pointing to it.
