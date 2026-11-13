# Requirements conformance — grade B

High B, close to A-. Almost every must-level rule in requirements §5-§9 that I traced has identifiable code, and most have a test: frozen branch ranges, retry limited to the last answer, summaries saved only on success, per-version read state, candidate/adopt/set-current/delete-version rules, stated-scope deletes with backup first, archive semantics, explicit layout save with revision snapshots, the exit prompt order, the two-slot scheduler, and credential references frozen per task. The gaps are few but sit in core promises. An answer cut off at the output limit is stored and shown as a normal complete answer (§6.2, §9.1). The manual "mark unread" switch is undone by the page's own progress reporting (§7.1). The first question typed on the new-exploration page is never persisted (§5.1, §6.3, §11). Summaries share the question priority, contrary to §9.2 and architecture §6. None of these is recorded in TRACK.md, and none has a test that would catch it. This audit was by code reading only: no tests were run, and Finding 2's exact trigger is inferred from code.

**Strengths**

- Branch context is frozen exactly as §6.2 and §9.6 require. `BranchRules.RangesForChild` copies the parent's ranges and adds the parent up to the source seq; `SqliteBranchStore.CreateAsync` writes child, source and ranges in one transaction; `BranchContext.Assemble` keeps the source answer even when it is a partial failed attempt. Later parent messages and sibling branches cannot leak in.
- Summary rules are enforced in the store transaction, not only in the UI (`SqliteSummaryStore`). `SaveGeneratedAsync` writes a version only when the task row is still 'running' (MW-C06). Adopt and SetCurrent refuse while a draft exists. `DeleteVersionsAsync` refuses with a draft, a running task, or a candidate when the last version goes. `SaveEditAsync` adds no version when nothing changed and keeps the baseline's `to_seq`.
- Deletes follow v0.12 "state the scope, then delete". `ConversationDeletionService` and `SqliteArchiveStore.DeleteAsync` recompute the scope inside the transaction and refuse on ScopeChanged or Busy. Cascading deletes back up first and do nothing when the backup fails. A backup made for a delete that then did not happen is discarded. Workspace delete follows the same pattern.
- Model destination and key are frozen per task as §9.2 requires. `ModelSnapshot` carries BaseUrl, Model and CredentialRef; `GenerationRequests.ReadKeyAsync` reads the task's own reference; `ModelSettingsService` writes a new reference per key change and only deletes retired ones no unfinished task uses. `ServiceAddress.SameService` stops a saved key going to a different host.
- The exit sequence matches §9.2 and architecture §12.3 (`ExitCoordinator`). The layout prompt comes first, then the task prompt. "Don't save" is only recorded until the exit is committed. Stopping is bounded. A failed save keeps the window with retry and return, and `Reopen` does not requeue anything.
- The layout rules of §7.4 are implemented precisely in `NetworkSession`. A new node's place is saved alone through `PlaceAsync` (ON CONFLICT DO NOTHING) and never marks the layout dirty. An explicit save snapshots one revision, so changes made during the save stay unsaved. A failed save keeps memory and the old stored layout. The viewport is reset on section entry and workspace switch but kept when returning from a summary.
- Requirement IDs (MW-Cxx, U1-U4, section numbers) are cited in code comments and test names, and most §11 scenarios have a matching test file: BranchUiTests, SummaryUiTests, ExitPromptTests, ArchiveFlowTests, ConversationDeleteFlowTests, SingleInstanceTests, StorageRecoveryTests, TransactionKillTests, KillDuringStreamTests.
- Internal AI outputs that the program uses directly (titles, context summaries, summaries, naming) are only accepted on a normal non-truncated finish, in `GenerationRequests.RequestCompleteTextAsync` and `ContextCompressor`. That satisfies the "no silent truncation" rule of §9.6 for compression.

**Not examined**

- No build or test was run (read-only audit); every finding comes from reading code. The baseline test.txt showed the Desktop test project at 381 passed, 58 skipped, 0 failed.
- Anchor capture and restore accuracy (SourceAnchors.cs, AnswerView, BranchMarks): whether "return to source" lands on the right passage after re-layout, and the MW-C13 "cannot locate" notice. I only checked that the view-model plumbing exists.
- XAML views and visual behaviour: tooltips, accessibility names, the non-colour-only node states in NetworkCanvas, dialog wording. Only spot-checked through UiText and view models.
- Migration, backup and restore internals (SqliteDatabase, SchemaMigrations, SqliteBackupFiles, in-place restore). I read only StartupCoordinator and RecoveryViewModel.
- StorageHealth and TerminalWrites internals (the §9.3 core-save-failure behaviour). I relied on the TRACK A3 description and the existence of tests.
- Real credential stores (MacKeychainCredentialStore, WindowsCredentialStore) and real provider behaviour; Windows-specific behaviour.
- Multi-select archive and delete in the sidebar (ConversationListViewModel), the archived page (ArchivedViewModel), onboarding and workspace rename were only skimmed.
- Features beyond requirements v0.13 first-version scope that the track added: statistics page, workspace name suggestions and preparation, news hints, in-app backup restore UI.
- §10.3 post-v1 increments (recommended questions and nodes, content links): not implemented yet by design and excluded per the task.
- §12 value validation and §5.3 course constraints (MVVM, navigation layering) are outside this dimension.

### requirements-1. An answer cut off at the output limit is stored and shown as a normal complete answer

- Final: **high** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Application/Conversations/AnswerRunner.cs:119`; effort M

**Evidence.** `ChatCompletionsClient.cs:101` reports truncation: `yield return new ChatStreamEvent.Completed(Truncated: reason == "length")`. `AnswerRunner.RunAsync` ignores the flag: `case ChatStreamEvent.Completed: completed = true;` then `final = MessageState.Complete` (lines 119-127). `MessageItemViewModel` shows no state label for Complete, and `Message.CountsAsContext` treats it as usable context. Truncation is recorded only in the diagnostic `ai_request` row (`AiRequestOutcome.Truncated`). The codebase rejects truncated output for titles, context summaries, summaries and naming (D-015), but not for user answers. No test covers a truncated answer: grep for `Truncated` in tests finds only the context-summary case.

**Failure scenario.** The user asks for a long explanation. Answers send no `max_tokens` by default (`MaxOutputTokens` is null), so the provider's own cap applies, and a reasoning model also spends that cap on hidden reasoning. The stream ends with finish_reason=length mid-sentence, or with empty content. The answer is saved as Complete with no "incomplete" mark or reason. It is then sent as full context for follow-ups, can be branched from, and is summarized as if it were the whole explanation. Requirements §6.2 says an incomplete answer keeps an incomplete mark, and §9.1 says interrupted responses need a clear status.

**Recommendation.** Carry `Truncated` into the terminal state: either a distinct state or error kind such as "reached output limit", or Complete plus a persisted flag. Show a notice with "regenerate" on the answer. Treat an empty truncated answer as failed. Add a FakeChatClient script for a length-finished answer, with flow and UI tests.

**Skeptic: partially confirmed, medium.** The core claim holds: I could not find any guard, test or recorded decision that makes it impossible or intended.

**What the code does**
- The client reports truncation: `Completed(Truncated: reason == "length")`.
- `AnswerRunner.RunAsync` matches `case ChatStreamEvent.Completed: completed = true;` without reading the flag, then sets `final = MessageState.Complete`.
- `FinishAsync` persists only state, error and detail, so truncation is never stored on the message.
- The UI state label has no text for Complete, and `IsProblem` covers only Failed and Interrupted, so nothing is shown on the answer.
- `CountsAsContext` accepts a Complete answer with non-empty text, so a cut-off answer feeds later turns, branches and summaries.
- The only record of truncation is the diagnostic `ai_request` outcome, set by `RequestTrace`.

**Why it is not intended**
- Every other request kind rejects truncated output: `GenerationRequests.cs:120` ("输出达到上限被截断，未保存。") and `ContextCompressor.cs:197`.
- D-015 and the TRACK.md findings of 2026-10-26 and 2026-10-27 record that reasoning models hit `length` with empty or cut content on real requests. Those findings cover internal requests only.
- Nothing in TRACK.md or `docs/design/decisions.md` accepts truncated user answers as Complete.
- No test covers it. `Completed(true)` appears in tests only in the client parser test, and the fake client has a truncated script only for context summaries.

**Why I lowered the severity to medium**
1. Answers send no `max_tokens`: the stored value is only ever carried over from `existing?.MaxOutputTokens`, and nothi … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity is overstated: medium, not high. With no `max_tokens` sent for answers, only the provider's default cap triggers this, and the text received is kept intact with regenerate available.
- "Sent as full context" is not true for the empty-content case: `CountsAsContext` excludes whitespace-only answers. The empty case is a blank Complete answer with no explanation, which is a display problem only.
- Requirements §6.2 and §9.1 do not name output-limit truncation. The conflict is with their intent and with the D-015 principle, not their literal text.
- "No test covers a truncated answer" is right at flow and UI level, but the client parser does test `finish_reason=length` (`ChatCompletionsClientTests.cs:114`).
- The claim that the rejection of truncated output for titles and naming falls under D-015 is loosely attributed: D-015 covers context summaries; the others follow the same rul … (cut; full text in the workflow journal)

**Reproduction attempt: confirmed, high.** Reproduced with a flow test through the real coordinator, AnswerRunner and SQLite stores. A chat client that ends an answer stream with Completed(Truncated: true) (what ChatCompletionsClient emits for finish_reason=length) produces a message row `complete|NULL|NULL` and a `completed` task. The only trace of truncation is ai_request.outcome = 'truncated'.

Both claimed variants hold:
- Cut mid-sentence: the partial text is saved as Complete, CountsAsContext is true, and the follow-up request sends it as the assistant turn verbatim.
- Empty content (the reasoning-model case already recorded in TRACK.md 2026-10-26/27 for internal requests): saved as Complete with empty text and no error kind.

Code reading agrees. AnswerRunner's `case ChatStreamEvent.Completed: completed = true;` never reads the flag. UiText.AnswerState returns null for Complete, so no label is shown and the action reads "regenerate" as for a normal answer. The other consumers of the flag (GenerationRequests.cs:120, ContextCompressor.cs:197) reject truncated output per D-015, so answers are the one path that ignores it.

The precondition is realistic: max_tokens is sent only when MaxOutputTokens is set, and ModelSettingsService carries over `existing?.MaxOutputTokens`, which is null for a fresh configuration, so the provider's default cap applies.

Requirements §6.2 line 139 ("不完整的回答保留不完整标记") and §9.1 line 252 (clear status for interrupted responses) support the conformance claim. High is justified at the stated production bar: silent, persisted, propagates into context, branches and summaries, and the empty v … (cut; full text in the workflow journal)

*Corrections to the finding.* Nothing material is wrong.
- "No test covers a truncated answer" is accurate, and FakeChatClient cannot script one at all (answers always end with Completed(false)); the repro needed a wrapping IChatClient.
- "Titles ... reject truncated output": there is also a title test (ConversationFlowTests.A_title_cut_off_at_the_output_limit_is_not_used) that uses RequestScript.TruncateNext rather than the word "Truncated", so the grep-based statement slightly undercounts truncation coverage for internal requests. This does not affect the finding.
- "can be branched from" and "is summarized as if it were the whole explanation" follow from the Complete state but were not exercised by my test.
- The empty-content variant is stored as Complete with empty text; CountsAsContext excludes it from later context (blank text), so that variant's harm is a blank "complete" answer with no reason shown, not cont … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Temporary test file tests/Mindweave.Infrastructure.Tests/Chat/ZzTruncatedAnswerReproTests.cs (deleted afterwards). Core of it:

```csharp
private sealed class LengthLimitedClient(FakeChatClient inner, string cutText) : IChatClient
{
    public int Remaining { get; set; } = 1;
    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (FakeChatClient.IsAnswer(request) && Remaining > 0)
        {
            Remaining--;
            inner.Requests.Enqueue(request);
            await Task.Yield();
            if (cutText.Length > 0) yield return new ChatStreamEvent.TextDelta(cutText);
            yield return new ChatStreamEvent.Completed(Truncated: true);
            yield break;
        }
        await foreach (var item in inner.StreamAsync(request, apiKey, cancellationToken)) yield return item;
    }
    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken ct) => inner.ListModelsAsync(baseUrl, apiKey, ct);
}

[Fact]
public async Task Repro_answer_cut_off_at_output_limit_is_stored_as_complete_and_reused_as_context()
{
    const string Cut = "蒸汽机车的发明始于十八世纪，瓦特改良的";
    var fake = new FakeChatClient();
    await using var h = await ChatHarness.StartAsync(client: new LengthLimitedClient(fake, Cut));
    var sent = await h.Conversations.StartAsync(h.Workspace, "详细讲讲火车的历史", Ct);
    var answer = await h.WaitForAnswerAsy … (cut; full text in the workflow journal)
```

</details>

### requirements-2. Manual "mark unread" on a summary is undone by the next progress report

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Pages/SummaryViewModel.cs:176`; effort S

**Evidence.** `ToggleReadAsync` calls `SetReadAsync(read: false)`, which only sets `read_source` to NULL. The page keeps `_reportedProgress` and `_shownSince` because `Show()` resets them only when the shown version id changes. On the next `ReportProgressAsync`, `ShownIsRead` is false and `_reportedProgress >= 0.9`, so `RecordProgressAsync` runs with the elapsed visible time (already over 8 s) and `markRead` true. The store then applies `read_source = coalesce(read_source, excluded.read_source)` (`SqliteSummaryStore.cs:517`), which turns NULL back into 'progress'. `SummaryView` reports on every `ScrollChanged` (300 ms timer) and in `OnDetachedFromVisualTree`. The only test, `SummaryUiTests` lines 57-59, asserts unread immediately after the toggle and then leaves the page without checking again. `SummaryFlowTests:271` likewise stops after the toggle.

**Failure scenario.** The user reads a summary to the end, so it becomes read. They click the read switch to mark it unread as a "come back later" flag. Any scroll movement afterwards, and probably simply leaving the page (the view reports once more on detach), marks the version read again. The network node and list show it as read. Requirements §7.1 and the §11 scenario say the switch can change it back to unread; in practice that does not stick for the version just read.

**Recommendation.** After a manual unread, suppress progress-based marking for that opening: reset `_reportedProgress` and `_shownSince` in the view model, or better, store a "manually unread at" marker so only progress recorded in a later opening can mark it read. Add a UI test: read, toggle unread, scroll and navigate away, assert the node is still unread.

**Skeptic: confirmed, medium.** I could not refute this by reading the code; every link in the claimed chain exists and no guard breaks it. Nothing was executed (read-only audit), so this is confirmed by code trace, not by a failing test.

The chain:
1. The toggle calls `SetReadAsync(read: false)`, which the service maps to a NULL read source, and the store overwrites `read_source` with NULL.
2. The page reloads and calls `Show()` with the same version id. `Show()` resets `_shownSince`, `_reportedProgress` and `_readCheckScheduled` only when the id changes, so all three survive the toggle; only `ShownIsRead` becomes false.
3. On the next `ReportProgressAsync`, the early-return guard is false (not read, progress already at or above 0.9), so it falls through with the elapsed time since the version was first shown.
4. The service computes `markRead` purely from progress >= 0.9 and visible >= 8 s, with no knowledge of the manual unread.
5. The store's upsert uses `read_source = coalesce(read_source, excluded.read_source)`, so NULL becomes 'progress' and the version is read again.

Triggers: any `ScrollChanged` starts the 300 ms report timer, and `ScrollChanged` also fires on extent or viewport changes, not only user scrolling. The view also calls `Report()` in `OnDetachedFromVisualTree`.

No design decision makes this intended. Requirements §7.1 (line 182) says the switch can change it back to unread, the §11 scenario (line 365) repeats it, and decisions.md line 71 says the manual switch can be changed at any time. TRACK.md does not list it as a known finding. No test covers progress after a manual unread: `S … (cut; full text in the workflow journal)

*Corrections to the finding.* Two refinements; the core claim is accurate.

- The leave-the-page trigger is not established. The finding itself says "probably". `ReportProgressAsync` returns early when `_disposed` is set (SummaryViewModel.cs:170), and I did not check whether navigation disposes the view model before the view detaches. The scroll trigger is certain; `ScrollChanged` on extent or viewport change (window resize, marker text reflow) is also a trigger.
- The recommendation should cover the scheduled re-check as well. If the toggle to unread happens while `_readCheckScheduled` is pending, the delayed callback at lines 192-201 calls `ReportProgressAsync` itself and can re-mark the version. The fix should reset or guard that flag along with `_reportedProgress` and `_shownSince`.

Separately, and by design rather than a defect: a short summary that fits on one screen is marked read again 8 s into any later ope … (cut; full text in the workflow journal)

### requirements-3. The first question typed on the new-exploration page is not persisted and is easily lost

- Final: **medium** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/NewConversationViewModel.cs:20`; effort M

**Evidence.** `NewConversationViewModel` builds a `Composer` with no draft store behind it. `docs/design/s2-branching.md:66` states that the new-conversation page's draft lives only in the page instance because the conversation does not exist yet. `NewConversationRoute` is not an `IRestorableRoute`, so it is not restored after restart. `Workbench.PushCore` creates a new page instance unless the same route is already on top, and `GoBack`/`Replace` dispose the instance. The exit sequence (`ExitCoordinator.SaveEverythingAsync`) flushes only conversation and summary drafts. This deviation appears only in the design document, not in TRACK.md's pending decisions or findings.

**Failure scenario.** The user types a long opening question on "new exploration" and has not sent it, for example because the model is not configured yet. (a) They quit or the app crashes: on restart the text is gone, though §5.1, §9.3 and the §11 "close and reopen" scenario say unsent drafts are restored. (b) They click another conversation in the sidebar and then "new exploration" again: a fresh empty page is pushed and the text sits in a buried instance reachable only by pressing Back, though §6.3 says switching does not discard unsent text. (c) They press Back: the instance is disposed and the text is gone.

**Recommendation.** Persist one "new root conversation" draft per workspace and viewer slot (for example a draft row keyed by workspace, or an `app_state` entry) through `ReadingStateService`. Load it in `NewConversationViewModel`, clear it when `StartAsync` is accepted, and include it in the exit flush. Reuse the existing new-exploration page instead of stacking a second one. Add restart and navigation tests.

**Skeptic: partially confirmed, medium.** I could not refute the core claim: the new-exploration draft has no persistence, and text typed there is lost on quit, crash or Back. Every cited code fact checks out, and no guard, test or recorded decision covers it. The finding overstates how easily the text is lost during a session, so I mark it partially confirmed and keep medium.

What holds:
- **No draft store.** `NewConversationViewModel` builds `new Composer(send)` with only a send delegate. `Composer.Text` is a plain observable property with no service behind it. The page receives `IConversations` only, not `ReadingStateService`.
- **Not restored after restart.** `NewConversationRoute` does not implement `IRestorableRoute`, so `Workbench.Changed` and `FlushAsync` save a null content key, and a restart lands on the section home.
- **Not in the exit flush.** `ExitCoordinator.SaveEverythingAsync` flushes the workbench location, summary drafts and `reading.FlushAsync()` (conversation drafts). None of these can see the new-page text, and there is no exit prompt for it.
- **Back discards it.** `GoBack` removes the top page from the stack. The view model is not `IDisposable`, so nothing is literally disposed, but the instance becomes unreachable and the text is gone.
- **Deliberate but unrecorded.** `docs/design/s2-branching.md:66` states the new-page draft lives only in the page instance. A grep for 草稿 in `docs/design/decisions.md` and the TRACK.md pending-decisions and findings sections shows no entry for it as a deviation.
- **Requirements.** §9.3 (line 263) says restart restores "the last valid page, reading position … (cut; full text in the workflow journal)

*Corrections to the finding.* - Scenario (b) is not data loss. The earlier page instance stays in the back stack with its text; the user sees a fresh empty page and must press Back twice to find it. Describe it as a discoverability problem.
- "`GoBack`/`Replace` dispose the instance" is imprecise. `NewConversationViewModel` is not `IDisposable`, so `Release` is a no-op; the text is lost because the instance leaves the stack. `Replace` is only reached on an accepted send, where clearing is intended.
- "Easily lost" is overstated for in-session use. Switching section (including the composer's own "go to settings" link), switching workspace, and clicking "new exploration" while that page is already on top all keep the text. Actual loss needs quit, crash, Back, or in-app restore.
- The requirement breach is clearest against §9.3 and the §11 close-and-reopen scenario. §5.1 and §6.3 are worded around existing conversations … (cut; full text in the workflow journal)

### requirements-4. Summaries queue at the same priority as user questions, contrary to §9.2 and architecture §6

- Final: **low** (confirmed); auditor said medium
- Where: `src/Mindweave.Application/Conversations/GenerationWork.cs:69`; effort S

**Evidence.** `SummaryWork` is declared with `GenerationPriority.User` (line 69), the same queue as `AnswerWork` (line 37). `GenerationCoordinator.Pump` takes `_userQueue` first-in first-out. Requirements §9.2 and `architecture.md:196` both say user questions take priority over automatic titles and summaries. The track's own design documents disagree with each other: `s1-chat.md:44` puts summaries in the background tier from S4, while `s4-summaries.md:22` puts summary tasks at user priority. No entry in `decisions.md` or TRACK.md records the deviation. `SchedulerTests` cover questions ahead of titles; no test covers a question against a queued summary.

**Failure scenario.** The user starts summaries in three conversations: two run and one is queued. Summaries allow 16,384 output tokens and can run for minutes on reasoning models. The user then asks a question in a fourth conversation. It queues behind the third summary instead of taking the next free slot, so the answer waits for a whole extra summary.

**Recommendation.** Either give `SummaryWork` its own tier between questions and titles (dequeue answers first, then summaries, then background), or record the deviation as a decision and raise it on `main` to correct §9.2. Add a scheduler test: with both slots busy, a question enqueued after a queued summary starts first.

**Skeptic: confirmed, low.** Every factual claim checks out. SummaryWork is declared with GenerationPriority.User, the same as AnswerWork; Enqueue appends to QueueFor(Priority), and Pump takes the head of _userQueue before anything else, so answers and summaries are strictly FIFO with each other. Requirements §9.2 (line 256) and architecture §6 (line 196) both say user questions take priority over automatic titles and summaries/summary tasks. The track's design docs contradict each other (s1-chat.md:44 puts summaries in the background tier from S4; s4-summaries.md:22 puts them at user priority). I found no entry in decisions.md or TRACK.md recording the deviation (grep for priority/queueing of summaries in both returned nothing relevant). SchedulerTests has three tests: answer-before-title, slot limit, and stopping a queued summary; none pits a question against a queued summary. I could not find a guard: nothing reorders the user queue or reserves a slot for answers.

Mitigating points that lower severity rather than refute:
1. The choice looks deliberate, not accidental: s4-summaries.md:22 states it explicitly, and requirements §238 / architecture §322 classify summaries as user-initiated work alongside answers (must finish or be stopped before delete/archive), unlike system-initiated titles. So the code follows the S4 design; the gap is that the design departs from the baseline wording without a recorded decision or a correction raised on main, which AGENTS.md requires.
2. The user-visible impact is narrow. The extra wait occurs only when both slots are busy AND at least one summary is already queued … (cut; full text in the workflow journal)

*Corrections to the finding.* Severity is overstated: medium -> low. The scenario needs both slots busy plus a summary already queued ahead of the question, and queued summaries can be cancelled. The finding should also note that the user-priority placement is an explicit S4 design statement (s4-summaries.md:22) consistent with the docs treating summaries as user-initiated work, so the defect is primarily an unrecorded deviation from requirements §9.2 / architecture §6 and a stale s1-chat.md:44, not an implementation slip. Line reference: SummaryWork's priority is on line 69 (declaration starts at 68), as cited.

### requirements-5. Older page instances of the same conversation show a stale input box and can overwrite the newer draft

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/ConversationViewModel.cs:333`; effort S

**Evidence.** `Composer.Text` is loaded from `ReadingStateService` once, in `LoadFirstAsync` (line 333). Nothing updates it afterwards; `IReadingState` has no change event. `Workbench.PushCore` creates a new page whenever the top route differs, so sidebar switches and "return to source" (always a new page, `Workbench.Navigate` comment) leave earlier instances of the same conversation in the 50-page back stack. Tests cover draft restore into a new instance (`BranchUiTests:135-143`) but not going Back to an older one.

**Failure scenario.** The stack is [home, P(A), child C]. The user returns to source, which pushes a new page P(B), and types "abc" there, so the stored draft is "abc". Pressing Back twice lands on instance A, whose input box still shows what it had when A was created (empty). The text looks lost, and typing in A overwrites the stored "abc". The reverse also happens: text typed in A and later sent from B is still shown in A after Back, inviting a duplicate send. §6.3 says returning and switching do not discard unsent text.

**Recommendation.** Refresh the composer from `ReadingStateService` whenever a page becomes the shown page (the view already sets `IsShown`), or have the service raise a per-(conversation, slot) draft-changed event that other instances apply. Alternatively reuse an existing page for a plain navigation to the same conversation. Add a Back-to-older-instance test.

### requirements-6. The context planner can summarize the most recent exchange before older ancestor background

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Core/Context/ContextPlanner.cs:87`; effort S

**Evidence.** Line 87 summarizes all closed chunks of the whole chain first, then tails: `chunks.Where(c => c.Closed).Concat(chunks.Where(c => !c.Closed))`. `Chunks()` marks a conversation's last chunk closed whenever its size reaches `ChunkTokens` (4,000): `Chunk(conversation, current, closed: size >= ChunkTokens)`. `Required()` protects only the current question, the branch's quoted first question, and the parent source answer with its question. The previous exchange in the current conversation is not protected. `ContextPlannerTests` has `The_oldest_runs_are_summarized_first_and_recent_turns_stay_raw` but no case with ancestor tails.

**Failure scenario.** A branch conversation's previous answer is long (4,000 or more estimated tokens, roughly 4,000 Chinese characters), and the ancestors hold a few thousand tokens of older background in unclosed tail chunks. When the budget is tight (small-context model or long chain), the planner replaces the immediately preceding exchange with a summary while older ancestor tails are still sent raw. A follow-up like "explain that second step" then refers to text the model no longer has. §9.6 and architecture §6.1 say recent exchanges are kept and earlier ancestor background is compressed first.

**Recommendation.** Order candidates by distance from the current question: ancestors before the conversation itself, and never the conversation's newest chunk until everything else (ancestor tails included) is summarized. Or add the last complete exchange to `Required`. Add a planner test with an ancestor tail and a large final own exchange.

### requirements-7. "One unfinished summary task per conversation" is checked only before the write, not inside it

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Summaries/SummaryService.cs:161`; effort S

**Evidence.** `SummarizeAsync` reads `LatestTaskAsync` and the node's pending candidate (lines 153-164), then awaits model, budget and planning, and only then calls `_conversations.AddTaskAsync(task)`. `SqliteConversationStore.AddTaskAsync` checks only `RequireOpenForWork` (existence and archive). It does not re-check for a queued or running summary task, a pending candidate, or an unfinished answer. Answers, by contrast, are guarded in-transaction (`ThrowIfUnfinished`, `CanRetry`). There are two entry points to summarize the same conversation: the page's summary panel and the sidebar row menu (TRACK item 2).

**Failure scenario.** Two summarize requests for the same conversation overlap, for example the row-menu command and the page button, or a repeated command while the first is still in its awaits. Both pass the read checks and both insert a summary task. Two paid summary requests then run. The second ends as an unrequested candidate on a first summary, or fails with a "storage unavailable" error kind because `SaveGeneratedAsync` throws HasCandidate. §8.1 allows at most one unfinished summary task per conversation.

**Recommendation.** Enforce the rule in the insert transaction: refuse when a summary task of the conversation is queued or running or the node has a pending version, or add a partial unique index on `generation_task(conversation_id)` where kind is 'summary' and state is queued or running. Map the refusal to `SummaryInProgress` or `HasCandidate`. Add a concurrent-call test.

### requirements-8. A failed naming enqueue leaves the same-name group permanently marked as queued

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Summaries/SummaryNaming.cs:151`; effort S

**Evidence.** `ScheduleNamingAsync` adds the group key with `_namingQueued.TryAdd((detail.Node.Workspace, key), 0)` (line 151) and then awaits `_conversations.AddTaskAsync(task, ...)`. The key is removed only in the work's `Apply` finally block or in `OnTaskFinished` via `_namingTasks`, and `_namingTasks[task.Id]` is set only after `AddTaskAsync` succeeds. If `AddTaskAsync` throws (StorageWriteException, or ConversationMissingException when the node's source was just deleted), `ScheduleNamingSafelyAsync` logs and swallows the exception and the key stays.

**Failure scenario.** A storage hiccup or a concurrent delete hits at the moment a second summary with the same base title is saved. Naming for that title group is never scheduled again in this process: later summaries with the same title hit `TryAdd` returning false. The nodes keep identical names until restart, so the §7.2 same-name disambiguation silently stops for that group.

**Recommendation.** Wrap the section after `TryAdd` in try/catch and remove the key when the task row was not stored and enqueued. Add a test with a failing `AddTaskAsync` followed by a later successful schedule.

### requirements-9. The real platform credential stores are only tested on opt-in

- Final: **info** (not verified); auditor said info; already known in TRACK.md
- Where: `tests/Mindweave.Infrastructure.Tests/Chat/PlatformCredentialStoreTests.cs:14`; effort S

**Evidence.** `Assert.SkipUnless(Environment.GetEnvironmentVariable("MINDWEAVE_CREDENTIAL_TESTS") == "1", ...)`. The default run and CI use `MemoryCredentialStore`. TRACK (2026-10-26 and 2026-11-12) notes that Windows Credential Manager cannot be exercised over SSH and that the opt-in run needs a desktop session.

**Failure scenario.** A regression in `MacKeychainCredentialStore` or `WindowsCredentialStore` (for example the error mapping for a locked keychain) would not be caught by the default run. The §11 scenario "credential service unavailable: no plaintext fallback, clear failure" is verified against the fake only.

**Recommendation.** Run the opt-in credential tests on a schedule on both desktop machines (the Windows tester role can do it) and record the last passing commit in the stage reports.

