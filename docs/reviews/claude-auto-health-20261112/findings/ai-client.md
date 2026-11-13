# AI provider client and context management — grade B

B-minus. The provider client and context pipeline are deliberately engineered: layered timeouts, correct separation of user cancellation from timeouts, key redaction, no automatic re-send of user questions, internal outputs accepted only on a clean non-empty finish, and a calibration scheme with outlier and no-room guards backed by real-service measurements. It falls short of an industrial bar in three places users will plausibly hit: (1) user-facing answers cut off by the provider (finish_reason=length/content_filter) are stored and shown as Complete with no signal, and there is no UI to raise the output cap; (2) response shapes outside the happy path (string-valued "error", non-object JSON) throw InvalidOperationException, which no runner catches, leaving the answer permanently "streaming" until restart; (3) every internal summary/compression request forces max_tokens=16384 regardless of the model, which many models reject. The rest are narrower provider-compatibility and planner-efficiency gaps. No runaway loops and no silent truncation of context were found. This audit was by code reading only; nothing was executed.

**Strengths**

- Timeouts are layered and distinguish user cancellation from stalls: ConnectTimeout 30s, FirstByteTimeout 90s to headers, IdleTimeout 120s re-armed per event via a linked CTS, with HttpClient.Timeout set to infinite so the stream is not cut at 100s (ChatCompletionsClient.cs:16-22, 70-75, 185-198, 211-221; InfrastructureServices.cs:49).
- A stream that ends without finish_reason or [DONE] is never treated as success: the client emits no Completed event (ChatCompletionsClient.cs:76-85) and AnswerRunner marks the answer Failed with a clear detail (AnswerRunner.cs:125-134). Covered by A_stream_that_ends_without_finishing_yields_no_completion and StreamDropTests.
- Internal outputs the program consumes (context summaries, conversation summaries, titles, names) are used only on a normal, non-empty finish; truncated ones are rejected and compression retries exactly once inside the task (ContextCompressor.cs:146-158, 195-202; GenerationRequests.cs:118-128). This came from a measured real failure (TRACK 2026-10-27, D-015).
- Nothing is automatically re-sent on behalf of the user; the only retry is the internal summary step. AllowAutoRedirect=false keeps the API key from following a redirect to another host (ChatCompletionsClient.cs:32-38, 274-277), and every ChatClientException leaving the client is scrubbed of the key (lines 56-59, 303-304).
- Token calibration is defensive: it only ever inflates the estimate, fits a slope only when samples span a 4x size range with max >= 1000, drops high-residual outliers from 3 samples on, clamps scale to 1-3, and caps overhead at a quarter of input space so a bogus usage report cannot lock a model out (ContextBudget.cs:82-135, 161-163). EstimateWithin/Predict round in the safe direction (lines 70-73).
- Context planning has no runaway path: one pass over chunks, required items never summarized, summaries built from original messages only, reuse keyed by conversation + seq range + message-id hash + prompt version with an ON CONFLICT upsert that makes concurrent duplicate summarization safe (ContextPlanner.cs:66-119; SqliteContextSummaryStore.cs:42-61). Post-compression size is re-checked against capacity minus output reserve (ContextCompressor.cs:99-103).
- Provider differences found in real trials are handled and tested: usage at top level or inside the final choice (Kimi), usage never requested, reasoning_content passed on as length only and keeping the idle timer alive, [DONE] without finish_reason treated as a clean finish (ChatCompletionsClient.cs:249-260; ChatCompletionsClientTests lines 44-116).
- Every request, including each compression sub-request and failed or stopped ones, gets an ai_request row with status, provider request id, timings, outcome, usage and the estimate at send time; trace write failures are logged and never affect the answer (RequestTrace.cs; GenerationRequests.cs:132-144).

**Not examined**

- Did not run any tests or the app (read-only audit); all findings are from code reading. Baseline build/test outputs in the scratchpad were not consulted.
- No live provider calls: provider-behaviour claims (max_tokens limits, mid-conversation system messages, keep-alive comments, string-valued error bodies) come from knowledge of those APIs, not from testing against them in this audit.
- FakeChatClient and the full test suites for compression/calibration were only skimmed by name; test adequacy beyond ChatCompletionsClientTests was not assessed.
- ConversationTitler, SummaryNaming, WorkspaceNameSuggestions prompt construction and output parsing; SummaryService save path beyond planning.
- Credential store, Redaction.Scrub internals, and base-URL normalization (security dimension).
- SqliteAiRequestStore writes and statistics queries beyond RecentTokenSamplesAsync; migration correctness for ai_request columns.
- Desktop view models' rendering of error kinds and the context-usage indicator; only grepped to confirm no truncation indicator exists.
- TerminalWrites retry behaviour and StorageHealth admission logic.
- Startup recovery of tasks/messages left 'running'/'streaming' (relevant to how the stuck-answer finding resolves after restart).

### ai-client-1. Answers cut off by the provider (finish_reason=length or content_filter) are stored and shown as Complete, with no indication and no way to raise the cap

- Final: **high** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Application/Conversations/AnswerRunner.cs:119`; effort M

**Evidence.** AnswerRunner.cs:119-127 handles `ChatStreamEvent.Completed` by setting `completed = true` and then `final = MessageState.Complete`; the `Truncated` flag is never read. The only consumers of `Truncated` are RequestTrace (writes ai_request.outcome='truncated', which no UI reads; grep for AiRequestOutcome in src/Mindweave.Desktop returns nothing) and the internal-request paths. ChatCompletionsClient.cs:101 maps only `reason == "length"` to Truncated; `content_filter` is a plain completion. max_tokens is sent only when ModelSnapshot.MaxOutputTokens is set (ChatCompletionsClient.cs:173-176), and ModelSettingsInput (ModelSettingsService.cs:19) has no such field; line 118 only preserves `existing?.MaxOutputTokens`, so for normal answers the provider's default output cap applies. The project already treats this as dangerous for internal requests (TRACK 2026-10-27, D-015) but not for the user's answers.

**Failure scenario.** A user asks for a long answer on a model whose default output cap is small (several providers default to 1k-4k tokens), or on a reasoning model that spends the cap on reasoning. The stream ends with finish_reason=length. The answer is saved as Complete: it ends mid-sentence, or is entirely empty, with no error, no notice and no retry hint. The truncated text is then sent as history, auto-titled, can be summarized into the summary network and branched from as if it were whole. The same happens when the provider stops with content_filter.

**Recommendation.** Carry the finish reason through to the message (a distinct state or a flag on Complete) and show a lightweight 'stopped at the output limit' note with retry. Treat Completed with empty text as a failure (InvalidResponse) rather than Complete. Expose max output tokens in model settings. Map content_filter to a visible reason.

**Skeptic: partially confirmed, medium.** The code facts hold. AnswerRunner's stream loop sets `completed = true` on `ChatStreamEvent.Completed` without reading `Truncated`, then stores `MessageState.Complete`; there is no check for empty text. The client maps only `finish_reason == "length"` to Truncated, so `content_filter` is a plain completion. `max_tokens` is sent only when `ModelSnapshot.MaxOutputTokens` is set, and `ModelSettingsInput` has no such field, so the stored value is always null for user answers. No Desktop code reads the truncated outcome. Every internal request path (compression, summaries, titles, naming, workspace names) sets an explicit cap and rejects truncated or empty results per D-015; the user-answer path alone does not.

I found no guard, test or decision making this intended. docs/design/s1-chat.md:59 says "有 finish_reason 即完成" (any finish_reason means finished), which is a parsing rule written before the 2026-10-26/27 findings, not a decision to hide truncation. TRACK.md lists the reasoning-model cap problem only for internal requests; nothing in 待决定 or 发现 covers user answers. The only test touching this (ChatCompletionsClientTests.cs:108) asserts the client flag, not the answer state.

Why medium rather than high:
- "No way to recover" is overstated. The last finished answer in a conversation, including a Complete one, gets a "重新生成" (Regenerate) action, and the user can simply ask the model to continue.
- Frequency is lower than claimed. With no `max_tokens` sent, most OpenAI-compatible providers apply the model's maximum or a generous default; the 4,096 and 64 caps seen in TRACK were … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. "No way to raise the cap" is correct for settings, but "no retry hint" is overstated: the last answer in a conversation shows a "重新生成" (Regenerate) action even when Complete (MessageItemViewModel.cs:194, MessageListReconciler.cs:69). There is still no notice telling the user the answer was cut off.
2. "Several providers default to 1k-4k tokens" is an unverified generalization. The app sends no `max_tokens` for user answers, and many compatible providers then use the model maximum. The small caps recorded in TRACK (64, 4,096) were set by the app on internal requests.
3. Severity high is overstated: no data loss, a visible symptom in the common case, and a user workaround exist. Medium fits; the empty-Complete-answer and truncated-answer-into-summary cases are the parts worth fixing first.
4. Minor: `ai_request.outcome` is read back by SqliteAiRequestStore.cs:64, but no Desktop UI surfa … (cut; full text in the workflow journal)

**Reproduction attempt: confirmed, high.** Reproduced with three focused xUnit tests against track/claude-auto (1ba95c9); all three passed, and each asserts the defective behaviour.

1. Cut-off answer stored as whole. A client that ends the answer stream with Completed(Truncated: true) after a mid-sentence text produces a message with State=Complete, ErrorKind=null, ErrorDetail=null. The only trace is the ai_request row with outcome Truncated. On the next question the cut-off text is sent back as an ordinary assistant turn.
2. Empty answer stored as Complete. Reasoning followed by Completed(Truncated: true) with no text gives State=Complete, Text="" and no error.
3. content_filter is a plain completion. ChatCompletionsClient maps finish_reason=content_filter to Completed(Truncated: false).
4. No output cap on normal answers. The answer request built through the normal settings path has Model.MaxOutputTokens == null, and the HTTP body then contains neither max_tokens nor max_completion_tokens. ModelSettingsInput has no field for it, and nothing in src/Mindweave.Desktop references MaxOutputTokens, Truncated or AiRequestOutcome, so the user can neither see the truncation nor raise the cap.

The project already treats this as dangerous for internal requests: GenerationRequests.cs:120 and ContextCompressor.cs:197 reject truncated results, and TRACK.md:146 (2026-10-27, D-015) records why. User answers are the one path that ignores the flag. TRACK.md does not list this as a known finding.

High stands: it is silent corruption of the product's primary content, which then feeds history, titles, summaries and branches, and re … (cut; full text in the workflow journal)

*Corrections to the finding.* Nothing in the finding is wrong; these parts are narrower than stated or were not tested.

- "Several providers default to 1k-4k tokens" is a claim about external services that I did not check (no network or paid calls). How often real users hit this depends on the provider and model; the code behaviour is certain either way.
- The auto-title, summarise and branch consequences are inferred from the message being stored as an ordinary Complete answer. I directly reproduced only the reuse as history.
- I did not check how the Desktop view renders an empty Complete answer. The absence of any truncation indicator rests on the grep of src/Mindweave.Desktop, not on a UI test.
- An existing test, Done_without_finish_reason_still_completes_and_length_marks_truncation (ChatCompletionsClientTests.cs:108), covers the client-level length flag, so the gap is specifically AnswerRunner ignoring it and … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Temporary file tests/Mindweave.Infrastructure.Tests/Chat/ZzTruncatedAnswerReproTests.cs (deleted afterwards). The assertions describe current behaviour, so a pass means the finding is real.

```csharp
[Fact]
public async Task Repro_answer_cut_at_length_limit_is_saved_as_Complete_and_reused_as_history()
{
    var client = new CutOffClient("蒸汽机车的发明始于十八世纪，其中最重要的");
    await using var h = await ChatHarness.StartAsync(client: client);
    var sent = await h.Conversations.StartAsync(h.Workspace, "详细讲讲火车的历史", Ct);
    var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

    Assert.Equal(MessageState.Complete, answer.State);
    Assert.Null(answer.ErrorKind);
    Assert.Null(answer.ErrorDetail);
    Assert.Equal("蒸汽机车的发明始于十八世纪，其中最重要的", answer.Text);

    var task = await h.Database.ReadAsync(c => {
        using var cmd = c.Command(null, "SELECT id FROM generation_task WHERE target_message_id = $id;", ("$id", answer.Id.ToString()));
        return GenerationTaskId.Parse((string)cmd.ExecuteScalar()!); }, Ct);
    IReadOnlyList<AiRequestRecord> records = [];
    await ChatHarness.UntilAsync(async () => (records = await h.Requests.ForTaskAsync(task, Ct)).Count > 0);
    Assert.Equal(AiRequestOutcome.Truncated, Assert.Single(records).Outcome);

    Assert.Null(client.Answers[0].Model.MaxOutputTokens);

    var next = await h.Conversations.AskAsync(sent.Conversation, "继续", Ct);
    await h.WaitForAnswerAsync(next.Answer!.Value);
    Assert.Contains(client.Answers[1].Turns, t => … (cut; full text in the workflow journal)
```

</details>

### ai-client-2. Unexpected response shapes throw InvalidOperationException, which no runner catches: the answer stays 'streaming' until restart

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:283`; effort S

**Evidence.** ThrowIfFailedAsync line 283: `JsonNode.Parse(text)?["error"]?["message"]` sits inside `catch (JsonException)` only. If the body is `{"error":"model not found"}` (error is a string), a JSON string, or an array, the JsonNode string indexer calls AsObject() and throws InvalidOperationException. The same applies in Parse: line 244 `node?["error"]` on a non-object event, line 246 `error["message"]` on a string-valued error, line 249 `.AsArray()` on a non-array `choices`; and in ListModels lines 134-135 (`AsArray()`, `GetValue<string>()`). AnswerRunner.RunAsync (lines 136-146) catches only OperationCanceledException-when-stopped and ChatClientException. Anything else propagates to GenerationCoordinator.RunAsync line 531, which logs 'crashed' and frees the slot but never calls FinishAsync. Tests cover only the object-shaped error (ChatCompletionsClientTests 126-175).

**Failure scenario.** A local server, gateway or relay answers 401/404/500 with `{"error":"..."}` (string-valued error is common outside OpenAI itself). InvalidOperationException escapes the runner. The LiveAnswer stays Streaming, the message and task rows stay streaming/running, no ai_request row is written, and the checkpoint loop keeps ticking forever because its CancellationTokenSource is disposed without being cancelled. The user sees an endless spinner; Stop does nothing (the work is no longer in _running, coordinator lines 240-256); a follow-up is presumably refused because the stored answer is still streaming (per the comment at coordinator lines 154-157). The conversation is unusable until the app restarts, and the real error text is never shown. The same crash path is reached by non-ChatClient exceptions in the compression prelude (e.g. a storage failure in summaries.AddAsync, ContextCompressor.cs:84). In settings, a malformed /models response throws past ModelSettingsViewModel's catch list (lines 279-287).

**Recommendation.** Make the client total: use `as JsonObject` / `as JsonArray` pattern checks instead of indexers on unknown nodes, and convert InvalidOperationException/FormatException in Parse, ThrowIfFailedAsync and ListModels into ChatClientException(InvalidResponse). Independently, add a catch-all in AnswerRunner.RunAsync (and a try/finally around the checkpoint loop) that always reaches FinishAsync with Failed, so no exception type can leave an answer non-terminal.

**Skeptic: confirmed, high.** I could not refute it; every link in the chain exists in the code as claimed. Nothing was executed (read-only audit), so this is confirmed by reading, not by reproduction.

1. The client is not total. ThrowIfFailedAsync line 283 chains `JsonNode.Parse(text)?["error"]?["message"]` inside a try that catches only JsonException. The JsonNode string indexer calls AsObject(), which throws InvalidOperationException on a JsonValue or JsonArray. So a non-2xx body of `{"error":"model not found"}`, a bare JSON string, or an array throws InvalidOperationException. Parse has the same problem at line 244 (non-object event), line 246 (string-valued error) and line 249 (`AsArray()` on a non-array `choices`). ListModelsCoreAsync lines 134-135 throw on a non-array `data` or a non-string `id`.

2. Nothing converts it on the way up. SendAsync's bare `catch` (204-208) only disposes and rethrows. StreamAsync (56-59) and ListModelsAsync (117-120) catch only ChatClientException. GenerationRequests.StreamAsync (49-50) is a pass-through.

3. AnswerRunner.RunAsync catches only OperationCanceledException-when-stopped (136) and ChatClientException (140). Any other exception skips lines 148-151, so there is no checkpointStop.CancelAsync, no FinishAsync and no RecordAsync (no ai_request row).

4. GenerationCoordinator.RunAsync (531-535) logs "Generation task {TaskId} crashed" and its finally removes the work from _running. It never finishes the LiveAnswer or the stored rows.

5. The stuck-state consequences hold:
- StopAsync (233-256) looks only in the queue and in _running, so it does nothing for this a … (cut; full text in the workflow journal)

*Corrections to the finding.* - "The checkpoint loop keeps ticking forever" is accurate as a leak, but the practical effect is small: nothing appends to the LiveAnswer after the crash, so at most one more checkpoint write happens and later ticks do nothing. It is an orphaned timer loop per crashed answer, not continuing I/O.
- "A follow-up is presumably refused" is supported by the count query at SqliteConversationStore.cs:279 and the coordinator comment. I did not trace the exact refusal site in ConversationService or what the UI shows.
- The compression-prelude variant depends on summaries.AddAsync throwing something other than the three caught types. I did not verify what the SQLite summary store throws on failure, so that sub-claim is plausible rather than confirmed.
- I did not trace what happens to the InvalidOperationException after it escapes ModelSettingsViewModel.RunAsync (crash, or swallowed by the command … (cut; full text in the workflow journal)

**Reproduction attempt: confirmed, high.** Reproduced end to end against track/claude-auto at 1ba95c9 (checked out detached in my isolated worktree, since the worktree branch itself is cut from main and has no src/). 11 of 11 repro tests failed in the way the finding predicts; none of the results are timing-dependent (the state was still wrong after a 1.5 s settle with a 50 ms checkpoint interval, and the client-level cases are synchronous).

1. Client is not total. ChatCompletionsClient.StreamAsync throws System.InvalidOperationException ("The node must be of type 'JsonObject'" / "'JsonArray'") instead of ChatClientException for: HTTP 401 `{"error":"invalid api key"}`, HTTP 404 body `"model not found"` (a JSON string), HTTP 500 `["boom"]`, SSE event `{"error":"upstream overloaded"}`, SSE event `[1,2]`, SSE event `"hello"`, and SSE `choices` as an object. ListModelsAsync throws InvalidOperationException for `{"data":"nope"}`, `{"data":[{"id":5}]}` and `[1]`.

2. The answer is left non-terminal. With the real client, real GenerationCoordinator and real SQLite (ChatHarness), a 401 with a string-valued `error` leaves: stored message state Streaming, live state Streaming, generation_task state running, 0 ai_request rows, coordinator UnfinishedCount 0 and HasUnfinishedAnswer false (slot freed, work gone from _running). StopAsync returns without changing anything. A follow-up AskAsync is rejected with AnswerInProgress and ConversationRules.CanRetry is false, so the user can neither stop, retry nor continue in that conversation in this process, and never sees the service's error text.

Code reading matches: AnswerRunner.Ru … (cut; full text in the workflow journal)

*Corrections to the finding.* Mostly accurate; small adjustments only.
- "A follow-up is presumably refused" is now confirmed, not presumed: AskAsync returns SendRejection.AnswerInProgress, and CanRetry is false as well.
- "The checkpoint loop keeps ticking forever" is true by code reading but overstated as an impact: after the crash it only wakes each interval and compares revisions; it writes only if text arrived that was not yet checkpointed, and then goes idle. It is a small leak per crashed answer, not ongoing work. Not demonstrated by a test.
- "Unusable until the app restarts" — the stuck state within the process is demonstrated; that a restart actually recovers it (startup marking streaming answers as interrupted) was not verified here.
- The coordinator also reports HasUnfinishedAnswer == false and UnfinishedCount == 0 for the stuck conversation, so in-memory and stored state disagree; the finding did not me … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Temporary file tests/Mindweave.Infrastructure.Tests/Chat/ReproUnexpectedShapeTests.cs (deleted afterwards). Core of it:

```csharp
public static TheoryData<int, string, string> Shapes => new()
{
    { 401, "{\"error\":\"invalid api key\"}", "http-error-string" },
    { 404, "\"model not found\"", "http-json-string" },
    { 500, "[\"boom\"]", "http-json-array" },
    { 200, "data: {\"error\":\"upstream overloaded\"}\n\n", "sse-error-string" },
    { 200, "data: [1,2]\n\n", "sse-array-event" },
    { 200, "data: \"hello\"\n\n", "sse-string-event" },
    { 200, "data: {\"choices\":{\"delta\":{\"content\":\"x\"}}}\n\n", "sse-choices-object" },
};

[Theory, MemberData(nameof(Shapes))]
public async Task Client_reports_unexpected_shapes_as_ChatClientException(int status, string body, string name)
{
    var client = new ChatCompletionsClient(new HttpClient(new FixedHandler(status, body)), new ChatClientOptions());
    var thrown = await Record.ExceptionAsync(async () =>
    {
        await foreach (var _ in client.StreamAsync(new ChatRequest(Model, [new ChatTurn(ChatRole.User, "hi")]), "gw-key-0123456789", Ct)) { }
    });
    Assert.IsType<ChatClientException>(thrown);
}

[Theory]
[InlineData("{\"data\":\"nope\"}")]
[InlineData("{\"data\":[{\"id\":5}]}")]
[InlineData("[1]")]
public async Task ListModels_reports_unexpected_shapes_as_ChatClientException(string body)
{
    var client = new ChatCompletionsClient(new HttpClient(new FixedHandler(200, body)), new ChatClientOptions()); … (cut; full text in the workflow journal)
```

</details>

### ai-client-3. Compression and summary requests force max_tokens=16384 regardless of the model's output limit, window, or parameter support

- Final: **high** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Application/Conversations/ContextCompressor.cs:137`; effort M

**Evidence.** ContextCompressor.cs:137 `task.Model with { MaxOutputTokens = SummaryOutputTokens }` (16_384, line 46); SummaryService.cs:166 does the same for conversation summaries; ChatCompletionsClient.cs:173-176 sends it as `max_tokens`. Nothing compares it with the model's real output cap or declared context window, and the summary request's own input (the whole chunk transcript, lines 131-135) is never checked against any budget; a chunk closes only at a question boundary after 4,000 tokens (ContextPlanner.cs:173), so one long pasted message or answer makes an arbitrarily large chunk. ChatClientException in a summary request is not retried and becomes CompressionFailed (lines 188-193). TRACK line 84 records only the small-context-window variant (~8k local models) as a pending decision.

**Failure scenario.** (a) The user's model has an output limit below 16,384 (many 4k/8k-output chat models): the provider answers 400 'invalid max_tokens'. Every answer that needs compression fails with CompressionFailed, and retry repeats it, so once a conversation outgrows the budget (default 32k assumed) it can never continue on that model; 'Summarize conversation' fails on that model always. (b) The endpoint rejects the `max_tokens` parameter itself (newer reasoning APIs require max_completion_tokens): titles, names, summaries and compression all fail while plain answers (which send no cap) work. (c) A single 30k-token chunk plus 16k output exceeds a 32k window: 400, permanently, for that chunk.

**Recommendation.** Derive the internal cap from model capabilities: min(16,384, declared max output, window minus chunk estimate minus margin). On a 400 that names max_tokens, retry once without the parameter or with a smaller value. Budget the summary request itself and split oversized chunks. Record output cap as a model capability (E4) and surface it in settings.

**Skeptic: partially confirmed, medium.** The cited code exists and behaves as described; I found no guard, fallback or test that prevents the scenarios. Two parts of the claim are overstated, and the impact depends on the provider, so I lower the severity to medium.

What holds:
- ContextCompressor overrides the model's cap with a constant 16,384 for every compression summary request, and SummaryService does the same for conversation summaries.
- ChatCompletionsClient sends that value as `max_tokens`. There is no `max_completion_tokens` path anywhere in src/.
- There is no retry without the parameter or with a smaller value. A 400 maps to ContextTooLong or InvalidResponse, becomes ChatClientException, and the compressor wraps it straight into ContextCompressionException. The two-attempt loop only covers truncated or empty summaries.
- AnswerRunner and TaskRunners turn that into CompressionFailed. A user retry rebuilds the same request, so the failure repeats.
- The compressor's own request is not budgeted. The transcript is the whole chunk, and a chunk only closes at a user message once it has reached 4,000 tokens. Questions may be up to 100,000 characters and answers are unbounded, so one long message makes one arbitrarily large chunk.
- The output cap is not a user setting: `MaxOutputTokens` is not referenced anywhere in Mindweave.Desktop, and ModelSettingsService only carries the existing value forward. Plain answers therefore send no cap, which makes the asymmetry in (b) real, and there is no in-app workaround.
- 16,384 is a deliberate decision (D-015), made after reasoning models exhausted 4,096. The decision … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. SummaryService does budget its request against the declared or default window with a 16,384 reserve (SummaryService.cs:178-183, ContextBudget.cs:181). The "never checked against any budget" claim and scenario (c) apply only to ContextCompressor's inner summary request.
2. The window-overflow part of the compressor request is already documented as a pending decision (s3-context.md:25, TRACK.md:84). What is new is that the chunk is unbounded rather than about 4,000 tokens, so it can also hit models with a 32k or larger window, plus the output-cap and parameter-name variants (a) and (b), which are recorded nowhere.
3. Scenarios (a) and (b) depend on the provider rejecting rather than clamping `max_tokens`. That is plausible for several real APIs but was not verified here, and the providers this track tested accept 16,384.
4. Severity should be medium: a compatibility defect with a visibl … (cut; full text in the workflow journal)

**Reproduction attempt: confirmed, high.** Reproduced with three focused xUnit tests (written against track/claude-auto at 1ba95c9, run through the real store/coordinator via ChatHarness; all three fail as the finding predicts).

(a) Confirmed. With a client that behaves like the fake but answers HTTP 400 when max_tokens exceeds 8,192 (simulated provider behaviour, as OpenAI/DeepSeek-style services do for 4k/8k-output models): plain answers succeed (they send no cap, MaxOutputTokens is null), but the first answer that needs compression fails with CompressionFailed, and Retry fails identically - both summary requests carried max_tokens=16384. "Summarize conversation" fails on the first attempt for even a one-turn conversation. There is no fallback path: a ChatClientException in the summary request is wrapped straight into ContextCompressionException (not covered by the SummaryAttempts=2 loop, which only handles truncated/empty results).

(c) Confirmed without any simulated provider. On the default 32k window, a 20,000-char pasted first question becomes one chunk; the compressor's summary request was input estimate 20,232 + max_tokens 16,384 = 36,616 > 32,000. Nothing in ContextCompressor.SummarizeAsync budgets that request, and ContextPlanner only closes chunks at a question boundary, so a single oversized message cannot be split.

(b) Not reproduced by test (needs a real endpoint's behaviour); confirmed by code reading only: ChatCompletionsClient always emits the cap as `max_tokens`, never `max_completion_tokens`, and titles/naming/workspace names/summaries/compression all set a cap.

Why high stands: MaxOutputToken … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. ContextPlanner lives at src/Mindweave.Core/Context/ContextPlanner.cs (line 173 is right), not under Application/Conversations.
2. "The summary request's own input is never checked against any budget" is true for the compressor's chunk requests only. SummaryService does budget the knowledge-summary input, using the 16,384 reserve (SummaryService.cs:180). The side effect there is different and not in the finding: with a declared window at or below roughly 18k, capacity - 16,384 - margin leaves no input space, so "Summarize conversation" would be rejected as TooLongForModel before any request (code reading, not tested; settings accept windows down to 2,000).
3. "Many 4k/8k-output chat models answer 400" is provider-dependent: some services clamp an over-large max_tokens instead of rejecting it. My reproduction simulates the rejecting kind; the code has no defence against it, but the affe … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

File (temporary, since deleted): tests/Mindweave.Infrastructure.Tests/Chat/ReproSummaryOutputCapTests.cs
Command: dotnet test tests/Mindweave.Infrastructure.Tests --filter "FullyQualifiedName~ReproSummaryOutputCapTests"

Test code (essentials):

    private static readonly ModelSettingsInput SmallModel = new("fake", "https://fake.example/v1", "fake-model", "sk-test", ContextWindowTokens: 12_000);
    private static readonly string LongAnswer = new('史', 1_000);
    private static readonly string Tipping = new('补', 800);

    [Fact] // (c) default 32k window, no simulated provider
    public async Task Repro_c_summary_request_of_a_large_chunk_fits_the_default_window()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Reply("好");
        var sent = await h.Conversations.StartAsync(h.Workspace, new string('史', 20_000), Ct);
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(sent.Answer!.Value)).State);
        var answer = await h.AskAsync(sent.Conversation, "第二问" + new string('补', 6_000));
        Assert.Equal(MessageState.Complete, answer.State);
        var summary = h.Client.Requests.Single(FakeChatClient.IsSummary);
        var input = TokenEstimator.Request(summary.Turns.Select(t => t.Content));
        var output = summary.Model.MaxOutputTokens ?? 0;
        Assert.True(input + output <= ContextBudget.DefaultCapacity,
            $"summary request: input estimate {input} + max_tokens {output} = {input + output} > window … (cut; full text in the workflow journal)

</details>

### ai-client-4. Idle timeout is not reset by SSE comment keep-alives; silent long-thinking requests fail at 120s

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:74`; effort S

**Evidence.** `idle.CancelAfter(options.IdleTimeout)` is armed once per call to ReadEventAsync (line 74). SseReader.ReadEventAsync skips comment lines (`line[0] == ':'`, SseReader.cs:38-41) and non-data fields in an inner loop without returning, so they never re-arm the timer. The existing test Reasoning_keeps_a_long_thinking_stream_alive covers only reasoning_content data events.

**Failure scenario.** A provider or relay that hides reasoning sends only ': keep-alive' / ': PROCESSING' comments (their documented way of holding the connection open) while the model thinks for more than two minutes. The client throws Timeout 'The stream stalled.' although the server is demonstrably alive; the request is billed, the answer fails, and a retry of the same hard question fails the same way. The same applies to compression requests, which are large and reason before writing.

**Recommendation.** Treat any received line (including comments) as liveness: have SseReader surface a heartbeat or accept a callback that re-arms the idle timer. Consider making IdleTimeout configurable per model for non-streaming-reasoning endpoints.

**Skeptic: confirmed, medium.** The mechanism is exactly as claimed, and I found no guard, test or design decision that covers it. Verified by reading code only; nothing was run.

- **Timer arming:** `StreamCoreAsync` calls `idle.CancelAfter(options.IdleTimeout)` once per loop iteration, immediately before `ReadAsync` -> `SseReader.ReadEventAsync(idle)`. The default `IdleTimeout` is 120 s.
- **Comments never return:** inside `ReadEventAsync`, comment lines (`line[0] == ':'`), non-`data` fields and blank lines without pending data all `continue` in the inner `while(true)`. The timer is therefore only re-armed when a complete `data` event is dispatched.
- **Resulting failure:** a stream carrying only `: keep-alive` comments for more than 120 s cancels the idle token, and `ReadAsync` turns that into `ChatClientException(Timeout, "The stream stalled.")` unless a finish_reason was already seen.
- **No override in production:** `ChatClientOptions` is registered as a default-constructed singleton, so 120 s is fixed, with no per-model or user setting.
- **Not an accepted trade-off:** the design doc states the intent that long thinking must not be treated as a stall, but achieves it only through reasoning chunks being stream events. Comment keep-alives are not mentioned in the design docs or TRACK.md.
- **Tests:** the only idle-timeout tests use reasoning data events or a fully stalled stream; none sends comment lines.

Severity stays at medium. The trigger is narrow (a provider or relay that hides reasoning, sends comment keep-alives, and thinks for more than two minutes), but that is a real pattern for OpenAI-co … (cut; full text in the workflow journal)

*Corrections to the finding.* - The SseReader comment skip is at SseReader.cs:36-39, not 38-41.
- "The request is billed" is provider-dependent and not verifiable from the code. Likewise "a retry fails the same way" is likely for the same prompt but not guaranteed, since thinking time varies.
- The compression-request claim was not traced. It is plausible if compression goes through the same `StreamAsync` path, but I did not check that.
- The recommended fix is only partial. Providers that hide reasoning and send nothing at all (no comments) during long thinking would still hit the 120 s limit, so a configurable or per-model idle timeout is the more general remedy.

### ai-client-5. HTTP 200 with a non-SSE body (JSON error or non-streamed completion) is discarded and reported as a generic stream failure

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Infrastructure/Ai/SseReader.cs:43`; effort S

**Evidence.** SseReader lines 43-48 ignore every line whose field name is not `data`. A plain JSON body such as `{"error":{"message":"insufficient balance"}}` parses as a field named `{"error"` and is skipped; at EOF the reader returns null. StreamCoreAsync (ChatCompletionsClient.cs:67-69) never inspects Content-Type. AnswerRunner then reports 'The service closed the stream without finishing the answer.' (line 133).

**Failure scenario.** A relay returns its error (quota, bad model name, upstream failure) as JSON with HTTP 200, or an endpoint ignores stream=true and returns a normal chat.completion object. The user sees an InvalidResponse 'stream closed' message with none of the provider's explanation; a fully generated, billed non-streamed answer is thrown away. The settings connection test reports 'The stream ended without completing.' with no clue why.

**Recommendation.** When the response Content-Type is not text/event-stream (or the first non-blank line does not look like an SSE field), read the body as JSON: surface `error.message` through the normal classification, and accept `choices[0].message.content` plus finish_reason as a completed answer.

**Skeptic: confirmed, medium.** I could not refute it. The code does what the finding says and nothing guards against it.

**Trace of an HTTP 200 body `{"error":{"message":"insufficient balance"}}`:**
1. `ThrowIfFailedAsync` returns immediately for any 2xx, so the body is never inspected as an error.
2. `StreamCoreAsync` wraps the body in `SseReader` unconditionally. Content-Type is never read anywhere in `src/`; the only media-type reference is the request's `Accept: text/event-stream` header.
3. In `SseReader`, the line's first colon makes the field name `{"error"`, which is not `data`, so the line is skipped. Pretty-printed JSON lines are skipped the same way. At EOF the reader returns null.
4. `StreamCoreAsync` sees null and ends without a `Completed` event and without throwing.
5. `AnswerRunner` then marks the message Failed with InvalidResponse and the fixed text "The service closed the stream without finishing the answer."
6. `ModelSettingsService.TestAsync` returns "The stream ended without completing."

A non-streamed `chat.completion` object takes the same path and is silently dropped. The existing error handling in `Parse` (`node["error"]` raising ServerError) only applies to JSON carried inside a `data:` event, so it does not help here.

**Guards and design decisions checked:**
- Tests: every 200-response fixture in `ChatCompletionsClientTests.cs` uses `text/event-stream`. The only `application/json` fixture is for the model list. `A_stream_that_ends_without_finishing_yields_no_completion` covers a truncated SSE stream, not a non-SSE body.
- Docs: grep of `TRACK.md`, `docs/design`, `docs/requi … (cut; full text in the workflow journal)

*Corrections to the finding.* - "A fully generated, billed non-streamed answer is thrown away" is plausible but speculative. Billing depends on the endpoint, and an endpoint that ignores `stream=true` is rarer than a relay returning a JSON error with HTTP 200. The error-body case is the realistic one.
- The line citation is slightly narrow: the skip logic is `SseReader.cs:41-46`, not just line 43.
- The recommendation should classify the surfaced error by its content (quota versus server error). The existing in-stream error path always uses ServerError.

### ai-client-6. A transport error after finish_reason marks a fully received answer as Failed

- Final: **low** (confirmed); auditor said medium
- Where: `src/Mindweave.Application/Conversations/AnswerRunner.cs:140`; effort S

**Evidence.** After finish_reason the client keeps reading for a trailing usage chunk or [DONE] (ChatCompletionsClient.cs:72-108). ReadAsync forgives only an idle timeout after completion (line 220, `afterCompletion ? null : throw`); IOException/HttpRequestException still become ChatClientException(Network) (lines 222-229). In AnswerRunner the `completed` flag is declared inside the try (line 106) and the catch at lines 140-146 sets `final = MessageState.Failed` unconditionally. ContextCompressor.RequestSummaryAsync (188-193) and GenerationRequests.RequestCompleteTextAsync (107-116) behave the same way.

**Failure scenario.** A relay sends the final chunk with finish_reason=stop and then resets the connection instead of terminating the chunked body cleanly ('The response ended prematurely'). The complete answer is stored as Failed with a network error; Message.CountsAsContext excludes it from later context, and the user re-pays for a retry. For compression, a complete summary is discarded and the whole answer fails with CompressionFailed.

**Recommendation.** Once Completed has been observed, treat a later transport error as end of stream (apply the same afterCompletion rule to IOException/HttpRequestException in ReadAsync), or have the callers honour `completed` in the ChatClientException catch.

**Skeptic: confirmed, low.** Could not refute the mechanics; every cited piece of code says what the finding claims, and I found no guard, test or recorded decision covering it.

- ChatCompletionsClient.StreamCoreAsync keeps looping after it yields Completed, waiting for a usage chunk, [DONE] or end of stream. ReadAsync applies `afterCompletion` only to the idle-timeout branch; the IOException and HttpRequestException catches throw ChatClientException(Network) regardless of that flag. HttpIOException("The response ended prematurely") derives from IOException, so it takes that path.
- AnswerRunner declares `completed` inside the try and the ChatClientException catch sets Failed with ex.Kind unconditionally, so a Completed already observed is ignored.
- ContextCompressor.RequestSummaryAsync wraps the ChatClientException into ContextCompressionException before its `completed` check; the two-attempt loop only retries on a returned null summary, not on a thrown exception, so the answer fails on the first such error.
- GenerationRequests.RequestCompleteTextAsync rethrows the same way before its `completed` check.
- Message.CountsAsContext only accepts Complete or Stopped answers, so a Failed answer with full text is excluded from later context.
- No test covers a drop after finish_reason: StreamDropTests cuts the body before the finish chunk, and no design decision or TRACK.md entry mentions transport errors after completion.

Severity is overstated at medium; I put it at low:
- The trigger is narrow. The stream must deliver finish_reason, then fail with a transport error before either [DONE] or a clean end … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity: low rather than medium. The answer text is preserved on the Failed message and Retry is available, and the trigger needs a transport error in the gap between finish_reason and [DONE] or clean end of body.
- "Re-pays for a retry" is accurate but is the whole cost for chat answers; there is no data loss.
- The compression claim is correct and slightly understated in mechanism: the built-in two-attempt summary retry does not apply, because it only covers a returned-unusable summary, not a thrown ChatClientException. One such error fails the answer immediately.
- The finding does not mention that a [DONE] received before the reset fully avoids the problem, which is the main reason the scenario is uncommon.

### ai-client-7. Planner replaces small chunks with larger assumed summaries: extra paid requests, more tokens, less fidelity

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Core/Context/ContextPlanner.cs:87`; effort S

**Evidence.** Lines 87-97: for every chunk in order (closed, then tails) while `total > budget`, it does `total -= chunk cost; total += SummaryTokens(chunk)` with no check that the summary is smaller than the chunk. An ungenerated summary is costed at framing + UnknownSummaryTokens, about 835 estimate tokens (lines 131-132); stored summaries of up to 600 characters cost a similar amount. Tails are, by construction, under 4,000 tokens and are often a single short Q&A in an ancestor frozen at an early branch point.

**Failure scenario.** A branch three levels deep is 300 tokens over budget. The grandparent and parent tails are 200 tokens each; the branch's own tail is 3,000. The loop 'summarizes' the two 200-token tails first (total grows by about 630 each), then the own tail, and finally fits. Result: three summary requests (each with a 16k output reserve and sequential latency before the answer starts) where one sufficed, and two short verbatim exchanges are replaced by longer, lossy summaries. In less lucky orderings the inflation pushes the plan into HistoryTooLong and the question is refused when summarizing only the large chunks would have fit.

**Recommendation.** Skip a chunk when its raw cost is not greater than its (known or assumed) summary cost, and prefer the chunks with the largest saving. Add a planner test with sub-800-token tails in the ancestor chain.

**Skeptic: partially confirmed, low.** The mechanism is real and I could not refute it, but it only bites in a narrower regime than the finding implies, so medium is overstated.

What holds:
- The loop at ContextPlanner.cs:87-97 swaps each chunk's raw cost for its summary cost with no check that the summary is smaller. Nothing upstream filters small chunks: Chunks() (lines 163-191) emits any non-empty tail, however small.
- An ungenerated summary is costed at the framing message plus 800 (lines 131-132), roughly 840 estimate tokens. A 200-token tail therefore adds about 640 to the total when "summarized".
- The claimed arithmetic works: 300 over, two 200-token ancestor tails, one 3,000-token own tail gives three summaries where one would do.
- The false refusal is also reachable. Example: 2,000 over with the same three tails ends about 1,120 over and returns HistoryTooLong; summarizing only the own tail would fit.
- ContextCompressor.PrepareAsync (lines 74-87) makes one sequential paid request per missing summary before the answer request, so the extra requests and latency are real.
- No test covers small tails; ContextPlannerTests has ten tests, none with sub-800-token tails in an ancestor chain.
- The code matches the design: docs/design/s3-context.md:37 specifies "oldest first, closed chunks then tails, re-check after each" with no saving check. This is a design gap, not an implementation slip, and TRACK.md does not list it as a known finding.

What narrows it:
- Closed chunks are always at least 4,000 tokens (lines 173, 186), so they always save about 3,160 or more. Only tails can inflate.
- Tails are reache … (cut; full text in the workflow journal)

*Corrections to the finding.* - The scenario is reachable only after all closed chunks across the chain are already summarized and the plan is still over budget; the finding does not state this precondition. At the default 32k capacity that is uncommon.
- "Three summary requests where one sufficed" is a one-time cost for the ancestor tails: their summaries are stored and reused by key on later questions in the same branch, then counted at real size.
- "16k output reserve" is the max output cap on the summary request (16,384), not tokens consumed.
- Closed chunks are never affected; the defect is confined to tails.
- The behaviour follows docs/design/s3-context.md:37 as written, so this is a design gap to raise via TRACK.md, not a deviation from the design.
- Severity: low rather than medium.

### ai-client-8. Context summaries are sent as system messages in the middle of the conversation

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Conversations/ContextCompressor.cs:93`; effort S

**Evidence.** Line 93: `new ChatTurn(ChatRole.System, ContextPlanner.SummaryMessage(...))` is emitted at the position of the chunk it replaces (ContextPlanner.cs:104-116), so a compressed branch request looks like [system summary][user][assistant][system summary][user]..., with several system turns after user/assistant turns when ancestors' chunks are summarized. docs/design/s3-context.md line 44 states this as the intended design. SummaryService line 97 likewise depends on leading system turns via TakeWhile.

**Failure scenario.** Endpoints with strict chat templates (several local-model servers and some hosted APIs) accept a system message only as the first message or require strict user/assistant alternation; they reject the request with a 400/template error. On such a model every compressed request fails after the summary calls have already been paid for, and since retry rebuilds the same shape the conversation cannot continue. Uncompressed requests on the same model work, which makes the failure look random to the user.

**Recommendation.** Send summaries in a form all chat templates accept: merge them into a single leading system message, or emit them as user-role context notes. At minimum, classify the resulting 400 and fall back to the merged form once.

### ai-client-9. Declared context windows the budget can never use are accepted; small models are refused every question

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Core/Context/ContextBudget.cs:181`; effort S

**Evidence.** ContextBudget.For: reserve = `model.MaxOutputTokens ?? 4096`, margin = max(1000, capacity/10), AvailableTokens = max(0, capacity - reserve - margin) (lines 166, 181). ModelSettingsService accepts windows from MinContextWindow = 2,000 (line 21), and MaxOutputTokens cannot be set in the UI. For SummaryService the reserve is 16,384 (SummaryService.cs:166, 179).

**Failure scenario.** A user declares 4,096 or 5,000 for a small local model: AvailableTokens is 0, so ContextPlanner returns QuestionTooLarge for every question, including 'hi', with a message telling them to shorten their selection. At 8,192 only about 3,096 tokens of input are usable although no max_tokens is actually sent. 'Summarize conversation' is refused as TooLongForModel on any model declared at or below roughly 19k.

**Recommendation.** Validate at save time that the window leaves usable input (or scale the default reserve to a fraction of the window when max output is unset), and say so in the settings field. Distinguish 'model too small' from 'question too large' in the rejection.

### ai-client-10. Error classification is coarse and partly misleading

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:311`; effort S

**Evidence.** LooksLikeContextLimit (lines 311-314) matches any 400/413 whose text contains 'context', 'too long' or 'maximum'. In-stream `error` events are always ServerError (line 246) whatever their type/code. 404 (unknown model or wrong path), 408 and 422 fall to InvalidResponse (line 297). Retry-After on 429 is not read. Separately, GenerationCoordinator._live (lines 27, 197) is never pruned, so every answer's full text stays in memory for the session.

**Failure scenario.** A 400 such as 'max_tokens exceeds the maximum allowed value' or 'maximum number of messages' is shown as 'content exceeds the model's context length', sending the user to shorten history when the cause is a parameter. A rate-limit or context-length error delivered inside the stream is shown as a server fault. A wrong model name (404) reads as an invalid response rather than a configuration problem.

**Recommendation.** Prefer the provider's structured `error.code`/`error.type` (context_length_exceeded, rate_limit_exceeded, model_not_found, insufficient_quota) for both HTTP and in-stream errors, and keep keyword matching only as a fallback with tighter phrases. Add a NotFound/configuration kind for 404. Remove terminal LiveAnswers from _live once their terminal write lands.

