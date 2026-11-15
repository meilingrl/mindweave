# Test suite quality — grade B

Upper B (B+). The suite is strong by production standards: 837 tests (49 Core, 356 Infrastructure, 424 Desktop, 8 Architecture), run against the app's own DI wiring, real SQLite and real child-process kills. I found no vacuous or tautological tests; a scripted scan of all 837 for assertion counts turned up only helper-asserted or wait-asserted cases. The 76 skips are all deliberate and documented: 55 screenshot-gallery scenes, 5 opt-in performance budgets, real-API tests, 1 credential-store test, and platform-specific file-lock tests.

It is not an A because of a mutation-style check on 12 invariants: 4 survive outright and 2 are only partly killed, clustered at the AI-stream boundary (finding 1, timeouts, usage ordering, empty output). Other gaps:
- The shared fake AI client cannot express several real protocol shapes.
- The only code that stores API keys (about 350 lines of P/Invoke) is never run automatically.
- About 20 desktop assertions are "wait 200–300 ms of wall clock, then assert nothing happened".
- There is no coverage measurement and no per-test timeout.
- CI does not run macOS by default, although it is a shipping platform.

The partial baseline at /private/tmp/claude-501/-Users-meilingluo-Developer-Projects-mindweave-worktrees-claude-auto/741bda69-6233-404f-b4a0-8bc89d0ed5bc/scratchpad/test.txt shows only the Desktop project: 381 passed, 58 skipped, 0 failed, in 1 m 56 s under concurrent load.

**Strengths**

- Tests run the production wiring, not a parallel one: ChatHarness.BuildServices (tests/Mindweave.Infrastructure.Tests/Chat/ChatHarness.cs:315-365) calls AddMindweaveInfrastructure and AddMindweaveApplication with ValidateOnBuild, and DesktopHarness goes through CompositionRoot.Build. Only the AI client, credential store, theme, sound and clocks are replaced.
- Real fault injection: TransactionKillTests kills a child process before COMMIT through a writer hook for 9 multi-table operations, then compares every row of every table. There are also disk-full, busy-writer, stream-drop, restore-failure and backup-kill tests, plus a stress workflow (.github/workflows/stress.yml) to repeat them on hosted runners.
- Migration tests are substantive: seeded older-version data for v3, v4, v5, v6, v8, v9 and v10, upgrade-backup checks, rollback of a failing step, foreign-key-check rollback, newer-schema refusal and foreign-file refusal (SqliteDatabaseTests.cs:68-180, SchemaV10MigrationTests.cs).
- Most critical invariants I mutated mentally are killed by a named test: frozen ancestor ranges (BranchFlowTests.cs:42), saved key never sent to another host or port (ModelSettingsTests.cs:52), cascade delete backs up first and deletes nothing if the backup fails (ConversationDeleteFlowTests.cs:140,176), truncated titles and summaries never saved (ConversationFlowTests.cs:32, SummaryFlowTests.cs:103, ContextCompressionTests.cs:128), and key redaction in client errors and log files.
- Helpers are built to fail loudly. UiWait.UntilAsync and ChatHarness.UntilAsync report the unmet condition expression with file and line; UiInput.Click settles the dispatcher and throws by name when a press misses a button; ChatHarness.StaysAsync polls a 'nothing happens' condition instead of sleeping.
- No vacuous tests: every zero-assert or one-assert method I opened (NetworkLayoutTests.cs:23, ConversationActionsTests.cs:106, SourceHighlightThemeTests.cs:202, ConversationNewsHintTests.cs:70) asserts through a helper or a failing wait.
- Shared state is handled deliberately. Every harness gets its own GUID temp directory; static switches (Motion.Enabled, ActivitySpinner.Animates) are flipped only inside try/finally with no await in between; AvaloniaTestIsolation(PerAssembly) is documented with the failure it fixed (TestApp.cs:8-12).
- Architecture tests enforce layering, vendor-name bans, credential-read locations and view-model boundaries (tests/Mindweave.Architecture.Tests).
- The team already practises 'revert the fix, the test must fail' and records flaky-test root causes in TRACK.md (2026-11-05 ordering assumptions, 2026-11-12 font-dependent pixel probe).

**Not examined**

- I ran no tests (read-only rule). Pass/fail status comes only from the partial baseline test.txt, which covers the Desktop project; Core, Infrastructure and Architecture results were not in the file when I read it.
- Not read line by line: most of the 424 Desktop tests. I read the helpers, AppHostLifecycleTests, parts of MotionTests, the news-hint tests, ExitSavingTests and the GalleryTests scaffolding; NetworkCanvas/NetworkDrag, UsageStatisticsUi, MultiSelect, RowMenu and WorkspacesPage tests were sampled only by name and assertion count.
- Statistics tests (UsageStatisticsTests, UsagePricingTests, LocalCalendarTests) and the summary draft/read-state tests were not checked for correctness of expected values.
- RealApiTests and the performance tests were read only for their skip conditions; I did not judge whether their budgets or prompts are meaningful.
- No real coverage numbers: the src-to-test mapping was done by name grep and call-chain reading, so 'untested branch' claims are limited to the ones I verified by grepping for the specific option, error text or enum.
- Windows-only behaviour of the tests (file locks, timer granularity) and the Windows mailbox test flow were not examined.
- scripts/*.sh (perf, real-api, windows) were only grepped for their dotnet test invocations.

### tests-1. No test covers an answer whose stream ends without a completion event

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Application/Conversations/AnswerRunner.cs:131`; effort S

**Evidence.** AnswerRunner.cs:124-134 marks an answer Complete only if a Completed event was seen; otherwise it becomes Failed/InvalidResponse with the detail "The service closed the stream without finishing the answer." FakeChatClient always ends an answer with Completed(false) (tests/Mindweave.Testing/FakeChatClient.cs:157 and :206) and has no way to end early. The only real-client flow test, StreamDropTests, ends the body with an IOException or HttpIOException, which takes the Network branch instead. ChatCompletionsClientTests.cs:118 checks only that the client yields no Completed. Grepping tests for 'InvalidResponse' and 'without finishing' finds no answer-level test.

**Failure scenario.** A relay or proxy closes the HTTP body cleanly (EOF, no exception) after half an answer. If someone simplifies the loop so the `completed` check is dropped or inverted, the half answer is stored as Complete with no retry offered, and it becomes branchable and summarizable. Every test still passes.

**Recommendation.** Add an EndWithoutCompleting script to FakeChatClient and a flow test asserting Failed + InvalidResponse + text kept + CanRetry. Add a third 'eof' case to StreamDropTests.Drops, which BreakingStream already supports with failure == null.

**Skeptic: partially confirmed, medium.** The factual claims hold; I could not find a test that exercises the branch. The severity is overstated because production code is correct today and the gap is a missing regression guard, not a live defect.

What I checked:
- **The branch exists as described.** AnswerRunner sets `completed = true` only on a `Completed` event; otherwise the answer becomes Failed / InvalidResponse with the quoted detail string.
- **No test reaches it through the answer flow.** A grep of tests/ for `InvalidResponse`, `without finishing` and `closed the stream` hits only workspace-name, knowledge-summary and HTTP-status tests. None is an answer-level assertion.
- **No fake client can end an answer early.** FakeChatClient ends every answer path with `Completed(false)` or a thrown `ChatClientException`. `TruncateNext` exists only on `RequestScript`, which serves internal requests (titles, summaries, naming), not answers. The other test clients I read (GatedReasoningClient, TextThenReasoningClient, StubbornClient) end with `Completed`; CountingClient wraps FakeChatClient.
- **The one real-client flow test takes a different branch.** StreamDropTests is the only harness test using the real `ChatCompletionsClient` offline, and its two cases ("io", "http-io") both throw, so they land in the Network catch. `BreakingStream` does support a clean EOF when `failure` is null, but `DroppingHandler` then serves all three chunks including `finish_reason` and `[DONE]`, so no clean-EOF-after-partial case exists.
- **The client half is covered.** `A_stream_that_ends_without_finishing_yields_no_completion` asserts the client emits no `Completed` on a clean EOF. Only the consumer half is unguarded.
- **No design decision makes this intended.** Nothing in docs/design or TRACK.md addresses this path.

So removing or inverting the `if (completed)` check in AnswerRunner would, by this read, leave the suite green; I did not run a mutation because the audit is read-only.

Why medium rather than high: the client contract is tested, and the unguarded part is a ten-line explicit if/else. The harm needs a future regression plus a relay that closes cleanly mid-answer. The consequence if it did regress is real (a half answer stored as Complete, with no retry offered), which keeps it above low. The proposed fix is cheap and correct.

*Corrections to the finding.* - Severity should be medium, not high: this is a regression-guard gap on a small explicit branch whose upstream contract is unit-tested, not a present defect.
- The cited line is slightly off: the else-branch is AnswerRunner.cs:129-134 and the InvalidResponse assignment is at :132, not :131.
- "FakeChatClient ... has no way to end early" needs a qualifier: it can end an answer early by throwing (`FailWith`), and its internal-request scripts support `TruncateNext`. What it lacks is a clean end without `Completed` for answers.
- The "every test still passes" mutation claim is inferred from reading the tests; neither the auditor nor I ran it.
- A similar branch in ModelSettingsService.cs:193 ("The stream ended without completing.") has the same shape; I did not check whether it is tested.

**Reproduction attempt: confirmed, medium.** The finding is a real test gap, confirmed by a mutation experiment, but it is not a present defect in the product.

Product behaviour today is correct: a new flow test that ends the HTTP body cleanly (EOF, no exception, no finish_reason, no [DONE]) after two text chunks passes against unmodified code. The answer ends Failed + InvalidResponse, keeps the text "蒸汽机车最早", its task is "failed", and CanRetry is true.

The gap is real: I replaced `if (completed)` at AnswerRunner.cs:125 with `if (completed || !completed)`, so a stream with no completion event is stored as Complete. Against that mutant, all four existing test projects stayed green (Infrastructure 412 passed / 18 skipped, Desktop 381 passed / 58 skipped, Core 91, Architecture 8). Only the new EOF test failed, with "Expected: Failed, Actual: Complete".

The static claims check out:
- FakeChatClient ends every answer path with Completed(false) or a thrown ChatClientException; it has no early-end script.
- The other test IChatClient implementations (GatedReasoningClient, TextThenReasoningClient, StubbornClient, RequestScript) also always emit Completed. EndlessClient never ends at all.
- StreamDropTests.Drops is only ["io", "http-io"], both of which take the Network branch.
- ChatCompletionsClientTests.cs:119-122 checks only the client layer (no Completed yielded).
- No test references "closed the stream" or asserts an answer-level InvalidResponse.

Severity lowered to medium because the current code is right and harm requires a future regression. It is still worth fixing: the unguarded branch is the only thing stopping a truncated answer from becoming Complete (branchable, summarizable, no retry) when a relay closes the stream cleanly, which is a realistic failure with OpenAI-compatible proxies. The fix is about 15 lines of test code.

*Corrections to the finding.* - The claimed "high" is overstated. No current behaviour is wrong; the EOF path works end to end through the real client. This is an unguarded-regression risk, so medium.
- The location is slightly off: the check is at AnswerRunner.cs:125 and the detail string at :133, not :131. StreamDropTests is under tests/Mindweave.Infrastructure.Tests/Faults/, not Chat/.
- The recommendation to "add a third 'eof' case to Drops" does not work as a plain theory row. The existing theory asserts GenerationErrorKind.Network, and DroppingHandler sends the full chunk list (including finish_reason and [DONE]) whenever failure is null. It needs either a separate test or a parameterised expected kind, plus the one-line handler change shown in the reproduction.
- "Every test still passes" is verified for the dropped-check mutation only. I did not test an inverted check; that would fail nearly every flow test and is not a realistic gap.
- 18 Infrastructure and 58 Desktop tests are skipped in this macOS environment. I did not check whether any of them (for example Windows-only ones) would kill the mutant; from the grep evidence that is unlikely.

<details><summary>Reproduction</summary>

Test added to tests/Mindweave.Infrastructure.Tests/Faults/StreamDropTests.cs. It also needed a one-line handler change, `var chunks = Drop is null ? Chunks : Chunks[..2];`, so that "eof" sends two chunks and then a clean end:

    [Fact]
    public async Task Repro_a_body_that_ends_cleanly_without_finishing_fails_as_invalid_response_and_offers_retry()
    {
        var handler = new DroppingHandler("eof");
        using var http = new HttpClient(handler);
        await using var h = await ChatHarness.StartAsync(client: new ChatCompletionsClient(http, new ChatClientOptions()));

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Failed, answer.State);
        Assert.Equal(GenerationErrorKind.InvalidResponse, answer.ErrorKind);
        Assert.Equal("蒸汽机车最早", answer.Text);
        Assert.Equal("failed", await h.TaskStateAsync(sent.Answer!.Value));
        var history = (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.Messages;
        Assert.True(ConversationRules.CanRetry(history, answer.Id));
    }

Run 1 — unmodified product code, `dotnet test tests/Mindweave.Infrastructure.Tests --filter FullyQualifiedName~StreamDropTests`:
    Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 433 ms
So current behaviour is correct.

Mutation — AnswerRunner.cs:125 `if (completed)` changed to `if (completed || !completed) // MUTATION`.

Run 2 — mutant, repro test only (`--filter FullyQualifiedName~Repro_a_body`):
    Failed ...StreamDropTests.Repro_a_body_that_ends_cleanly_without_finishing_fails_as_invalid_response_and_offers_retry [123 ms]
    Assert.Equal() Failure: Values differ
    Expected: Failed
    Actual:   Complete
    Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1

Run 3 — mutant, every existing test (`--filter FullyQualifiedName!~Repro_a_body` for Infrastructure; the other projects in full):
    Passed!  - Failed: 0, Passed: 412, Skipped: 18, Total: 430, Duration: 11 s - Mindweave.Infrastructure.Tests.dll
    Passed!  - Failed: 0, Passed: 381, Skipped: 58, Total: 439, Duration: 1 m 19 s - Mindweave.Desktop.Tests.dll
    Passed!  - Failed: 0, Passed: 91, Skipped: 0, Total: 91 - Mindweave.Core.Tests.dll
    Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8 - Mindweave.Architecture.Tests.dll

The mutant survives the whole existing suite and is killed only by the new EOF test. None of this is timing-sensitive.

</details>

### tests-2. Client timeouts and several error branches have no test at all

- Final: **medium** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:186`; effort S

**Evidence.** Grepping tests for FirstByteTimeout and ModelListTimeout returns nothing. Untested branches: first-byte timeout (:186, :195-198); model-list timeout (:126, :142-145); model list not valid JSON (:150-153); redaction on the ListModels path (:113-116); silence after completion treated as a clean end (:220); a stream event that is not valid JSON (:241); and the 413 context-limit classification. On the application side, the 'empty content' rejections (GenerationRequests.cs:121, ContextCompressor.cs:199) and both 'stream not finished' rejections for internal requests are also untested; no test enqueues an empty reply.

**Failure scenario.** Deleting line 186 passes every test. Because production sets HttpClient.Timeout to infinite (InfrastructureServices.cs:49), a server that accepts the connection and never sends headers would then hold a generation slot forever, and the answer would show 'waiting' until the user stops it. Separately, a title or summary request that returns only whitespace could be saved as an empty title or summary if the Length == 0 arm is lost.

**Recommendation.** Add StubHandler cases that never return headers (FirstByteTimeout = 100 ms), a slow /models response, malformed JSON for both endpoints, a 401 on /models that echoes the key, and finish_reason followed by a stalled body. Add Titles.Replies.Enqueue(" ") style tests for the empty-output arms.

**Skeptic: partially confirmed, medium.** The test-gap claims hold; one of the two failure scenarios is overstated. I did not run any tests (read-only audit), so "deleting line 186 passes every test" is inferred from reading the tests, not from a mutation run.

**Confirmed by reading the code and tests**
- **First-byte timeout:** no test sets or triggers `FirstByteTimeout`. `StubHandler` always returns headers immediately, and the only timeout test uses `Stalled()`, which returns 200 headers with a never-ending body, so it exercises only `IdleTimeout`.
- **No other guard:** the only `CancelAfter` calls in `src` are the three in `ChatCompletionsClient` plus one in `SingleInstance`. Production sets `HttpClient.Timeout` to infinite, and the handler's 30 s `ConnectTimeout` covers only connection setup. A server that accepts and never sends headers would hang until the user stops it if line 186 regressed.
- **Model list:** the only `ListModelsAsync` test against the real client is the happy path. Its timeout, malformed-JSON and key-redaction branches are untested; the redaction test covers only the streaming path.
- **Silence after completion:** every test stream ends with EOF or `[DONE]`; none stalls after `finish_reason`.
- **Invalid stream JSON:** no test feeds a malformed `data:` event.
- **413:** `Http_errors_are_classified` has a 400 context-limit case but no 413 case.
- **Empty and unfinished internal replies:** `RequestScript.StreamAsync` always yields a `TextDelta` then `Completed`, so the shared fake cannot produce an unfinished stream. No `Replies.Enqueue` call passes empty or whitespace text.
- **No design decision covers this:** I found nothing in `TRACK.md` or `docs/design/decisions.md` that accepts these gaps.

**Overstated**
- **Empty title or summary being saved:** losing the `Length == 0` arm in `GenerationRequests` would not save an empty title. `ConversationRules.CleanGeneratedTitle` returns null for an empty line and the title runner then fails the task. Summaries go through `SummaryRules.ParseGenerated`, which is null-checked in `SummaryService` and has a test for a title-only reply; I did not open the parser to confirm it rejects empty text.
- **Where the empty arm does matter:** the context-compression path (`ContextCompressor.cs:199`) and the workspace-name path have no second guard that I verified.
- **Partial coverage of the 400/413 arm:** the classification arm is shared and the 400 case exercises it; only the 413 literal is uncovered.

**Severity**
Medium stands for the first-byte timeout alone: it is the sole protection against a held generation slot and has zero coverage. The production code is correct today, so this is regression risk, not a live defect. The other items are low individually.

*Corrections to the finding.* - **Empty title or summary:** the claim that a whitespace-only reply "could be saved as an empty title or summary" is wrong for titles and probably wrong for summaries. `CleanGeneratedTitle` returns null on empty output and the task fails; summaries pass through `ParseGenerated` with a null check. The unguarded paths are context compression (`ContextCompressor.cs:199`) and workspace-name suggestion.
- **File path:** `GenerationRequests.cs` is at `src/Mindweave.Application/Conversations/GenerationRequests.cs`. The empty arm is at line 121 as cited; the "stream not finished" arm is at line 119.
- **413:** "the 413 context-limit classification" is untested only for the 413 literal. The shared arm is exercised by the 400 case at `ChatCompletionsClientTests.cs:129`.
- **"Deleting line 186 passes every test":** consistent with the tests I read, but not demonstrated by a run. Even with the line removed the user can still stop the answer, so "forever" means until the user stops it or the app exits.

### tests-3. FakeChatClient does not reproduce real stream shapes: usage first, no ResponseStarted, no truncated answer

- Final: **low** (confirmed); auditor said medium
- Where: `tests/Mindweave.Testing/FakeChatClient.cs:140`; effort S

**Evidence.** FakeChatClient.cs:116 documents usage as reported 'as a real one does at the end of its stream', but line 140 yields the Usage event before any text and before Completed. The real client yields Completed and then Usage (ChatCompletionsClient.cs:98-107; OpenAI sends a trailing chunk, Kimi puts it on the final choice). The fake never yields ResponseStarted, so HTTP status, provider request id and time-to-response never pass through AnswerRunner and RequestTrace into ai_request in fake-based tests. Answers cannot end with Completed(true); only RequestScript-driven internal requests can truncate. TokenCalibrationFlowTests and the statistics tests all rely on this fake ordering.

**Failure scenario.** A developer makes AnswerRunner or GenerationRequests stop reading at Completed, a natural change given ModelSettingsService.TestAsync already returns there. With real services, prompt and completion tokens are then never recorded. Token calibration silently stops learning (Claude via relay was underestimated by about 17% per TRACK 2026-11-05, so sends get rejected) and the statistics page shows no usage. All calibration and statistics tests stay green because the fake sent usage first.

**Recommendation.** Make the fake emit ResponseStarted(200, id) first and Usage after Completed, with a switch for the Kimi shape. Add Truncate() for answers and decide and test what a length-truncated user answer should look like; today it is stored as Complete with no marker. Assert http_status and provider_request_id on one flow test.

**Skeptic: confirmed, low.** Every factual claim checks out; I could not refute any of them. The severity is overstated: this is a test-fidelity gap that only bites after a hypothetical future regression, not a current defect.

What is true in the code:
- **Usage order:** the fake yields Usage before any text and before Completed, while its own doc comment says usage arrives "at the end of its stream". The real client yields Completed and then Usage, both when usage is on the same chunk (Kimi shape) and when it is on a trailing chunk (OpenAI shape).
- **ResponseStarted:** the fake never yields it, and no other test double feeds it through the harness. The only automated assertion on it is at client level. RequestTraceTests, which run on the fake, assert outcome, character count and time-to-first-text but never http_status, provider_request_id or time-to-response. The path ResponseStarted -> RequestTrace -> ai_request for a successful request is untested offline.
- **Truncated answers:** the fake's answer path only ever yields Completed(false); truncation exists only in RequestScript for internal requests. AnswerRunner sets Complete on any Completed and ignores Truncated, so a length-truncated user answer is stored as a Complete message. No test covers this.
- **Regression scenario:** AnswerRunner and GenerationRequests currently read to the end of the stream, so usage is recorded correctly today. If someone added an early exit at Completed (as ModelSettingsService.TestAsync already does), tokens would be lost with real services and fake-based calibration and statistics tests would stay green.

Mitigations I looked for:
- ChatCompletionsClientTests pin the real ordering at the client level, but not through the consumers.
- StreamDropTests run the real client through the harness, but their SSE carries no usage and they do not assert status.
- RealApiTests check prompt_tokens and calibration samples end to end, but only when run manually with a key via scripts/real-api.sh.
- Neither decisions.md nor TRACK.md records the fake's ordering as intentional.

Why low rather than medium: there is no present bug, the consumers are simple loops, and the regression is speculative. The fix is cheap (reorder the fake, emit ResponseStarted, add an answer Truncate()). The truncated-answer-stored-as-Complete behaviour is a separate product question worth its own decision.

*Corrections to the finding.* - "Stored as Complete with no marker" is true for the message only: the ai_request diagnostics row does get outcome 'truncated' (RequestTrace.cs:84). Nothing user-visible marks it.
- The real ordering is not wholly untested: ChatCompletionsClientTests pin it at client level, and RealApiTests cover prompt tokens and calibration end to end when run manually with a key. The gap is specifically the offline consumer-level tests (AnswerRunner / GenerationRequests / RequestTrace).
- The failure scenario is a hypothetical future regression; current production code reads the full stream and records usage correctly. Low fits better than medium.
- I did not verify the TRACK 2026-11-05 "about 17%" Claude underestimate figure.

### tests-4. The platform credential stores are never run by any automated test

- Final: **low** (confirmed in part); auditor said medium; already known in TRACK.md
- Where: `tests/Mindweave.Infrastructure.Tests/Chat/PlatformCredentialStoreTests.cs:14`; effort S

**Evidence.** The single test for MacKeychainCredentialStore (217 lines of SecItem/CoreFoundation P/Invoke) and WindowsCredentialStore (129 lines) is skipped unless MINDWEAVE_CREDENTIAL_TESTS=1. No script or workflow sets that variable; a repo grep finds it only in the test and in TRACK.md. CI runs ubuntu and windows only, with macOS opt-in (.github/workflows/ci.yml:24). Every harness substitutes MemoryCredentialStore, so the production registration CredentialStores.ForPlatform (InfrastructureServices.cs:46) is never resolved. TRACK.md (2026-11-12, 2026-10-26) records that the test is opt-in and that Windows over SSH cannot use Credential Manager, but not that it never runs.

**Failure scenario.** A change to the P/Invoke layout (CFDictionary construction, the duplicate-item update path, CredWrite blob encoding) or a runtime or OS update breaks key storage. Build and all 892 tests stay green, and the first sign is a user who cannot save an API key, or whose saved key reads back wrong after an update, on either platform.

**Recommendation.** Set MINDWEAVE_CREDENTIAL_TESTS=1 in the windows-latest CI job and in the opt-in macOS job; hosted runners have a logged-on user and the test uses a throwaway namespace. Add cases for a missing-item delete, a long key and non-ASCII. Add one test that resolves ICredentialStore from an unmodified CompositionRoot.Build to cover the registration.

**Skeptic: partially confirmed, low.** The core facts hold. The only test that touches the real Keychain / Credential Manager is gated on MINDWEAVE_CREDENTIAL_TESTS=1, and nothing in the repo sets that variable: a grep finds it only in the test file and in TRACK.md line 94. Neither workflow sets it, and no script under scripts/ mentions it. CI runs ubuntu and windows by default with macOS behind a dispatch input. Every DI harness replaces ICredentialStore with MemoryCredentialStore, so the production registration at InfrastructureServices.cs:46 is never resolved in a test. I could not refute any of that.

The finding is overstated in four ways, which is why I lower it to low:

1. The opt-in is a documented design choice, not an oversight. The test's doc comment says it is opt-in because it writes to the signed-in user's secret store; run-guide.md line 16 lists "system credentials" among the explicitly opt-in tests; TRACK.md 2026-10-26 records that Windows over SSH cannot use Credential Manager at all (error 1312), which rules out the main Windows automation path (scripts/windows.sh).

2. There is compensating manual evidence. stage-reports.md line 720 records "Windows Credential Manager, real-machine key save: passed (2026-10-26, user confirmed)", and s6-windows-checklist.md row 24 keeps a desktop-session check for it. TRACK.md line 94 says the Windows tester can run the opt-in test in a desktop session with the user's consent; I found no record that it has actually been run.

3. Exposure to regression is small. `git log -- src/Mindweave.Infrastructure/Credentials` shows a single commit (c20a37a, 2026-10-26); the P/Invoke code has not changed since it was written. A break would surface on first use as a visible "credential service unavailable" error rather than silent data loss, because the design refuses a plaintext fallback.

4. Part of the recommendation is already done. The existing test writes a non-ASCII secret ("sk-测试-1"), exercises the update-over-existing path (second WriteAsync), and deletes an already-deleted item at the end. Only the long-key case is missing.

What remains valid: a credential-storage regression from a runtime or OS update would pass CI on both platforms, and the s6 checklist lists PlatformCredentialStoreTests in its "automatic coverage" column, which overstates what runs. Enabling the variable on the windows-latest job is a cheap fix. I did not verify that a hosted runner's logon session supports CredWrite or that the macOS runner's keychain is unlocked; that assumption in the recommendation is untested. The registration gap is real but trivial: CredentialStores.ForPlatform is a three-branch OS switch that the opt-in test calls directly.

*Corrections to the finding.* - "Never run by any automated test" should read "skipped in every default and CI run"; an opt-in test exists and the opt-in is documented as deliberate (run-guide.md:16, the test's own doc comment, TRACK.md:94 and :124).
- The recommendation to add missing-item delete and non-ASCII cases is redundant: the existing test already does both, plus the update-over-existing path. Only a long-key case is absent.
- The finding omits the recorded manual verification on Windows (stage-reports.md:720) and the desktop-session checklist row (s6-windows-checklist.md:46).
- The claim that hosted runners can use the credential stores is an untested assumption; TRACK.md:124 shows a Windows session type where CredWrite fails, so this needs a trial run before relying on it.
- The failure scenario "saved key reads back wrong" is less likely than stated: the code has had one commit since creation, and the design surfaces store failures as an explicit error rather than degrading.
- Severity medium is too high given the deliberate opt-in, the manual evidence and the unchanged code; low fits, with the s6 checklist's "automatic coverage" wording worth correcting.

### tests-5. About 20 desktop assertions wait a fixed 200–300 ms and then assert nothing happened

- Final: **low** (confirmed in part); auditor said medium
- Where: `tests/Mindweave.Desktop.Tests/WorkspaceNewsHintTests.cs:421`; effort M

**Evidence.** Scene.SettledAsync (WorkspaceNewsHintTests.cs:419-424) is `await Task.Delay(300); await News.Loaded;`, commented as 'Past the hints' 100 ms refresh delay'. It is used 15+ times before negative assertions such as Assert.Empty(news.Toasts) and Assert.Equal(0, news.Count). The same pattern is inlined at ConversationNewsHintTests.cs:343, WorkspaceNewsTests.cs:100, WorkspaceNewsHintTests.cs:309 and ExitSavingTests.cs:184 (200 ms, then ExitRequests == 0). The product delay is real time: UiThrottle uses Task.Delay(interval) (src/Mindweave.Desktop/Services/IUiDispatcher.cs:31), not the injectable clock these scenes use for toast lifetime. TRACK.md records Windows running the desktop suite about 3.5x slower, and today's baseline took 1 m 56 s versus the usual 37 s.

**Failure scenario.** A regression makes a toast pop up for marks restored at startup, or lets the exit sequence request shutdown while an export is still writing. On a loaded or slow-sync machine the throttled reload or exit step lands after the 300/200 ms window, so the assertion reads the old state and passes. In ExitSavingTests the later `ExitRequests == 1` also passes, so the test cannot detect the bug there.

**Recommendation.** Give UiThrottle the same TimeProvider the scenes already fake and advance it, then await a deterministic 'refresh ran' counter (the scenes already expose MarksNoted). For the export case, assert Exit.Stage and !Exit.Running.IsCompleted while the writer gate is held instead of sleeping.

**Skeptic: partially confirmed, low.** The pattern is real, but the count, the Scene line range and the consequence are overstated.

**What holds**
- `Scene.SettledAsync` is `await Task.Delay(300); await News.Loaded; Window.UpdateLayout();`, commented as getting past the 100 ms refresh delay.
- `UiThrottle.Request` uses real-time `Task.Delay(interval)`, with no `TimeProvider`. `WorkspaceNewsViewModel` builds it with 100 ms.
- The four inlined sites exist as cited, including `ExitSavingTests.cs:184` (200 ms, then `ExitRequests == 0`).
- The baseline desktop run took 1 m 56 s against the 37 s that TRACK.md records, so the machine was about 3x slower than usual during it.
- I found no decision in TRACK.md accepting fixed sleeps. Its recorded lessons point the other way: tests that assumed background-task ordering were fixed as defects.

**The exit test is the strongest instance**
- `window.Close()` leads to `ExitCoordinator.AskAboutTasks`, which sets `Running = CommitAsync()` asynchronously.
- If a regression stopped waiting for the export, the shutdown request would land only after the stop and flush steps, which do synced DB writes.
- If those steps exceed 200 ms, `ExitRequests == 0` passes. The later `ExitRequests == 1` and `writer.Writes == 1` then also pass, because the test itself releases the writer and awaits the export.
- On a normal run the steps finish in milliseconds and the test would catch the regression. The guard is probabilistic, not absent.

**What is overstated**
- **Count.** There are 14 `scene.SettledAsync()` calls (8 in ConversationNewsHintTests, 6 in WorkspaceNewsHintTests) plus 4 inline sleeps, so 18 sites, not "15+ uses" plus more.
- **Negative assertions.** Only about 7–8 sites are followed by purely negative assertions: ConversationNewsHintTests 63, 194, 343; WorkspaceNewsHintTests 264, 309; WorkspaceNewsTests 100; ExitSavingTests 184; partly WorkspaceNewsHintTests 83. The rest precede positive assertions such as `Assert.Single(news.Toasts)` or an exact toast list. A late refresh there causes a visible false failure, not a silent pass.
- **Partial determinism.** `ConversationNewsHintTests.cs:194` also asserts `MarksNoted` is unchanged, a counter incremented synchronously in `OnAnswered`, so the sleep is not its only guard. The two restart cases first poll for state that the first refresh produces (`news.Count == 3`, `IsUnread`), so a toast created in that same refresh would not depend on the sleep.
- **Margin.** A false pass needs a real regression and a stall of more than 200 ms beyond the 100 ms throttle at that exact point. TRACK.md records the full desktop suite passing repeatedly with 10 ms added to every disk sync; the refresh path is mostly reads.
- **Other `SettledAsync`.** The one in `WorkspaceNamePreparationUiTests` is a different method on the preparer, not this sleep.

This is a real test-robustness weakness of about 8 time-bounded negative assertions, plus roughly 5 s of fixed sleeping per run. It is not a current defect and has no observed flake, so low rather than medium. The recommendation (inject `TimeProvider` into `UiThrottle`; assert `Exit.Stage` and `!Exit.Running.IsCompleted` while the gate is held) is sound.

*Corrections to the finding.* - The site count is 18 (14 `scene.SettledAsync()` calls plus 4 inline sleeps), not "15+ uses" plus inlines.
- Only about 7–8 sites guard purely negative assertions. The others precede positive assertions, where a late refresh produces a visible false failure, not a silent pass.
- `ConversationNewsHintTests.cs:194` also checks the synchronous `MarksNoted` counter, and the two restart cases poll for refresh-produced state before sleeping, so those are less exposed than claimed.
- "The test cannot detect the bug" applies to ExitSavingTests only when the exit steps take longer than 200 ms; on a normal run it would detect it.
- The `Scene.SettledAsync` range is 418-424, not 419-424.
- No flake or missed regression from this pattern is recorded, and TRACK.md reports the desktop suite passing under injected slow sync. Severity is low, not medium.

### tests-6. Enum-to-storage and enum-to-UI-text mappings have no exhaustive test

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/SqlNames.cs:156`; effort S

**Evidence.** SqlNames.Of(GenerationErrorKind) throws ArgumentOutOfRangeException for an unmapped value (:156-170). ParseErrorKind silently maps unknown strings to InvalidResponse (:173-186). UiText.ErrorKind falls back to "未知错误" (src/Mindweave.Desktop/UiText.Conversation.cs:92-106). The same pattern exists for MessageState, GenerationTaskState and AiRequestOutcome. The only Enum.GetValues loop in the whole test tree is for Appearance (ThemeTests.cs:92), and SqlNames is referenced by one test file.

**Failure scenario.** A new GenerationErrorKind is added and used in AnswerRunner but not in SqlNames.Of. The first answer that fails that way throws inside FinishAnswerAsync, the terminal write fails, and StorageHealth pauses all new requests (the behaviour described in TRACK 2026-11-01). If Of is updated but ParseErrorKind is not, the kind is stored correctly but always read back as 'invalid response'. No test fails in either case unless someone happens to write a flow test for the new kind.

**Recommendation.** Add theories over Enum.GetValues<T>() for each mapped enum: Parse(Of(x)) == x, UiText.ErrorKind(x) != "未知错误", and, for columns with a CHECK list, an insert of every value into a fresh database.

**Skeptic: partially confirmed, low.** The test gap is real, but the headline failure scenario is wrong for current code and the severity is overstated.

What holds:
- The cited code is as described. SqlNames.Of(GenerationErrorKind) throws ArgumentOutOfRangeException on an unmapped value, ParseErrorKind defaults unknown strings to InvalidResponse, and UiText.ErrorKind falls back to "未知错误".
- There is no exhaustive test. The only Enum.GetValues/GetNames use in tests/ is ThemeTests.cs:92 (Appearance). SqlNames is referenced by one test file, ModelCapabilitiesTests.cs, for capabilities JSON only. No test references UiText.ErrorKind or "未知错误".
- Coverage of GenerationErrorKind through storage is incidental. Most kinds appear in a single test file, usually the HTTP classification theory in ChatCompletionsClientTests, which does not go through SQLite. NotConfigured appears in no Infrastructure test.
- The silent read-back case is real: adding a kind to Of but not ParseErrorKind stores it correctly and reads it back as InvalidResponse, with a misleading message and no failing test.
- I found no decision in TRACK.md or docs/design that waives such a test.

What is wrong or overstated:
1. "StorageHealth pauses all new requests" does not happen. That was the pre-A3 behaviour, which TRACK 2026-11-01 lists as a problem that was fixed. TerminalWrites.SaveAsync now treats only StorageWriteException as a storage condition. Any other exception, including ArgumentOutOfRangeException from SqlNames.Of, is logged as a write defect and queued in _unsavedDefects, which per the code comment "neither pause requests nor hold up recovery". StorageFailures.ForCaller only converts SqliteException with storage result codes. The real consequence is narrower: that one failed answer's terminal state is not persisted, it is reported unsaved at exit, and the message would likely surface as interrupted on next start.
2. "The same pattern exists for MessageState, GenerationTaskState and AiRequestOutcome" is only partly true. Their Parse functions throw InvalidDataException on unknown strings, so there is no silent mis-mapping; the lenient fallback is unique to ParseErrorKind and is a documented forward-compatibility choice. These enums also have CHECK lists in the schema, and their values are exercised constantly by flow tests.
3. The error_kind columns have no CHECK constraint, so the recommended "insert every value" test does not apply to GenerationErrorKind. It would apply to state, outcome, kind, origin, name_source and read_source. GenerationTaskKind is the one enum where this has mattered: the CHECK list had to be widened by migration from ('answer','title') to include 'summary' and 'naming'.
4. Triggering any of this requires a developer to add an enum member and forget a switch arm sitting a few lines away in the same file. The resulting defect is bounded: one unsaved terminal state or a wrong error label, with no data corruption and no global pause.

Net: a cheap, worthwhile hardening test is missing (round-trip theory per enum, UiText non-fallback, CHECK-list insert), but it guards a hypothetical future regression of limited blast radius. Low, not medium.

*Corrections to the finding.* - The claim that an unmapped kind makes StorageHealth pause all new requests is false for current code. Since A3 (TerminalWrites.cs:26-28, 66-78) a non-storage exception in a terminal write is logged as a defect and kept for exit reporting without pausing. The actual impact is one answer's terminal state left unsaved.
- "The same pattern" for MessageState, GenerationTaskState and AiRequestOutcome is overstated: their parsers throw rather than silently defaulting. Only ParseErrorKind is lenient, and that is a documented forward-compatibility choice.
- error_kind has no CHECK constraint, so the "insert every value" recommendation applies to the state, kind, outcome, origin, name_source and read_source columns, not to GenerationErrorKind.
- All mappings are complete today; this is a missing regression guard, not an existing defect. Severity should be low.

### tests-7. No coverage measurement and no per-test timeout in a 31k-line suite

- Final: **low** (not verified); auditor said low
- Where: `Directory.Packages.props:16`; effort S

**Evidence.** Directory.Packages.props lists xunit.v3, the runner and Avalonia.Headless.XUnit but no coverlet or other coverage collector, and ci.yml runs a plain `dotnet test`. No xunit.runner.json exists, and no [Fact(Timeout=...)] appears anywhere (grep count 0). Many awaits have no bound, for example `await writer.Started.Task;`, `await export;` and `await app.Root.Exit.Running;` (ExitSavingTests.cs:181-189). PumpUntil (AppHostLifecycleTests.cs:159-167) gives up silently after 5 s and the test then does an unbounded `await archiving`.

**Failure scenario.** Coverage: the untested branches in findings 1 and 3 were found only by manual grep; nothing reports them, or the next ones, automatically. Timeout: a regression that leaves an exit sequence or gated writer waiting hangs the whole Desktop assembly, which runs serially on one UI thread. CI then dies at the 20-minute job timeout with no failing test name, and locally the run just sits.

**Recommendation.** Add coverlet.collector and publish a per-project branch-coverage report in CI, starting with a ratchet rather than a threshold. Set a default per-test timeout or longRunningTestSeconds in xunit.runner.json so a hang names the test. Make PumpUntil throw on its deadline.

### tests-8. All functional UI tests run with motion and spinner animation switched off

- Final: **low** (not verified); auditor said low
- Where: `tests/Mindweave.Desktop.Tests/TestApp.cs:21`; effort M

**Evidence.** TestAppBuilder sets ActivitySpinner.Animates = false and Motion.Enabled = false for the whole assembly (TestApp.cs:21-23). App.Initialize then skips loading Styles/Motion.axaml (src/Mindweave.Desktop/App.axaml.cs:29-32) and Motion.PlayIfEnabled does nothing. The shipped app runs with both on. MotionTests turns motion on in four places (MotionTests.cs:56,130,159,186), each a synchronous block checking first-frame opacity or offset and the settled end state on an isolated element or one page switch. No click, drag-selection, dialog or popup-menu flow runs with motion on.

**Failure scenario.** A dialog's 0.98-to-1 scale with 8 px rise, or a popup's -4 px translate, moves a hit target while the user clicks during the animation. Or a replaced or cancelled animation leaves a prompt at reduced opacity or offset. Selection anchors, floating branch-action placement and menu clicks are verified only in the motion-off configuration, so such a defect ships with a green suite.

**Recommendation.** Add a small set of motion-on functional tests: open a prompt and click its default button on the first frame, open and click a row menu, switch pages twice quickly and assert the final opacity and transform, and select text right after a page appears. Alternatively, run one smoke subset of existing UI tests with Motion.Enabled = true.

### tests-9. Gallery scenes use fixed temp directories shared by every worktree and run

- Final: **low** (not verified); auditor said low
- Where: `tests/Mindweave.Desktop.Tests/GalleryTests.cs:1881`; effort S

**Evidence.** Scene.Directory returns Path.Combine(Path.GetTempPath(), "mindweave-gallery", name) (GalleryTests.cs:1881). Scene.Create deletes that directory recursively when fresh (:1887-1890) and DisposeAsync deletes it again. All other harnesses use a GUID directory (DesktopHarness.cs:84). AGENTS.md rule 4 requires runtime data directories to be per worktree, and the documented workflow renders the gallery twice, once Light and once Dark.

**Failure scenario.** Two sessions render the gallery at the same time: Light and Dark in parallel, or two task worktrees after parallel UI work, which this track does routinely. One run deletes $TMPDIR/mindweave-gallery/g24 while the other has its SQLite database open there. The result is spurious failures, or screenshots taken from the other run's data, in the frames used for the user's visual sign-off.

**Recommendation.** Put the gallery root under a per-run directory (for example a GUID or MINDWEAVE_SCREENSHOT_DIR-derived subfolder) and keep only the scene name fixed inside it. Frame comparability depends on the fixed clock and content, not on the absolute path, unless the path is rendered, in which case render a placeholder.

### tests-10. Core has 49 direct tests and Application has none; several pure rules are reachable only through slow flows

- Final: **low** (not verified); auditor said low
- Where: `tests/Mindweave.Core.Tests/ContextPlannerTests.cs:1`; effort M

**Evidence.** Test counts: Core 49 (645 lines) for 1,305 source lines; Application 0 dedicated tests for 9,074 source lines (there is no Mindweave.Application.Tests project and Application has no InternalsVisibleTo); Infrastructure 356; Desktop 424 headless-UI. No test references these by name: EnclosingMarks (Core/Text, the title-mark stripping fixed on 2026-11-10 and covered only through ConversationRulesTests title cases), ServiceAddress.SameService, SummaryNaming, ConversationTitler, UsagePrices, NewsAlerts, SelectionCarry and ContentRoute. FileLoggerProvider has one redaction test and nothing for daily roll, 14-day pruning or its IOException tolerance (FileLoggerProvider.cs:71-97).

**Failure scenario.** A boundary bug in a pure rule needs a full SQLite or headless-window flow to observe. Examples: SameService with http versus https on the same host, EnclosingMarks with nested or unbalanced marks, log pruning deleting the newest files if the ordering is flipped. So edge cases are tested sparsely (one host pair, a few titles) or not at all, and the Desktop suite, already serial at about 38 fsyncs per test, keeps growing as the only place to add them.

**Recommendation.** Add direct table-driven tests for the pure helpers listed (EnclosingMarks, ServiceAddress, SummaryNaming parsing, UsagePrices parsing, log roll and prune with a fake TimeProvider). Consider an Application.Tests project with in-memory stores for coordinator and titler edge cases that do not need SQLite.

