# End-to-end lifecycle journeys: archive/delete with work in flight, exit with pending saves, kill and startup recovery — grade B

The lifecycle journeys are unusually well defended for a codebase this size: deletes are single transactions that recompute scope and busy state inside the write, late writes are guarded by EXISTS or zero-row updates rather than relying on foreign-key errors, constraint errors are deliberately not classified as storage failures, exit prompts precede every irreversible step, and startup recovery is one atomic statement pair with real process-kill tests behind it. I traced all five journeys and found no data-loss or corruption path that is not already reported elsewhere. What keeps this from an A: (1) non-answer tasks do not keep their terminal write when storage refuses it, so a summary can stay 'running' in the database with no in-process reconciler until restart; (2) every OS logout or shutdown is vetoed, even with nothing unsaved; (3) several waits in the exit sequence are unbounded under an overlay with no button; (4) workspace deletion and summary-draft ownership each skip a notification the conversation-delete path sends. All of this is from reading code; nothing was built or run.

**Strengths**

- Delete is atomic and re-validated inside the write transaction: SqliteArchiveStore.DeleteAsync (lines 94-153) recomputes the scope, refuses on Busy or ScopeChanged, then clears every RESTRICT reference in one transaction; SqliteWorkspaceStore.DeleteAsync (87-133) does the same. TransactionKillTests kills a real process inside conversation, cascade and workspace deletes, archive and restore.
- Late writes to deleted rows are harmless by construction, not by luck: SqliteReadingStateStore.SaveDraftAsync and SavePositionAsync insert only WHERE EXISTS (lines 29-31, 61-63), SqliteNetworkStore.SaveLayoutAsync likewise (44-48), FinishTask and MarkRunning are zero-row updates, and StorageFailures.Classify (12-20) keeps constraint errors out of the 'storage paused' path. GenerationRequests.RecordAsync (132-144) swallows trace-write failures so an ai_request foreign-key miss after a delete never pauses storage.
- Cancel-then-delete ordering is thought through: ConversationDeletionService cancels titles and naming before the transaction and again after it (lines 93, 115); ArchiveAsync cancels queued titles in the archive's own commit (SqliteArchiveStore 41-47) and AddTaskAsync refuses a title for a hidden or missing conversation (SqliteConversationStore 124-145). WorkspaceNameCache keeps a per-run 'forgotten' set so a name request finishing after its workspace was deleted cannot resurrect the key (lines 44-47, 80-94).
- Exit ordering matches architecture §12.3: the layout prompt and the task prompt both come before the first irreversible step, 'don't save' is only recorded until commit (ExitCoordinator 164-168, 211-233), failures land in a retry/return state rather than a stuck spinner (235-285), and TryShutdown is used so the final shutdown passes both hooks. AppHost.Run closes the database only after the UI loop returns, and does so even when the loop throws (lines 37-49).
- Kill-and-restart recovery is simple and complete for the row states that exist: RecoverUnfinishedAsync (SqliteConversationStore 235-247) marks every pending/streaming message and every queued/running task interrupted in one transaction, before any work is admitted, and also runs after a recovery-page restore and an in-app restore (StartupCoordinator 62-76). ai_request has no 'running' state, summary versions are written only on success in the same transaction as the task's completion, and the UI has distinct text for interrupted answers and summaries.
- Navigation after delete is handled centrally: Workbench.Forget removes matching pages from every stack of every workspace and disposes them, falling back to the parent conversation for a deleted branch (Workbench 146-189, ConversationRemoval 14-23); RemoveWorkspace disposes every page of the deleted workspace and moves to the store-chosen next one or to onboarding (197-227). The delete transactions also null out workspace_location.content_key so a restart does not reopen a deleted page.

**Not examined**

- Nothing was built or run; every finding is from reading code. The re-entrant shutdown finding and the OS-logout finding in particular depend on Avalonia 12 lifetime behaviour I did not execute.
- Real Windows behaviour for WM_QUERYENDSESSION / forced shutdown, and real macOS logout behaviour.
- The in-app restore file swap and RestoreInPlaceCore internals (reported elsewhere); I only traced how the exit sequence hands over to it.
- SqliteWriteQueue internals and SqliteDatabase backup code (VACUUM INTO, pruning).
- ContextCompressor's own writes during a kill; I only checked that context_summary rows have no in-progress state.
- SummaryNaming apply/guard logic and NetworkSession load/save races (reported elsewhere).
- The view layer (AXAML, code-behind) apart from MainWindow.OnClosing; whether the exit overlay really blocks all input was taken from the view-model comments.
- MessageListReconciler, Composer and NewConversationViewModel beyond their interaction with delete/archive.
- The test suites were only listed by name and test title, not read for assertion quality.
- Baseline scratchpad outputs (build, test, vuln, outdated, format) were not consulted.

### gap-lifecycle-journey-archive-delete-exit-recovery-1. Summary, naming and title tasks do not keep their terminal write when storage refuses it: the task row stays 'queued' or 'running' until restart and locks the conversation

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Application/Conversations/TaskRunners.cs:81`; effort S

**Evidence.** AnswerRunner handles a failed MarkRunning by finishing the answer as Failed with retryOnFailure: true (AnswerRunner.cs:44-49) and keeps every answer terminal write (160, 169-171). TaskRunners does not: RunSummaryAsync returns when MarkRunningAsync is not Applied (81-84) and writes Stopped / Failed terminal states without retryOnFailure (lines 89, 104, 137, 141, 145, 149); only the 'version could not be stored' branch keeps its write (126). Line 149 is the StorageAdmissionRefused branch, i.e. the write is attempted exactly when storage is already paused. GenerationCoordinator.CancelQueuedTaskAsync (433) is also unretried. TerminalWrites.SaveAsync without retry only calls _health.Report (59-62). StorageHealth.RecoverAsync (261-326) retries registered kept writes and probes; nothing reconciles task rows. The row decides everywhere: ConversationSummaryPanel.RunningSummary (118), SummaryService.SummarizeAsync refusal (161-164), SqliteArchive.UnfinishedUserWork (346-357) used by archive, delete and workspace delete. StopTaskAsync (GenerationCoordinator 262-285) finds nothing queued or running and does nothing.

**Failure scenario.** Disk fills while a summary is running or about to start. Either MarkRunning fails (row stays 'queued', work dropped) or the request is refused/stopped/fails and the terminal write fails too (row stays 'running'). The user frees space; storage recovers. The conversation page still shows the summary as in progress, its Stop button does nothing, 'summarize' is refused as already in progress, and archiving or deleting that conversation, any ancestor of it, or its workspace is refused as busy. Nothing tells the user that only a restart clears it (RecoverUnfinishedAsync at next start).

**Recommendation.** Give non-answer tasks the same treatment as answers: on a failed MarkRunning write a kept Failed terminal state, and pass retryOnFailure: true for every FinishTaskAsync in TaskRunners and in CancelQueuedTaskAsync. Add a test with FlakyStore: storage refuses during a running summary, recovers, and the task row must end terminal without a restart.

**Skeptic: confirmed, medium.** I could not refute it; every cited line says what the finding claims, and I found no guard, reconciler, test or design decision that covers the gap.

**What the code does**
- `RunSummaryAsync` returns on any non-Applied `MarkRunningAsync` (TaskRunners.cs:81-84). It does not distinguish NotApplied (cancelled) from Failed (storage), so a storage failure leaves the row `queued` and drops the in-memory work. `AnswerRunner` does distinguish them and writes a kept Failed state (AnswerRunner.cs:37-49).
- Every summary terminal write except line 126 is called without `retryOnFailure` (89, 104, 132, 137, 141, 145, 149). The same holds for naming (170, 181, 186, 190, 194), titles (40, 57, 62, 68, 72) and `CancelQueuedTaskAsync` (GenerationCoordinator.cs:433).
- Without `retryOnFailure`, `TerminalWrites.SaveAsync` only calls `_health.Report(ex)` and returns Failed (TerminalWrites.cs:53-64). Nothing is queued for retry.
- `StorageHealth.RecoverAsync` runs only the registered retries plus a probe write (StorageHealth.cs:286-291). The registered retries are `TerminalWrites.FlushAsync`, `ReadingStateService` and `SummaryDrafts`; none touches task rows.
- `RecoverUnfinishedAsync` is called only from `StartupCoordinator.cs:72`, so only a restart clears a stuck row.
- Line 149 is the worst-placed write: `StorageAdmissionRefusedException` is thrown only when `_health.TryAdmit()` fails (GenerationRequests.cs:74-77), so the unretried write is attempted exactly while storage is paused.

**Why the stuck row matters**
- The summary panel shows "in progress" from the stored row (ConversationSummary … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. **The lock applies to summary tasks only.** `UnfinishedUserWork`, `LatestTaskAsync` and the summary panel all filter on `kind = 'summary'`. A stuck title or naming row has the same missing-retry defect but does not block archive, delete or summarize, and does not show in the panel. It is a stale row until restart (and, for a title, possibly no re-request this session). The finding's title overstates this by saying title and naming rows "lock the conversation".

2. **Disk-full is the weaker trigger.** The project's own notes say small updates such as a failed terminal state usually still succeed on a full disk (s6-wrapup.md:174-175). A writer lock held by another program is the more credible trigger: every write fails, admission is refused, and the unretried write at line 149 fails too.

3. **The unretried failure is not silent to storage health.** `_health.Report` does pause requests … (cut; full text in the workflow journal)

### gap-lifecycle-journey-archive-delete-exit-recovery-2. Every OS logout, restart or shutdown is vetoed, even when nothing is unsaved; there is no handler for termination that cannot be cancelled

- Final: **medium** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/ExitCoordinator.cs:104`; effort M

**Evidence.** InterceptClose returns true for every stage except Releasing, including Idle with no prompts needed (104-117). App.OnShutdownRequested sets e.Cancel from it (App.axaml.cs:57-62) and its comment says the system's shutdown request arrives there. Avalonia documents that cancelling ShutdownRequested blocks OS shutdown on Windows and macOS. The coordinator then saves asynchronously and calls TryShutdown itself. A grep of src for PosixSignal, SIGTERM, ProcessExit and CloseReason finds nothing, so a forced end skips ReadingStateService's 500 ms and SummaryDrafts' 1 s debounced writes. ExitScenario.cs (tests) only drives the handler with a hand-made ShutdownRequestedEventArgs.

**Failure scenario.** On macOS the user chooses Restart or Log Out with Mindweave open and idle. The request is cancelled, the system reports that Mindweave interrupted it, and a moment later Mindweave quits on its own; the user has to start the restart again (or walks away and the machine never restarts). On Windows the 'app is preventing shutdown' screen appears; 'shut down anyway' kills the process mid-sequence, which the next start treats as a crash (answers marked interrupted, text since the last 1 s checkpoint lost).

**Recommendation.** Decide the OS-initiated case explicitly: when no layout is dirty and no user work is unfinished, let the request through and do a bounded synchronous flush (drafts, reading state, kept writes, checkpoint) before returning; veto only when a prompt is genuinely needed. Register a ProcessExit / SIGTERM hook that runs the same bounded flush. Verify on a real macOS logout and a Windows sign-out.

**Skeptic: partially confirmed, medium.** I could not refute the core claim; the code does what the finding says, and no guard or design decision covers it.

**What holds**
- `InterceptClose()` returns true for every stage except `Releasing`, including `Idle` with no dirty layout and no unfinished work. In that case it starts `CommitAsync` asynchronously and still vetoes the request.
- `App.OnShutdownRequested` does `e.Cancel |= root.Exit.InterceptClose()` without looking at the event args. Its own comment says the system's shutdown request arrives there.
- The Avalonia 12.1.4 XML docs shipped with the pinned package state that cancelling `ShutdownRequested` blocks OS shutdown on Windows, and that macOS behaves the same. The Win32 assembly references `WM_QUERYENDSESSION` and the native dylib implements `applicationShouldTerminate:`, so the OS path does reach this handler.
- `ShutdownRequestedEventArgs.IsOSShutdown` exists in 12.1.4 and is never read anywhere in `src`. The code has the information needed to treat the OS case differently and ignores it.
- There is no `PosixSignal`, `SIGTERM`, `ProcessExit` or `CloseReason` handling in `src`. The only process-level hook is `AppDomain.UnhandledException` for crash logging.
- The debounce values are as cited: reading state 500 ms, summary drafts 1 s, generation checkpoint 1 s.
- The test helper only builds a `ShutdownRequestedEventArgs` by hand and calls the static handler; nothing exercises the platform lifetime.

**Not an accepted decision**
- Architecture §12.3 defines the exit order but says nothing about OS-initiated shutdown.
- `docs/design/s6-wrapup.md:56` and `d … (cut; full text in the workflow journal)

*Corrections to the finding.* - The user-visible outcomes are inferred from Avalonia's documentation and platform conventions, not observed. Nothing was run on a real macOS logout or Windows sign-out, and the project's own docs list this as unverified.
- The Windows scenario is overstated. The idle app saves and exits within about a second, so the "preventing shutdown" screen may not appear or may clear by itself. "Shut down anyway kills the process mid-sequence" needs the user to act inside that window, or a long stop wait (up to the 5 s `StopWait`) with an answer generating.
- "Answers marked interrupted, text since the last 1 s checkpoint lost" applies only when an answer is generating at the moment of the kill, not to the idle case in the title.
- The missing `ProcessExit`/`SIGTERM` hook is a separate low-severity point: the loss is bounded to 500 ms–1 s of debounced state, and `s6-wrapup.md:44` already accepts t … (cut; full text in the workflow journal)

### gap-lifecycle-journey-archive-delete-exit-recovery-3. The exit sequence has unbounded waits under an overlay that offers nothing to press, and queued work is cancelled before running streams are stopped

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Conversations/GenerationCoordinator.cs:346`; effort S

**Evidence.** StopForExitAsync first awaits a database write per queued item, sequentially (346-357), and only then cancels the running requests (359-362); the 'wait' bound covers only the final WhenAny (364-365). ExitCoordinator.SaveEverythingAsync awaits exports.WhenIdleAsync() first (330) with no timeout; SummaryViewModel calls ExportAsync with CancellationToken.None (line 339). ExitCoordinator.IsBusy is documented as 'the overlay shows progress and offers nothing to press' (92-93), and InterceptClose ignores further close requests in these stages. ExitCoordinator also discards StopForExitAsync's result (240-241).

**Failure scenario.** The user confirms exit with one answer streaming and several questions queued while the single writer is occupied (a pre-delete full-database backup, or a slow disk). 'Stopping' lasts as long as the writer is busy; during that time the running answer keeps streaming and billing because it has not been cancelled yet. Separately, an export writing to a stalled network share keeps the app on 'saving' indefinitely; the only way out is force-quit.

**Recommendation.** Cancel running work first, then write the queued cancellations, and put the whole stop step under the bounded wait. Give the export wait a timeout (or cancel the export) and let a long Flushing stage fall through to the existing Failed state so retry/return appear.

### gap-lifecycle-journey-archive-delete-exit-recovery-4. SummaryDrafts is never told that a summary node was deleted; a draft left unsaved or in conflict for it makes every later exit fail

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Summaries/SummaryDrafts.cs:154`; effort S

**Evidence.** Records live in _records for the session and are only cleared by CommitAsync / DiscardAsync, which need the summary page. Nothing subscribes to conversation or workspace deletion (grep for SummaryDrafts and Deleted += shows only ConversationMarksService listening). For a deleted node SaveDraftAsync calls RequireNode, which throws SummaryRefusedException(Changed) (SqliteSummaryStore.cs:215, 475-476); WriteAsync turns that into Status = Conflict and returns false (372-386). FlushAsync returns false while any record is Dirty, Failed, Deleting or Conflict (168-171), and ExitCoordinator.SaveEverythingAsync requires it to be true (332, 336).

**Failure scenario.** A summary has a draft in Conflict (two pages of the same summary, or newer input arriving during a commit), or the user's last keystroke in the summary editor is under a second old, and the source conversation or its workspace is then deleted. From then on every exit ends in 'save failed' with retry and return; retry can never succeed and the page that could discard the draft no longer exists, so the app can only be force-quit. The trigger is narrow, the consequence is not.

**Recommendation.** Have the delete paths tell SummaryDrafts which nodes went (drop their records and resolve any obligation), for both ConversationDeletionService and WorkspaceDeletionService. Treat 'node missing' in WriteAsync as 'nothing left to save' rather than as a conflict.

### gap-lifecycle-journey-archive-delete-exit-recovery-5. Workspace deletion skips the per-conversation notifications that conversation deletion sends

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Workspaces/WorkspaceDeletion.cs:121`; effort S

**Evidence.** ConversationDeletionService calls conversations.NotifyDeleted(scope) (ConversationDeletion.cs:116), which forgets title marks and raises Deleted and VisibilityChanged per conversation (ConversationService.cs:454-468). WorkspaceDeletionService only cancels background work and forgets the name cache (121-127); it has no ConversationService dependency. ConversationMarksService removes an unread mark only on Deleted or when the conversation is shown (lines 54, 120-129) and persists the set under 'conversations.unread'; its class comment says 'a deleted conversation leaves it'. Workspace deletion also cancels titles and naming only after the commit (121), where conversation deletion cancels before and after (93, 115).

**Failure scenario.** A workspace holding conversations with unread 'new answer' marks is deleted. Their ids stay in the in-memory set and in the stored preference for good (they are filtered at display time, so nothing shows, but the stored set only grows). A naming task still running at commit time applies its output to nodes that no longer exist and is logged as 'Generation task crashed' at error level.

**Recommendation.** Return the deleted conversation ids to ConversationService (or raise the same Deleted events from WorkspaceDeletionService) so marks, title marks and draft owners are cleaned the same way on both paths; cancel background work before the workspace transaction as the conversation path does.

### gap-lifecycle-journey-archive-delete-exit-recovery-6. Deleting the conversation being viewed discards the whole Explore back history, including unrelated pages

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Navigation/Workbench.cs:168`; effort S

**Evidence.** In Forget, when the shown page was removed the slot is forced to the Explore section and, with no fallback route, every page above the Explore home is popped and disposed (168-186). ConversationRemoval supplies a fallback only for a deleted branch whose parent survives (ConversationRemoval.cs:18-22); a root conversation or a summary page gets none.

**Failure scenario.** The user opens conversation A, then B, then root conversation C, and deletes C from its page. They land on the Explore home with Back disabled; A and B are gone from history although neither was deleted. If they were on C's summary page in the Network section, they are also moved out of Network to the Explore home.

**Recommendation.** After removing the forgotten pages, show whatever is now on top of the stack the user was in, and fall back to the section home only when that stack is empty.

### gap-lifecycle-journey-archive-delete-exit-recovery-7. Closing the window before the database is open runs the whole exit synchronously and calls TryShutdown from inside the Closing handler, which then cancels the close

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Views/MainWindow.axaml.cs:135`; effort S

**Evidence.** On the recovery page or during startup there is no writer, so every await in the chain completes synchronously: no dirty layout, no work, StopForExitAsync with nothing queued or running, SaveEverythingAsync with no active workspace, and DrainAsync returning at once when _writer is null (SqliteDatabase.cs:293-296). ExitCoordinator therefore reaches Stage = Releasing and shell.ExitApplication() (283-284) inside InterceptClose, which still returns true (116); OnClosing then sets e.Cancel = true (135-138) after the nested TryShutdown has already closed the window. The same nesting happens for Cmd+Q through OnShutdownRequested. The existing test (RecoveryTests 'Exit_on_the_recovery_page_asks_the_shell_to_quit') uses the page's own button and a fake shell, not the window's close path.

**Failure scenario.** The user sees the 'database cannot be read' page, or a long migration on the startup page, and closes the window with the title-bar button. Shutdown is re-entered from within the window's own Closing callback and the outer close is reported as cancelled. I could not run this; whether Avalonia 12 tolerates it on both platforms is unverified.

**Recommendation.** Make InterceptClose return false when the sequence it started has already reached Releasing, or post the commit step to the dispatcher so shutdown is never requested from inside a close hook. Add a headless test that closes the real window on the recovery page.

### gap-lifecycle-journey-archive-delete-exit-recovery-8. A request in flight when the process is killed leaves no ai_request row, so its usage never reaches the statistics page

- Final: **info** (not verified); auditor said info
- Where: `src/Mindweave.Application/Conversations/AnswerRunner.cs:151`; effort S

**Evidence.** The trace row is written only after the terminal write (AnswerRunner.cs:150-151; GenerationRequests.RequestCompleteTextAsync 100-126), and ai_request.outcome allows only completed, truncated, stopped, failed (SchemaMigrations v10). RecoverUnfinishedAsync touches message and generation_task only (SqliteConversationStore.cs:239-244). This is consistent, not a reconciliation bug.

**Failure scenario.** The app is killed (or the OS forces it down) during a long answer or compression. The provider bills the prompt and whatever was generated; the statistics page shows nothing for that request, and the interrupted task has no trace row for diagnosis.

**Recommendation.** If usage completeness matters, have recovery insert a 'failed/interrupted' ai_request for each task it marks interrupted (timing from started_at, tokens unknown), or state on the statistics page that interrupted requests are not counted.

