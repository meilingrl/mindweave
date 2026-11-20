# GenerationCoordinator.cs: responsibility map and split plan

Read-only analysis; nothing was edited or run. The class mixes three things:

- **The scheduler core.** It is cohesive, it is the only code that holds `_gate`, and it should stay as the coordinator.
- **Two components that share no state with the scheduler.** These are the kept terminal writes and the context-budget calibration. Both can move out with no public API change.
- **About 350 lines of per-kind request runners.** They never touch `_gate`, `_queues` or `_running`; they only need injected services, the write helper and a way to raise the coordinator's events.

Splitting along those lines takes the coordinator from 1186 lines to about 420. The constructor signature, the public members and the events can all stay the same, so `ChatHarness`, `TestHost` and `CompositionRoot` do not change for most steps.

The rest of the user's request (other long source files, and test code) was outside this step's scope. I only looked at the tests that protect the coordinator.

## 1. Member → responsibility map

| # | Responsibility | Lines | Members | State used |
|---|---|---|---|---|
| A | Work types and options | 13–78 | `GenerationOptions`, `GenerationPriority`, `GenerationWork`, `AnswerWork`, `TitleWork`, `SummaryWork`, `NamingWork`, `AnswerOutcome`, `UnfinishedUserWork`, `SummarySaveResult` | none |
| B | Fields, constructor, events | 82–161 | constructor (registers the retry at 132; `health.Changed` → `Pump` at 133–138); events `Changed`, `AnswerEnded`, `AnswerFinished`, `TitleSaved`, `TitleAbandoned`, `SummaryTaskFinished` | all |
| C | Queue queries | 163–261 | `UnfinishedCount`, `UnfinishedAnswerCount`, `UnfinishedUserWork`, `IsClosed`, `Observe`, `HasUnfinishedAnswer`, `AnswerWritesSettledAsync`, `HasUnfinishedSummary`, `IsLive` | `_gate`, `_userQueue`, `_backgroundQueue`, `_running`, `_closed`, `_live` |
| D | Admission and scheduling | 264–285, 488–565 | `Enqueue`, `RemoveQueued`, `Pump` (slots plus `_health.TryAdmit`), `RunAsync` (type switch, crash guard, slot release), `Running` (1133–1140) | the same `_gate` set |
| E | Stop, cancel and exit lifecycle | 287–486 | `StopAsync`, `StopTaskAsync`, `CancelBackgroundAsync`, `StopForExitAsync`, `Reopen`, `SaveUnsavedAsync`, `ShutdownAsync`, `DisposeAsync` | `_gate` set, `_closed`, `_shutDown`; calls F and H |
| F | Answer runner | 567–736 | `RunAnswerAsync` (mark running, key, compression, admission check, stream, thinking, trace), `FinishAnswerAsync`, `CancelQueuedAsync`, `CheckpointLoopAsync` | services only, plus `_options.CheckpointInterval` and events |
| G | Title, summary and naming runners | 738–910 | `RunTitleAsync`, `RunSummaryAsync`, `RunNamingAsync` | services only, plus events |
| H | Request plumbing | 912–960, 986–1020 | `RequestCompleteTextAsync`, `RecordAsync`, `ReadKeyAsync` (the only `ICredentialStore` use), `FinishTask` | `_client`, `_credentials`, `_requests`, `_clock`, `_health` |
| I | Budget calibration (public API) | 962–985 | `BudgetForAsync`, `LogCalibrationFailed` | `_requests`, `_logger` only |
| J | Kept terminal writes, storage obligations, retry | 98–107, 1022–1129 | `SaveResult`, `SaveAsync`, `FlushUnsavedAsync`, `RetryAsync`, `KeptWrite`, `_unsaved`, `_unsavedDefects`, `_flushGate` | its own queues and semaphore, plus `_health`; never `_gate` |
| K | Logging and exception | 1142–1186 | 7 `LoggerMessage` methods; `StorageAdmissionRefusedException` | none |

**What must stay together.** Groups C, D and E share `_gate`, `_userQueue`, `_backgroundQueue`, `_running`, `_closed` and `_live`. They also rely on two ordering rules that only hold inside one class:

- `Running.Completion` is assigned under the lock in `Pump`. `AnswerWritesSettledAsync`, `StopAsync` and exit all wait on it.
- `IsLive` reads `LiveAnswer` state. It has to agree with `FinishAnswerAsync`, which calls `live.Finish` before raising `AnswerEnded`.

These stay as the coordinator's core, about 420 lines.

**What moves cleanly.**

- **J** has separate state. Its only link to the core is that both F/G and E call `SaveAsync`, and E calls `FlushUnsavedAsync`.
- **I** uses only `_requests`.
- **F, G and H** never lock `_gate`. Their links to the coordinator are `RaiseChanged()` and the five events (the sender must stay the coordinator), plus `SaveAsync`, `BudgetForAsync` and `_compressor`.

## 2. Evidence: how the parts change

The file has 21 commits. Grouped by the region each commit touched:

| Region | Commits | What changed |
|---|---|---|
| Runners (F/G) | `3053822` | one 156-line hunk for summaries and naming |
| | `07fd3a1` | budget calibration; touched the answer runner (578, 613), the summary runner (775, 892) and added `BudgetForAsync` (931) |
| | `acb09eb` | thinking state (625) |
| | `624c2da`, `7fac46f`, `1c04bd1` | runner changes |
| Lifecycle and queries (C/E) | `9853111` | exit sequence, about 115 lines |
| | `6026358`, `6af5c6c`, `929d1ff` | lifecycle and query changes |
| | `2db60d6` | settle query at 232, `AnswerEnded` at 144, finish at 695: one feature spanning queries, events and the answer runner |
| Kept writes (J) | `7fac46f`, `624c2da`, `6026358`, `1d1e4f0`, `02fc23c` | storage-fault work |

`02fc23c` ("move storage health out of the generation coordinator") already removed 158 lines. It is the precedent for this kind of extraction.

So features land on one axis at a time: a new request kind or request behaviour touches F/G/H; exit and archive features touch C/E; storage faults touch J. Review diffs mix the three only because they share one file. `07fd3a1` shows the budget concern leaking into two runners and into the public API.

**Callers use narrow slices of the public surface:**

| Caller | Members used |
|---|---|
| `ConversationService` | `Enqueue`, `Observe`, `StopAsync`, `HasUnfinishedAnswer`, `AnswerWritesSettledAsync`, `IsClosed`, `CancelBackgroundAsync`, `BudgetForAsync` ×4, `Changed`, `AnswerFinished`, `TitleSaved`, `TitleAbandoned` |
| `SummaryService` | `Enqueue`, `StopTaskAsync`, `HasUnfinishedAnswer`, `BudgetForAsync`, `SummaryTaskFinished` |
| `ConversationMarksService` | `AnswerEnded`, `HasUnfinishedSummary` |
| `ConversationDeletion`, `WorkspaceDeletion` | `CancelBackgroundAsync` |
| Exit flow | through `IGenerationExit` |
| `AppHost` | `ShutdownAsync` |

`ContextCompressor` does not reference the coordinator; it receives `RecordAsync` and `TryAdmit` as delegates.

The coordinator is constructed in three places: `ChatHarness.cs:35`, `TestHost/Program.cs:129` (without a compressor) and DI (`CompositionRoot.cs:55,72`). Desktop tests resolve it from DI (`ExitTests`, `SidebarStatusTests`, `DesktopHarness`).

## 3. Risks today

1. **Kept writes (J) are the most concurrency-sensitive code.** They involve obligations, a flush semaphore and moving entries between two queues. That logic is interleaved with runner code in one class, so a reviewer cannot see J's invariants in isolation, and it can only be tested through full chat flows.
2. **The runners repeat the same skeleton.** Title, summary and naming each do mark running → read key → request → finish, then the same 3–4 catch blocks. The compression prelude is copied in two places (598–608 and 803–809), and the copies merge turns differently: the answer replaces all turns, the summary keeps the leading system turns. Any change to failure classification has to be made 3–4 times.
3. **The cancel-a-queued-task code is copied three times** (`StopTaskAsync` 327–331, `CancelBackgroundAsync` 361–371, `StopForExitAsync` 406–418), and the copies already differ:
   - `StopTaskAsync` always raises `SummaryTaskFinished`, even if the task is a `TitleWork`. The other two raise `TitleAbandoned` for titles.
   - This cannot happen today because only `SummaryService` calls `StopTaskAsync`, but it is a latent inconsistency.
4. **Budget calibration sits on the coordinator's public API.** That means `ConversationService` and `SummaryService` depend on the scheduler just to compute a budget.
5. **One stale doc reference.** `SaveAsync`'s doc comment at about line 1030 points to `<see cref="IsStorageBlocked"/>`, which no longer exists in Application. It is left over from `02fc23c`.
6. **Test gap.** No test checks the scheduler's core rules directly: user work runs before background work, and no more than `Slots` tasks run at once. They are only covered indirectly.

## 4. Target structure

All files stay in `src/Mindweave.Application/Conversations/` and namespace `Mindweave.Application.Conversations`, so no `using` churn.

| File | Visibility | Contents | Approx. lines |
|---|---|---|---|
| `GenerationWork.cs` | public | Group A, moved verbatim | 75 |
| `StorageAdmissionRefusedException.cs` | internal | moved verbatim | 20 |
| `TerminalWrites.cs` | internal sealed | `SaveResult`, `SaveAsync(write, retryOnFailure)`, `FlushAsync(includeDefects)`, `RetryAsync`, `KeptWrite`, both queues, `_flushGate`, `LogWriteDefect`. Its constructor registers `health.RegisterRetry(_ => FlushAsync())`. | 130 |
| `ContextBudgets.cs` | public sealed | `BudgetForAsync(ModelSnapshot, CancellationToken)`, `LogCalibrationFailed`. Depends on `IAiRequestStore` and a logger. | 40 |
| `GenerationRequests.cs` | internal sealed | `ReadKeyAsync`, `RequestCompleteTextAsync`, `RecordAsync`, `FinishTaskAsync` (wraps `FinishTask` through `TerminalWrites`), and `PrepareCompressedAsync(task, plan, budget, key, stop)`, the shared compression prelude that returns `PreparedContext`; each caller keeps its own turn-merge rule | 140 |
| `IGenerationEvents.cs` | internal | Notifier interface: `Changed()`, `AnswerEnded(o)`, `AnswerFinished(o)`, `TitleSaved(id)`, `TitleAbandoned(id)`, `SummaryTaskFinished(t)`. The coordinator implements it explicitly, so every event keeps `this` as sender and keeps its raising thread. | 20 |
| `AnswerRunner.cs` | internal sealed | `RunAsync`, `FinishAsync`, `CancelQueuedAsync`, `CheckpointLoopAsync`, `LogAnswerFinished` | 180 |
| `TaskRunners.cs` | internal sealed | `RunTitleAsync`, `RunSummaryAsync`, `RunNamingAsync`, `LogSummaryNotSaved`. Optionally one private template for the shared skeleton, with the title's differences kept explicit: mark-running is outside the try, stop raises `TitleAbandoned`, and there is no `SummaryTaskFinished` finally. | 170 |
| `GenerationCoordinator.cs` | public | Groups B–E: queues, `Pump`, `RunAsync` dispatch, queries, lifecycle, events. Plus one private `CancelQueuedTaskAsync(GenerationWork)` used by all three lifecycle paths. | ~420 |

**Ownership.** The coordinator creates `TerminalWrites`, `ContextBudgets`, `GenerationRequests` and both runners inside its existing constructor. It passes them the same `ILogger<GenerationCoordinator>` instance, so log categories and messages do not change.

**What stays on the coordinator.**

- The public surface does not change, including `BudgetForAsync` as a one-line forwarder at first.
- `IGenerationExit` does not change.
- `SaveUnsavedAsync` and `ShutdownAsync` call `_writes.FlushAsync(includeDefects: true)`.
- `_live` registration stays in `Enqueue`.
- `Running.Completion` stays assigned under `_gate` in `Pump`.
- `RunAsync`'s `finally` (slot release, `Pump`, `RaiseChanged`) stays in the coordinator, so `AnswerWritesSettledAsync` still covers the terminal write.

**Architecture test.** `SeamTests.Only_the_request_paths_read_credentials` is an allow-list of file names. Moving `ReadKeyAsync` requires replacing `"GenerationCoordinator.cs"` with `"GenerationRequests.cs"` in that list. The test's own comment calls for exactly this kind of deliberate edit. The runners must not reference `ICredentialStore`.

## 5. Migration order

Every step is behaviour-preserving and should end with the full suite green. Each row lists the tests that protect it.

| Step | Change | Priority / effort | Protecting tests |
|---|---|---|---|
| 0 | Add characterization tests: (a) with `Slots = 1`, a queued `TitleWork` and then an `AnswerWork` start answer-first; (b) no more than `Slots` streams are open at once; (c) `StopTaskAsync` on a queued summary raises `SummaryTaskFinished` once. Use `FakeChatClient` gating through `ChatHarness`. | high / S | new tests |
| 1 | Move group A to `GenerationWork.cs` and the exception to its own file. Pure move. Also fix the stale `IsStorageBlocked` reference. | medium / S | compile plus the full suite |
| 2 | Extract `TerminalWrites`. Move the queues, semaphore, `SaveAsync`, flush, retry and `KeptWrite`. The coordinator keeps a `_writes` field, and the `RegisterRetry` call moves into the `TerminalWrites` constructor, which is still called from the coordinator constructor before health starts. `DisposeAsync` disposes it. | **high / S–M** | `ExitStopTests` (`Saving_unsaved_writes_waits_for_a_retry_already_writing…`, `Failed_terminal_writes_are_reported_until_they_are_saved`), `StorageRecoveryTests` (`Content_kept_during_a_pass…`, `Reports_kept_writes_and_retries_racing…`, `Retries_are_registered_before_start_only`, `An_accepted_answer_waits_while_storage_is_blocked…`), `Faults/DiskFullTests`, `BusyWriterTests`, `TransactionKillTests`, `StoreGuardTests` |
| 3 | Extract `ContextBudgets`, created inside the coordinator; `BudgetForAsync` forwards to it. Optional step 3b: register it in DI and inject it into `ConversationService` and `SummaryService`, then delete the forwarder. This changes constructors at `ChatHarness.cs:47,51` and in `CompositionRoot`. | medium / S (3b: S–M) | `TokenCalibrationFlowTests`, `ContextCompressionTests`, `Core.Tests/TokenCalibrationTests` |
| 4 | Extract `GenerationRequests` (key, request, trace, finish, shared compression prelude) and update the `SeamTests` allow-list. | high / M | `RequestTraceTests`, `ThinkingPhaseTests.Titles_knowledge_summaries_and_traces_ignore_reasoning`, `StreamDropTests`, `ModelSettingsTests` (missing or unavailable key), `ContextCompressionTests`, `SeamTests` |
| 5 | Add `IGenerationEvents` (explicit implementation on the coordinator) and move the answer runner to `AnswerRunner`. Lifecycle's `StopAsync` and `StopForExitAsync` call `_answers.CancelQueuedAsync`. | high / M | `ConversationFlowTests`, `ThinkingPhaseTests`, `KillDuringStreamTests`, `StreamDropTests`, `ConversationMarksTests` (`AnswerEnded` ordering), `ExitStopTests.A_finished_answer_still_holding_its_slot…`, desktop `SidebarStatusTests` and `ExitTests` |
| 6 | Move title, summary and naming to `TaskRunners`. First as a verbatim move; then, in a separate commit, introduce the shared skeleton template. | medium / M | `SummaryFlowTests`, `ExitStopTests` (title-after-exit tests), `ArchiveFlowTests`, `ConversationDeleteFlowTests.Background_titles_in_the_scope_are_stopped…`, `WorkspaceDeleteFlowTests` |
| 7 | Inside the coordinator, merge the three cancel-a-queued-task copies into `CancelQueuedTaskAsync(GenerationWork)`, raising `TitleAbandoned` for titles and `SummaryTaskFinished` otherwise. This is identical behaviour for every path reachable today; the step-0 test guards the summary path. | medium / S | `ExitStopTests`, archive and delete flow tests, desktop `ExitTests` |

Not recommended: splitting groups C, D and E into separate classes, or moving the queue queries out. They share one lock and the `Completion` and `IsLive` ordering rules, and the remaining ~420 lines are one responsibility (scheduling plus its lifecycle).

Using `partial` class files instead of real types would shorten the file but keep every field shared. That buys nothing for J, I or the runners, so use real types.

## 6. Ratings

| Item | Priority | Effort |
|---|---|---|
| Kept writes → `TerminalWrites` | high | S–M |
| Request plumbing + answer runner (steps 4–5) | high | M |
| Scheduler characterization tests (step 0) | high | S |
| `ContextBudgets` | medium | S |
| Background task runners + skeleton (step 6) | medium | M |
| Cancel-queued dedupe (step 7) | medium | S |
| Type move + stale doc reference (step 1) | medium | S |
| Splitting the scheduler core | do not | — |

All paths are under `~/Developer/Projects/mindweave.worktrees/claude-auto/`. Main files: `src/Mindweave.Application/Conversations/GenerationCoordinator.cs`, `tests/Mindweave.Infrastructure.Tests/Chat/ChatHarness.cs`, `tests/Mindweave.TestHost/Program.cs`, `tests/Mindweave.Architecture.Tests/SeamTests.cs`, `src/Mindweave.Desktop/CompositionRoot.cs`.