# Background paid AI requests (titles, naming, workspace-name preparation, compression) and the correctness of usage statistics and pricing — grade B

Background spending is deliberately bounded and disclosed, and the statistics maths (local days across DST, scope joins, decimal money, unreported requests kept apart) is correct and tested with hand-derived expectations. The grade is held down by one structural gap: usage is never requested from the service (D-013), so for OpenAI-semantics endpoints the statistics page and token calibration have no data at all. Outputs the program uses directly are also not robust to inline reasoning text, and two background request types (summary naming, title) have unbounded input size. No runaway retry loop, startup burst or post-delete request was found. Nothing here was run; all conclusions are from reading the code.

**Strengths**

- Background workspace-name preparation is well bounded (WorkspaceNamePreparation.cs:211-238, 339-503): fires only on a TitleSet event (nothing at startup), needs 3 titled conversations then growth of max(3, 15%) capped at 20, waits 30 s after the last title, runs one request app-wide, and re-checks setting, exit, storage health, workspace existence and due-ness when the timer fires.
- Idle priority is real: GenerationCoordinator.Pump (lines 490-504) starts idle work only when no user/background work waits and preempts it when both slots are full; the preempted request is still recorded as 'stopped'.
- Background name preparation has an off switch with cost and data disclosure (UiText.Settings.cs:55-56, NewsAlerts.cs:45-49), and the in-memory choice applies immediately even if the write fails.
- Deletion races are handled: WorkspaceNameCache keeps a _forgotten set so a request answering after its workspace was deleted cannot re-create the key (lines 44-46, 80-94); titles and naming are cancelled before archive/delete (CancelBackgroundAsync); SetAutoTitleAsync refuses hidden or manually titled conversations in SQL.
- Titles are asked at most once per conversation per session (_titleRequested, ConversationTitler.cs:89) and never retried on failure; model output for workspace names is only offered, never applied, and is validated (<=20 text elements, WorkspaceName.TryCreate strips control characters, deduplicated against the current name).
- Statistics SQL is sound: local day computed as (ts + offset)/86400000 with a CASE over the zone's offset changes found by binary search (LocalCalendar.Offsets); joins are on primary keys so no double counting; workspace scope covers both task-bound and task-less requests (SqliteStatisticsStore.cs:141-150).
- Money is decimal end to end (UsagePrices.cs:10-15), parsed with the invariant culture, range-checked, and unpriced models are named in the total instead of silently counted as zero (UsageStatisticsViewModel.cs:447-453).
- Requests without usage are counted apart and never filled with the app's own estimate, and the page says so; the cost caption states it excludes cache and discount rules; the deleted-content caveat is on the page (UiText.Statistics.cs:56, 145, 238).
- UsageStatisticsTests use a custom Berlin zone independent of the machine's tz database, seed rows at explicit UTC instants, and assert hand-computed buckets across the spring and autumn changes and window edges; expected values are not derived from the code under test.

**Not examined**

- WorkspaceNewsViewModel.cs, WorkspaceToastViewModel.cs and UsageChart.cs were not read; they appear to send no AI requests, but that was not verified.
- WorkspacesViewModel.cs was read only around the suggestion flow (lines 215-345); row view-model ghost-text and Tab cycling were not traced. The view model sends one request per SuggestNamesAsync call.
- AnswerRunner.cs was only grepped for trace recording; the answer path's usage capture was not read line by line.
- ContextPlanner trigger thresholds (when compression fires) were not examined; that area is already reported elsewhere. Only ContextCompressor's request and retry loop was read.
- UsagePricingTests, LocalCalendarTests, UsageStatisticsUiTests and WorkspaceNamePreparationUiTests were not read; only the first ~110 lines and the test names of UsageStatisticsTests were.
- No test or build was run; the baseline scratchpad outputs were not consulted.
- Real provider behaviour for stream usage (which services return it without stream_options) is from general knowledge, not from a live check.
- Behaviour of the preparer and its timers across an in-app restore or session swap (already reported elsewhere under debounce timers).
- Performance of the statistics queries (no index on ai_request.started_at; about 12 scans per load) was not measured.

### gap-unprompted-ai-spend-and-statistics-accuracy-1. Usage is never requested from the service, so statistics and token calibration are empty for OpenAI-semantics endpoints

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:256`; effort M

**Evidence.** The request body (lines 160-175) sets model, stream=true and max_tokens only. The comment at line 256 says usage 'is never requested'. docs/design/decisions.md D-013 (2026-10-27) made this choice when usage was only for diagnostics. The statistics page (2026-11-12) sums only rows where prompt_tokens or completion_tokens is non-null (SqliteStatisticsStore.cs:135), and RecentTokenSamplesAsync requires prompt_tokens (SqliteAiRequestStore.cs:111). Real-service tests covered GLM, Kimi, DeepSeek and a relay, which volunteer usage; the official OpenAI API was not tested directly.

**Failure scenario.** A user configures api.openai.com, Azure OpenAI, or a local vLLM/LiteLLM/Ollama server. These send a streaming usage chunk only when stream_options.include_usage is true. Every request is stored with NULL tokens. The statistics page shows 0 input, 0 output, 0 requests and 'N requests have no usage record', cost stays at 0 whatever price is typed, and per-model token calibration never learns, so context budgeting runs on the raw estimate.

**Recommendation.** Send stream_options {include_usage:true} by default and fall back once, remembered per connection, when the service rejects it with a 400 naming the field; or make it a per-connection capability probed by 'Test connection'. Revisit D-013 now that a user-facing feature depends on usage. Until then, tell the user on the statistics page when a model has never reported usage.

**Skeptic: partially confirmed, medium.** The code facts are all accurate and I found no guard that prevents the scenario. The request body in SendAsync contains only model, stream, messages and optional max_tokens; there is no stream_options anywhere in src, and a unit test asserts its absence (so this is deliberate, per D-013). Usage is parsed only when the service volunteers it. The statistics store sums tokens and counts "Requests" only for rows where prompt_tokens or completion_tokens is non-null, and token calibration samples require prompt_tokens IS NOT NULL. D-013's stated rationale ("for cross-checking estimates and diagnostics") predates both calibration (2026-11-05) and the statistics page (2026-11-12) and was not revisited.

Why I lower the severity from high to medium:
1. The degradation is honest, not silent or wrong. Requests without usage are counted separately and shown on the page ("另有 N 次请求没有用量记录（服务未返回，或请求没有送达），未计入"), per bar, per model row and per table cell. Nothing is mis-stated; the design (s7-ui-polish.md:687) explicitly chose not to substitute estimates. Cost shows 0 next to an explicit "N requests returned no usage" note. The recommendation's last clause ("tell the user on the statistics page") is therefore already largely implemented at the request level, though not as a per-model "never reported" hint.
2. The gap is already known and recorded by the team, not a new discovery: TRACK.md finding of 2026-11-05 and stage-reports.md:351 both state that official OpenAI needs stream_options to return usage, that it is not requested and was not tested. However it is NOT listed under "待决定" for the statistics feature (only deleted-content usage and chart contrast are), so the consequence for the statistics page was never put to the user as a decision.
3. Calibration impact is bounded: calibration only scales the estimate up, never down, and TRACK's own measurements show the raw estimator runs high (safe) for OpenAI models. Without samples the budget falls back to the raw estimate plus the existing margin; that is the pre-2026-11-05 behaviour, not a new failure. It would matter for a local/self-hosted model whose tokenizer the estimator under-counts.
4. No data loss, no incorrect billing, no crash; a secondary feature is empty-but-labelled for a class of endpoints.

Why it stays at medium rather than low: api.openai.com is the reference implementation of the protocol this client targets, and for it (and Azure OpenAI, vLLM, LiteLLM and similar) the user-facing statistics feature shipped on 2026-11-12 is functionally empty and price entry is useless. That is a real feature gap against an industrial quality bar, and the fix is cheap and well understood.

Not independently verified by me (no network calls allowed): the behaviour of the real OpenAI/Azure/vLLM/Ollama endpoints. That official OpenAI omits streaming usage unless stream_options.include_usage is set matches the project's own notes and my knowledge; the claim about Ollama is less certain (its OpenAI-compatible endpoint has varied by version), so the list of affected servers should be read as "likely" rather than established.

*Corrections to the finding.* 1. Severity high is overstated; medium fits. The page does not silently show wrong numbers: it shows zero reported tokens together with an explicit count of requests that returned no usage, by design.
2. The finding presents this as undiscovered. It is a known, documented limitation (TRACK.md:101, stage-reports.md:351, D-013). What is new is that the statistics page now depends on it and the decision was not revisited or raised under pending decisions.
3. "context budgeting runs on the raw estimate" is true but its impact is overstated for OpenAI: the project's own measurements show the estimator runs high (safe) for OpenAI models, and calibration only ever enlarges. The risk is confined to endpoints whose models the estimator under-counts.
4. The recommendation's "tell the user on the statistics page" is already implemented per request (unreported counts at every level); only a per-model "this model has never reported usage, here is why" hint is missing.
5. File paths: the two stores are in src/Mindweave.Infrastructure/Sqlite/, not Persistence/. Line numbers are correct.
6. The list of affected servers (Azure, vLLM, LiteLLM, Ollama) is unverified here; Ollama in particular may volunteer usage depending on version.

**Reproduction attempt: confirmed, medium.** The code defect is real and reproduced. ChatCompletionsClient.SendAsync builds the body with only model, stream=true, messages and optional max_tokens; stream_options is never sent. A new test with a fake service that follows official OpenAI semantics (usage chunk streamed only when stream_options.include_usage is true) receives the text but no Usage event, so the test fails at Assert.Single.

Downstream consequences follow by code reading (not separately tested):
- Statistics count a request as reported only if prompt_tokens or completion_tokens is non-null; otherwise it goes into the "unreported" count and contributes 0 tokens, hence 0 cost.
- RecentTokenSamplesAsync requires prompt_tokens IS NOT NULL, so TokenCalibration.Fit gets no samples and ContextBudgets falls back to the plain estimate.

Why I lower severity to medium:
- It is a deliberate, documented trade-off, not an oversight. D-013 chose not to send the field because some compatible services reject unknown fields. TRACK.md's 2026-11-05 finding and stage-reports.md:351 both state that official OpenAI needs stream_options and was not tested. An existing test (Reported_usage_is_passed_on_and_never_requested) pins the behaviour.
- The failure is visible, not silent. The statistics page already says "N 次请求未返回用量" / "另有 N 次请求没有用量记录（服务未返回，或请求没有送达），未计入". No wrong numbers are shown and chat itself works.
- The calibration fallback is the pre-calibration behaviour. TRACK records the raw estimator as over-estimating (the safe direction) for OpenAI models, so the budgeting risk on api.openai.com is small; it is larger for unknown local models.

What remains valid: D-013 predates the statistics page, which now makes usage a user-facing feature. For official OpenAI, Azure and most local servers the token and cost parts of that page are unusable, and the user is not told why or how to fix it. The recommendation (send include_usage with a remembered fallback on 400, or probe it in "Test connection") is sound, and the decision should be revisited.

Method note: my worktree was cut from main, which has no product code, so I checked out track/claude-auto detached at 1ba95c9 inside my own worktree, ran the test, then restored the original branch and cleaned; git status is clean.

*Corrections to the finding.* - "Statistics ... are empty" and the implication of a hidden failure are overstated. The page explicitly reports "N 次请求未返回用量" and gives the cause as "服务未返回，或请求没有送达", so the recommendation's last point (tell the user) is already partly implemented. What is missing is a per-model hint that usage was never reported and any way to fix it.
- This is not a newly discovered gap. It is recorded in D-013, TRACK.md:101 ("OpenAI 官方需请求 stream_options 才返回…官方未测") and stage-reports.md:351, and an existing test pins it. The new point is only that the statistics page (2026-11-12) raises the stakes of the old decision.
- "Context budgeting runs on the raw estimate" is true but is the designed fallback. TRACK records the raw estimate as over-estimating (safe) for OpenAI models, so the impact on api.openai.com is limited.
- File paths are wrong: the stores are in src/Mindweave.Infrastructure/Sqlite/, not Persistence/. The request body is at lines 158-175.
- The claim that OpenAI, Azure, vLLM, LiteLLM and Ollama omit streaming usage unless include_usage is set was not tested against any real service (no paid calls allowed). It matches documented OpenAI behaviour and the project's own notes; LiteLLM and vLLM can be configured server-side to always include it.

<details><summary>Reproduction</summary>

Added temporarily to tests/Mindweave.Infrastructure.Tests/Chat/ChatCompletionsClientTests.cs (removed afterwards):

    [Fact]
    public async Task Repro_openai_semantics_service_yields_no_usage()
    {
        // A service with official OpenAI semantics: the usage chunk is streamed only when stream_options.include_usage is true.
        StubHandler? handler = null;
        handler = new StubHandler(_ =>
        {
            var body = JsonNode.Parse(handler!.Requests[^1].Body)!;
            var wants = body["stream_options"]?["include_usage"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":\"stop\"}]}\n\n"
                + (wants ? "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":7,\"total_tokens\":127}}\n\n" : string.Empty)
                + "data: [DONE]\n\n";
            return Sse(sse);
        });

        var events = await Collect(new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions()));

        Assert.Equal("a", Assert.Single(events.OfType<ChatStreamEvent.TextDelta>()).Text);
        Assert.Equal(new ChatStreamEvent.Usage(120, 7), Assert.Single(events.OfType<ChatStreamEvent.Usage>()));
    }

Command: dotnet test tests/Mindweave.Infrastructure.Tests --filter "FullyQualifiedName~Repro_openai_semantics_service_yields_no_usage"

Output:
  [xUnit.net 00:00:00.77]     Mindweave.Infrastructure.Tests.Chat.ChatCompletionsClientTests.Repro_openai_semantics_service_yields_no_usage [FAIL]
  Failed Mindweave.Infrastructure.Tests.Chat.ChatCompletionsClientTests.Repro_openai_semantics_service_yields_no_usage [23 ms]
  Error Message:
   Assert.Single() Failure: The collection was empty
  Stack Trace:
     at ...ChatCompletionsClientTests.Repro_openai_semantics_service_yields_no_usage() in .../ChatCompletionsClientTests.cs:line 97
  Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 31 ms - Mindweave.Infrastructure.Tests.dll (net10.0)

The failure is on the Usage assertion (line 97); the text-delta assertion on the line before passed. The test is deterministic, with no timing involved. The statistics and calibration consequences were verified by reading the SQL, not by a test.

</details>

### gap-unprompted-ai-spend-and-statistics-accuracy-2. Inline reasoning text (<think>...</think>) is not stripped from outputs the program uses directly

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Core/Conversations/ConversationRules.cs:74`; effort S

**Evidence.** CleanGeneratedTitle takes the first non-empty line of the raw output. TaskRunners.RunTitleAsync (lines 47-51) stores it via SetAutoTitleAsync. WorkspaceNamePrompt.ParseCandidates (WorkspaceNameSuggestions.cs:272-296) accepts any line of at most 20 text elements. ContextCompressor.RequestSummaryAsync (line 195) stores text.ToString().Trim() as the context summary. A grep for 'think>' in src/ finds nothing; only the separate reasoning_content field is handled (ChatCompletionsClient.cs:253).

**Failure scenario.** A model that puts its reasoning inside content, such as deepseek-r1 or qwen via Ollama or some relays, answers a title request with '<think>\nThe user asks about...\n</think>\n蒸汽机的发明'. The conversation is titled '<think>' and never re-titled, because the title is now 'auto' and the request counted as completed. Workspace-name candidates include '<think>' and short reasoning lines. Context summaries are stored with the whole reasoning block and re-sent on every later request of that conversation.

**Recommendation.** Strip a leading <think>...</think> (and unterminated <think>) block in one shared place before any 'complete text' output is used (GenerationRequests.RequestCompleteTextAsync and ContextCompressor). Add tests for titles, names, naming terms and context summaries.

**Skeptic: confirmed, medium.** I could not refute it. Every cited code path exists and behaves as claimed, and no guard, test or recorded decision covers inline reasoning.

- **Title:** `CleanGeneratedTitle` takes the first non-empty line. For `<think>\n...\n</think>\n蒸汽机的发明` that line is `<think>`. Nothing removes it: the title prefixes do not match, the trailing-punctuation list has no angle brackets, and `EnclosingMarks` has no `<`/`>` pair. `RunTitleAsync` stores it with `SetAutoTitleAsync` and marks the task Completed.
- **Not re-titled automatically:** `ConversationTitler` only requests a title when `TitleSource` is `None`, and I found no regenerate-title path in `src/` (the "重新生成" strings are for answers and summaries). The user can still rename manually.
- **Workspace names:** `ParseCandidates` skips only empty lines, lines ending in a colon, and preamble matches. `<think>`, `</think>` and any reasoning line of 20 text elements or fewer pass as candidates. Since `MaxCandidates` is 3, they can crowd out the real names. This path is also prepared in the background and shown as ghost text.
- **Context summaries:** `ContextCompressor.RequestSummaryAsync` returns the trimmed raw text with no filtering, so the reasoning block is stored and re-sent.
- **Shared entry point:** `RequestCompleteTextAsync` also returns the trimmed raw text, so knowledge summaries and naming terms are affected too.
- **Only the separate field is handled:** the client reads `delta.content` and counts `reasoning_content` length. The design doc (`s1-chat.md` line 70) says background requests "只取正文，忽略推理", which assumes reasoning arrives in the separate field.
- **No coverage:** grep for `think>` in `tests/` and `docs/` returns nothing. TRACK findings and D-015 address only output caps and truncation.

Medium holds because the scenario is in scope, not exotic. The settings page ships presets for Ollama (local), OpenRouter and SiliconFlow (`s7-ui-polish.md` line 210), which are where inline `<think>` output is most likely. The design docs say only DeepSeek, Kimi, GLM and Pinaic relays were actually tested, and all of those use `reasoning_content`. The damage is silent and persistent (a stored `<think>` title, junk name candidates, bloated context summaries re-sent at cost), but there is no data loss and the title is fixable by manual rename.

*Corrections to the finding.* - "Never re-titled" should read "never re-titled automatically": the user can rename the conversation manually. I found no regenerate-title command in `src/`.
- The scope is slightly understated. Knowledge summaries (`TaskRunners.cs:101`) and naming terms (`:175`) go through the same `RequestCompleteTextAsync` and are equally affected.
- The provider claim is untested here. Recent Ollama versions may return reasoning in a separate field on the OpenAI-compatible endpoint, so exposure depends on provider, version and model. None of the services the project actually tested emit inline `<think>`.
- Chat answers from such a model would also show the raw `<think>` block. That is the same root cause but outside this finding.
- A fix that strips a leading `<think>` block must treat an unterminated block as unusable output (empty), not fall through to the reasoning text.

### gap-unprompted-ai-spend-and-statistics-accuracy-3. Same-name summary naming re-sends the whole group on every saved summary, with no cap on group size

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Summaries/SummaryNaming.cs:134`; effort M

**Evidence.** ScheduleNamingAsync collects every summary in the workspace with the same normalised base title (lines 134-136) and builds a prompt with a 600-character excerpt per member (lines 156-160, NamingBodyExcerpt=600) with max_tokens 4096; there is no limit on members. SummaryService.cs:378 calls it after every non-candidate summary save, and CheckNameAsync after edits and version changes. There is no check that the group is already disambiguated; the prompt itself asks the model to keep existing terms that are still accurate.

**Failure scenario.** A workspace has 25 summaries whose base title is '总结'. Each new or regenerated summary in that group fires a background request of about 25 x 650 characters plus up to 4,096 output tokens, so total spend grows quadratically with group size. The user never asked for these and there is no switch. With a small-window model the request eventually exceeds the context and fails on every save.

**Recommendation.** Skip the request when every other member already has an automatic or manual distinguishing term and only name the new member against the existing names. Cap members and total characters per request. Budget-check the request against the model window before sending.

**Skeptic: partially confirmed, low.** The core claim is true, but the trigger frequency and the "by mistake" framing are overstated, so medium drops to low.

**What holds:**
- `ScheduleNamingAsync` takes every summary in the workspace with the same normalised base title, with no cap on member count or total prompt size.
- Each member contributes its display name, title and a 600-code-point excerpt; output is capped at 4,096 tokens.
- The only skip conditions are: fewer than 2 members, every member manually named, storage blocked, no active model, or the same group already queued or running.
- There is no check that the group is already disambiguated (for example, all other members already `auto` or `manual`), and no budget check against the model window before sending. `RunNamingAsync` sends the request as built.
- So each newcomer to a group of size n sends a request of about n x 650 characters, and cumulative spend over the growth of a group is quadratic.
- There is no user switch for naming.

**What is wrong or overstated:**
1. **"Every saved summary" is not accurate.** Naming at save time fires only for non-candidate saves, which means the first summary of a conversation (a new node). A regenerated summary of an existing node is saved as a candidate and triggers nothing. Adopting, restoring, editing or deleting versions goes through `CheckNameAsync`, which schedules naming only when the current title's normalised form differs from the stored base title. Regenerating or editing without a title change costs nothing. The request fires once per node joining a group, or per title change into one.
2. **Sending the whole group is the specified behaviour, not an oversight.** Requirements say the whole same-name group is renamed from content, keeping still-valid terms (`docs/requirements.md:194`). Architecture specifies merged, deduplicated group tasks, no automatic retry, and priority below user questions (`docs/architecture.md:353`). `docs/design/decisions.md:75` fixes the per-node payload as title, first 600 characters and existing name. The recommendation to name only the newcomer would change a documented design, because a newcomer can make an existing term stop distinguishing.
3. **The 25-member scenario is unlikely in practice.** A group requires an exact match after NFKC, case and punctuation normalisation of model-generated content titles within one workspace. Groups of 2 to 4 are realistic; 25 identical titles would need a model that emits a generic title every time.
4. **The cost at that size is small.** At 25 members the input is roughly 16k characters, about 10 to 16k tokens. Output is in practice about 25 short lines; 4,096 is a ceiling chosen for reasoning models (TRACK.md finding of 2026-10-26).
5. **A context overflow fails soft.** The task is marked failed, existing names are kept, and nothing is retried. It does still recur on the next group change.
6. **The spend is visible.** Naming usage appears in the statistics page under its own purpose.

**What remains as a real gap:** an unbounded, un-budgeted background request, where sibling features (compression, workspace-name preparation) have thresholds or budgets. A member and character cap, or a window check, is a reasonable hardening item. It is a robustness gap rather than a medium-severity cost defect.

*Corrections to the finding.* - "Calls it after every non-candidate summary save, and CheckNameAsync after edits and version changes" is literally true but misleading. Regenerations are candidates and trigger nothing. `CheckNameAsync` sends a request only when the title actually changed relative to the base title.
- "Each new or regenerated summary in that group fires a background request" is wrong for regenerated summaries. Only a new node joining the group, or a title change into the group, fires one.
- Re-sending the whole group with existing names is the documented design (requirements §10.2 line 194, architecture line 353, decisions.md line 75), not an oversight. The recommendation to name only the new member conflicts with that design and would need a user decision.
- "Fails on every save" should read: fails softly, with names kept and no retry, on each subsequent group change.
- The 25-identical-title scenario is improbable given exact-match normalisation of model-generated content titles. At that size the cost is about 10 to 16k input tokens per group change.
- The valid residue is the missing member/character cap and the missing pre-send window check. Severity is low.

### gap-unprompted-ai-spend-and-statistics-accuracy-4. The automatic title request sends the entire first question, unbounded, and pairs it with whichever answer just completed

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Conversations/ConversationTitler.cs:103`; effort S

**Evidence.** Line 101 takes the first user message; line 103 uses question.Text in full (plus the full branch quote via BranchContext.QuotedQuestion); line 130 sends '提问：{asked}\n\n回答开头：{excerpt}'. Only the answer is cut, to 2,000 characters (line 104). There is no context-budget check. e.Text is the answer that just completed, not necessarily the answer to the first question.

**Failure scenario.** The user pastes a 60k-token document as the first question. After the answer, a background title request re-sends all 60k tokens, roughly doubling the input cost of that turn for a 20-character title. With a model near its window the title request fails with a context-length error. Separately, if the first answer failed and the second question's answer completes, the title prompt pairs question 1 with answer 2.

**Recommendation.** Cut the question and the quote to a few hundred characters (head, and tail if needed) before building the prompt. Use the first question's own answer, or the question that the completed answer belongs to.

**Skeptic: partially confirmed, low.** The cited code exists and behaves as described, but two of the three claimed consequences are overstated and the third is minor.

**What holds**
- **Full first question is re-sent.** `ConversationTitler.cs:101-103` takes the first user message and uses `question.Text` whole (with the branch quote for branches); only the answer is cut to 2,000 characters (`:104`). There is no budget check and no truncation before `:130`.
- **Question 1 can be paired with a later answer.** `e.Text` is whichever answer just completed. `OnAnswerFinished` returns early for non-complete answers (`ConversationService.cs:621-624`), and `CheckReadyAsync` only blocks a send while an answer is unfinished, so after a failed or stopped first answer the user can ask question 2 and its answer triggers the title. The same pairing happens by design after an exit-abandoned title (`ConversationTitler.cs:55-56`), after archive/restore (`:60-62`), and when no model was configured at first completion.

**What is overstated**
- **"Unbounded" is wrong.** Questions are capped at 100,000 characters (`ConversationService.cs:76`, `:546`) and quotes at 4,000 (`BranchSource.cs:46`). The extra cost is real but bounded and happens once per conversation.
- **The behaviour is documented design, not an oversight in code.** `docs/design/s1-chat.md:83` specifies the title input as "首问＋回答前 2000 字", and `decisions.md:39` deliberately includes the quote. The design did not weigh cost for a very long first question, so the recommendation to cap it is still valid as a cost improvement.
- **The context-length failure is nearly unreachable.** The answer request already had to fit the question within Capacity − OutputReserve − Margin, where OutputReserve defaults to 4,096 and Margin is at least 1,000 or 10% of capacity (`ContextBudget.cs:150-182`). The title request adds only about 60 tokens of prompt, at most 2,000 characters of excerpt and 1,024 output tokens, which is normally less than that headroom. For a branch, the answer request also carried ancestors, so the title request is smaller still. Overflow needs a user-set small `MaxOutputTokens`, a question at the limit and a dense CJK answer; even then the title fails silently and the fallback title stays.
- **The mismatch has small impact.** The prompt asks for a title summarising the question's topic (`:28`), the answer excerpt is secondary context, and the user can rename.

Net: a genuine cost inefficiency (the first turn's input is roughly doubled for a 20-character title when the first question is very long) plus a minor prompt mismatch. No data loss, no user-visible failure. Low severity rather than medium.

*Corrections to the finding.* - "Unbounded" is incorrect: the question is capped at 100,000 characters and the quote at 4,000.
- The context-length failure claim is essentially refuted. The answer request reserves at least 4,096 output tokens plus a margin of at least 1,000, which exceeds the title request's overhead except with a small user-configured `MaxOutputTokens`; a failure there is silent and leaves the fallback title.
- Sending the whole first question is the documented design (`docs/design/s1-chat.md:83`), so this is a design-level cost gap rather than a coding defect.
- The question-1-with-answer-2 pairing is real but also arises from intentional re-ask paths (exit-abandoned title, archive/restore), and its effect on a title that summarises the question is minor.
- Severity should be low, not medium.

### gap-unprompted-ai-spend-and-statistics-accuracy-5. Background name preparation has no memory of failure: each new title re-sends a request that keeps failing

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Workspaces/WorkspaceNamePreparation.cs:496`; effort S

**Evidence.** On NameSuggestionProblem.Failed, or a successful answer with no usable names, PrepareAsync only logs at Information (lines 496-501). WorkspaceNameSuggester stores to the cache only when names.Count > 0 (WorkspaceNameSuggestions.cs:145-148). The cache is unchanged, so NameRefreshRule.IsDue stays true and ConsiderAsync re-arms the 30 s timer on every later TitleSet, including manual renames (lines 363-370).

**Failure scenario.** A reasoning model spends the 1,024-token cap thinking and returns truncated or empty output, or a chatty model's lines all exceed 20 characters. From the third titled conversation on, every new title in that workspace triggers another billed request that is discarded, indefinitely, visible only in the log and as growing '工作区名称' usage on the statistics page. The same holds after a revoked key (401 on every title, but free).

**Recommendation.** Record the attempt (time and titled count) in the cache even on failure or empty result, so the growth threshold applies to failed attempts too. Stop for the session after an auth or not-configured error.

### gap-unprompted-ai-spend-and-statistics-accuracy-6. Cost and token totals systematically omit stopped and mid-stream-failed requests although an estimate is stored

- Final: **low** (not verified); auditor said low; already known in TRACK.md
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteStatisticsStore.cs:135`; effort S

**Evidence.** Only rows with a reported count enter the sums (lines 30-33, 135). Usage arrives on the last stream chunk, so a request stopped by the user, by idle preemption, or by a transport error is recorded with NULL tokens (RequestTrace.Finish). estimated_tokens is stored on every row but deliberately not used (docs/design/s7-ui-polish.md:687). The page states the count of such requests.

**Failure scenario.** A user who often stops long answers has those requests billed by the provider for the full prompt plus generated text, but the page's token totals and '约 ¥' figure exclude them. The estimate reads lower than the bill with no indication of magnitude, only 'another N requests'.

**Recommendation.** Keep reported and unreported apart as now, but show a labelled lower bound for the unreported ones (estimated_tokens for input, output_chars-based for output), or at least say the cost estimate excludes them next to the cost figure.

### gap-unprompted-ai-spend-and-statistics-accuracy-7. Prices are a single current value per model@host: edits re-price all history, and one symbol covers all models

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Statistics/UsagePrices.cs:137`; effort M

**Evidence.** The price is stored under 'usage.price:{model}@{host}' with no effective date (lines 134-138, 174). UsageStatisticsViewModel.UpdateTotalCost (line 452) multiplies the whole period's tokens by the current price and sums across models under one currency symbol. ModelKey.HostOf (UsageStatistics.cs:45-51) drops scheme and path.

**Failure scenario.** After a provider price cut the user updates the price, and the 12-month view silently re-prices earlier months at the new rate. A user with one model billed in $ and another in ¥ gets one total under one symbol. Two deployments on the same host with different paths and the same model name share one usage row and one price. The caption says 'estimated from your price' but none of these three limits.

**Recommendation.** State in the caption that the current price applies to the whole period. Either store prices with an effective-from date or accept and document the limit. Consider per-model currency or refuse to total across symbols.

### gap-unprompted-ai-spend-and-statistics-accuracy-8. Workspace-name requests send more than the setting discloses, including archived conversations' titles and question text

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Workspaces/WorkspaceNameSuggestions.cs:205`; effort S

**Evidence.** SelectInput orders hidden (archived) conversations last but still includes them (lines 205-209), substitutes the first 30 characters of the first question for an untitled conversation (line 208), and adds up to 12 summary names (lines 210-213). NameRefreshRule.TitledConversations excludes hidden ones, so the two rules disagree. The setting text says only that titles are sent (UiText.Settings.cs:56).

**Failure scenario.** A user archives a conversation with a sensitive title to put it away. In a workspace with fewer than 20 visible conversations, its title, or the opening of its first question, is still sent in the next background naming request.

**Recommendation.** Exclude hidden conversations from SelectInput, matching the due rule. Reword the hint to mention question openings and summary names.

### gap-unprompted-ai-spend-and-statistics-accuracy-9. Generated conversation titles and naming terms are stored without removing control or bidi-format characters

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Core/Conversations/ConversationRules.cs:97`; effort S

**Evidence.** Shorten collapses only char.IsWhiteSpace (lines 102-117); CleanGeneratedTitle adds no further filtering before SetAutoTitleAsync writes it unattended. SummaryNaming.ParseNaming (line 117-118) checks only a length of 1 to 16 after EnclosingMarks.Strip. WorkspaceName.Normalize does strip char.IsControl, but workspace names are user-confirmed anyway.

**Failure scenario.** A first question pasted from an untrusted document steers the title model to emit U+202E or zero-width characters (or the model emits a stray control character). The title is saved automatically and renders reversed or invisible in the sidebar, toast cards and the workspace-name prompt. Impact is cosmetic or spoofing only, bounded to 60 characters.

**Recommendation.** Drop Unicode categories Cc and Cf (except ZWJ/ZWNJ where needed) in one shared 'clean generated name' step used by titles, naming terms and workspace-name candidates.

### gap-unprompted-ai-spend-and-statistics-accuracy-10. Statistics use the process's cached local time zone; a zone change while the app runs is not picked up

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Statistics/UsageStatistics.cs:148`; effort S

**Evidence.** LoadAsync uses 'options.Zone ?? clock.LocalTimeZone'. TimeProvider.System.LocalTimeZone is TimeZoneInfo.Local, which .NET caches for the process; a grep finds no ClearCachedData call in src/. All history is bucketed in the one current zone by design.

**Failure scenario.** A laptop user flies from Beijing to Berlin with the app left running. 'Today', the 30-day window and each request's day are still computed in UTC+8 until restart, so evening requests land on tomorrow's bar and the active-day count can differ from the wall calendar. After a restart all past days are re-bucketed in the new zone and bars shift.

**Recommendation.** Call TimeZoneInfo.ClearCachedData() before each statistics load, or on the OS time-zone change notification. Mention in the caption that days follow the machine's current zone.

