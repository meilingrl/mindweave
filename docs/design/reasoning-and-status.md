# Reasoning and run status

Basis: the user's plan of 2026-11-20, step 1 (how reasoning is presented and how the run status is described, together with the existing weaving motions), and the user's decisions (reasoning is shown live only while an answer is being generated, collapsed by default; it is never saved, and does not go into branches, summaries or sharing; the wording of the statuses and how they match the motions is up to the implementer). Requirements §10.4.4 (v0.21), [Generating indicator](generating-indicator.md), [Runs and tools](runs-and-tools.md) R10, [Run loop improvements](agent-loop.md).

## 1. Reasoning

**Reading.** “只在生成回答时实时显示” (shown live only while an answer is being generated) is read as follows: while the answer is in progress (from sending to the end, including while the body streams in and between two tool calls), it can be expanded and read; when the answer ends (completed, stopped, failed, interrupted), the collapsed area disappears together with its content. The reading "it disappears as soon as the body starts" was not adopted: during a run the model thinks again between two tool calls, so there is new reasoning even after the body has started.

| Aspect | Approach |
| --- | --- |
| Position | At the top of the answer, above the activity text: one line, “思考过程” (thinking process), with an expand arrow; it appears only once reasoning has been received |
| Default | Collapsed. When expanded, it is a quiet area (secondary text color, at most about 200 px high, scrollable, following the latest content); the expanded state holds only while this answer is in progress |
| Several rounds | The reasoning of each model request in a run is joined in order, separated by a blank line |
| Memory | Only in the answer in progress (`LiveAnswer`); at most the last 20,000 characters are kept, and earlier parts are dropped, with “…” marked at the start |
| Not saved | Not written to the database and not put into the answer's blocks, so it does not go into branches, summaries, titles, context, exports or sharing; no migration needed. Cleared when the answer ends |
| Sending back | R10 is unchanged: within the same run it is sent back to the service with the tool calls (`reasoning_content`), regardless of display |
| Switching pages | When the user leaves while an answer is in progress and comes back, the reasoning received so far is still read from `LiveAnswer` |

## 2. One set of statuses

While an answer is in progress, the end of the answer always has one status line: the three weaving dots, one sentence of text, and a timer. It replaces the former separate "waiting text" and “正在生成…” (generating…). The activity text above the body records only the steps done (“搜索了 1 次 · 读了 1 个网页” (searched 1 time · read 1 web page)) and no longer shows “正在…” (currently…); what is happening now is said only in this line.

Wording rules: present progressive, “正在 + 动词 + 对象…” ("正在" + verb + object…); the object (query, page host, tool name) goes after a colon, at most 40 characters; no metaphors, so that step 2 can translate it into English sentence by sentence. All text goes through `UiText`.

| Stage (`AnswerStage`) | When | Text | English | Motion |
| --- | --- | --- | --- | --- |
| `Waiting`: waiting | Queued, connecting, or nothing at all yet in this round | 正在等待回答… | Waiting for the answer… | braiding `Braid` |
| `Compressing`: compressing | Compressing earlier conversation before sending | 正在压缩较早的对话… | Compressing earlier conversation… | tightening `Tightening` |
| `Thinking`: thinking | Reasoning received in this round, no body yet | 正在思考… | Thinking… | joining `Joining` |
| `Tool`: web search | `web_search` executing | 正在搜索：{检索词}… | Searching: {query}… | reaching `Reaching` (new) |
| `Tool`: read a page | `fetch_page` executing | 正在读取网页：{主机}… | Reading web page: {host}… | reaching `Reaching` |
| `Tool`: search attachments | `search_attachments` | 正在检索附件：{检索词}… | Searching attachments: {query}… | reaching `Reaching` |
| `Tool`: search summaries | `search_summaries` | 正在检索总结网络：{检索词}… | Searching summary network: {query}… | reaching `Reaching` |
| `Tool`: third party | An MCP tool executing | 正在调用 {服务器} › {工具}… | Calling {server} › {tool}… | reaching `Reaching` |
| `Approval`: waiting for approval | When the approval card appears | 等待你批准读取网址… ／ 等待你批准调用 {服务器} › {工具}… | Waiting for you to approve reading the URL… / calling {server} › {tool}… | holding `Holding` (new) |
| `Retrying`: retrying | L5: a request failed by chance; waiting, then retrying, until the new request has content | 请求出错，正在重试… | Request failed, retrying… | braiding `Braid` |
| `Finishing`: finishing | The last request after a limit is reached or a middle round was truncated, with no content yet | 正在根据已有结果作答… (the limit note is already below and is not repeated) | Answering from the results so far… | braiding `Braid` |
| `Writing`: writing the answer | The body of this round is streaming in | 正在写回答… | Writing the answer… | threading `Thread` |
| End | When a limit is reached, the note below the answer is unchanged (“达到了本次回答的……上限，根据已经取得的结果作答。” (reached the … limit of this answer; answering from the results already obtained)) | — | — | — |

**Order of decision** (when several hold, the earlier one wins): waiting for approval → tool executing → compressing → this round already has body text (writing the answer) → thinking → retrying → finishing → waiting. "This round" means one model request in a run: when the second round starts, the status goes back to waiting or thinking even if the previous round wrote body text, rather than showing “正在写回答” (writing the answer) all along. It is computed by `LiveAnswer`; the UI only takes `Stage`.

The indicator for summary generation is unchanged (braiding).

## 3. Two new motions

They follow the family of motions in [Generating indicator](generating-indicator.md): three dots, accent color, 44×20 px, a 0.3-second sliding transition between stages.

| Motion | Description | Meaning |
| --- | --- | --- |
| reaching `Reaching` | Two dots stay on the left; the third reaches out to the right along a faint line and back, period 1.4 seconds; it is most solid at its farthest point | Reaching out for material (searching, reading pages, calling tools) |
| holding `Holding` | The three dots sit still in a row and breathe slowly together (size and intensity), period 2.4 seconds | Stopped to wait for you |

## 4. Code

| Where | Change |
| --- | --- |
| `ChatStreamEvent.ReasoningDelta` | Carries the reasoning text (formerly only the character count); `Characters` stays, as the text length |
| `LiveAnswer` | `AppendReasoning` (replacing `MarkThinking`), `AnswerStage`, the snapshot's `Reasoning` and `Stage`; `BeginTurn(final)` starts a new round; reasoning is cleared at the end |
| `AnswerRunner` | Both paths hand the reasoning text to `LiveAnswer`; the stage is set to retrying before an L5 retry |
| `MessageItemViewModel` | `StatusText`, `Motion`, `Reasoning`, `HasReasoning`, `IsReasoningExpanded`; `ActivityText` records only steps |
| `UiText.AnswerStatus` | The wording of section 2 |
| `WeavingPaths` | `Reaching`, `Holding` |

## 5. Changes to the requirements

Committed separately (AGENTS.md "Changing requirements or architecture"), requirements v0.21: the activity text item of §10.4.4 changes to "records only the steps done; the current status is in the status line at the end of the answer", and a "reasoning" paragraph is added; architecture §15.6 and R10 note that reasoning can be shown live and is still not saved.

## 6. Not in this change

- `<think>…</think>` inline in the answer body (a few services write reasoning into the body): it stays in the answer body (D-029; titles, summaries and compression remove it).
